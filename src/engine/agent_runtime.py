"""
src/engine/agent_runtime.py
─────────────────────────────────
AgentRuntime owns the per-agent step logic.

One AgentRuntime is created per simulation run (not per agent).
It receives all agents and steps them via the scheduler.
"""
from __future__ import annotations

from typing import Any

from src.contracts.action import ActionResult, parse_intent
from src.contracts.brain import BrainContext
from src.contracts.world import AgentView, World, VisibilityWorld
from src.engine.agent import Agent
from src.engine.event_bus import (
    EventBus, Event,
    ACTION_COMPLETED, ACTION_FAILED, AGENT_STEP_ERROR,
)
from src.engine.registry import ActionRegistry
from src.engine.obs_registry import ObservationRegistry


# Module-level constants to avoid per-call literal allocation on hot path.
_EMPTY_DICT: dict[str, Any] = {}


class AgentRuntime:
    """
    Drives a single agent through one tick step:
      1. Tick state extension
      2. Build observation from world
      3. Call brain.decide()
      4. Dispatch through registry
      5. Apply mutations to agent and world
      6. Store memory
      7. Emit events
    """

    def __init__(
        self,
        registry:     ActionRegistry,
        world:        World,
        bus:          EventBus,
        obs_registry: ObservationRegistry | None = None,
    ):
        self.registry     = registry
        self.world        = world
        self.bus          = bus
        self.obs_registry = obs_registry
        # Cache the VisibilityWorld capability check — isinstance is fast but
        # we call it N×ticks times. Cache once on construction.
        self._world_has_visibility = isinstance(world, VisibilityWorld)

    async def step(self, agent: Agent) -> tuple:
        """Returns (intent, ActionResult)."""
        tick = self.world.current_tick

        # 1. Tick simulation-specific state and snapshot it ONCE.
        #    The snapshot is reused for AgentView.ext, observation["state_ext"],
        #    and the memory-store outcome string.
        state_ext = agent.state_ext
        if state_ext is not None:
            state_ext.tick()
            state_snap   = state_ext.snapshot()
            state_advice = state_ext.urgent_advice()
            state_str    = state_ext.to_prompt_str()
        else:
            state_snap   = _EMPTY_DICT
            state_advice = ""
            state_str    = ""

        # 2. Single defensive copy of inventory — shared between AgentView and
        #    observation["inventory"]. Both downstream readers treat it as
        #    read-only; sharing the reference saves one dict copy per agent.
        inventory_view = dict(agent.inventory)

        # 3. Build observation by mutating world_obs in place.
        #    HostedWorld.observe() already returns a fresh dict; local-authoritative
        #    worlds (medieval, corporate) return freshly computed dicts. So mutating
        #    is safe and avoids the {**world_obs, ...} double-copy.
        observation = self._build_observation(
            agent          = agent,
            state_snap     = state_snap,
            state_advice   = state_advice,
            state_str      = state_str,
            inventory_view = inventory_view,
        )

        # 4. Brain decides — receives typed agent view, schemas, and context
        view = AgentView(
            id        = agent.id,
            name      = agent.name,
            inventory = inventory_view,
            ext       = state_snap,
        )
        obs_schemas = (
            self.obs_registry.schemas_for(agent.capabilities)
            if self.obs_registry is not None
            else []
        )
        context = BrainContext(
            tick                = tick,
            memory              = agent.memory.recall(),
            metadata            = self.world.metadata,
            observation_schemas = obs_schemas,
            emit                = self.bus.emit,
        )
        intent = await agent.brain.decide(
            agent       = view,
            observation = observation,
            actions     = self.registry.schemas_for(agent.capabilities),
            context     = context,
        )

        # 5. Validate then dispatch
        validation_errors = self.registry.validate_intent(intent, agent.capabilities)
        if validation_errors:
            error_text = "; ".join(e.message for e in validation_errors)
            result = ActionResult(
                success      = False,
                outcome_text = f"action '{intent.action}' rejected: {error_text}",
            )
        else:
            result = self.registry.dispatch(intent, view, self.world)

        # 6a. Apply state_ext mutations (ext dict only — not inventory)
        mutations = result.mutations
        if state_ext is not None and mutations.ext:
            state_ext.apply_mutations(mutations.ext)

        # 6b. Apply inventory mutations (int deltas only)
        if mutations.inventory:
            agent_inv = agent.inventory
            for item, delta in mutations.inventory.items():
                if isinstance(delta, int) and isinstance(item, str):
                    agent_inv[item] = max(0, agent_inv.get(item, 0) + delta)

        # 6c. Let world apply cross-agent effects
        self.world.apply(agent.id, result)

        # 7. Memory — reuse the state snapshot rather than re-querying state_ext
        loc = observation.get("location", "?")
        if state_snap:
            # Inline join to avoid generator overhead for small dicts
            parts = [str(loc)]
            for k, v in state_snap.items():
                parts.append(f"{k}={v}")
            obs_str = " ".join(parts)
        else:
            obs_str = str(loc)

        incoming = observation.get("incoming_messages")
        if incoming and isinstance(incoming, list):
            heard_parts = []
            for m in incoming:
                if isinstance(m, dict):
                    name = m.get("from_name") or m.get("from", "?")
                    text = str(m.get("text", ""))[:60]
                    heard_parts.append(f'{name}: "{text}"')
            if heard_parts:
                obs_str = obs_str + " | heard: " + "; ".join(heard_parts)

        agent.memory.store(tick, obs_str, intent, result.outcome_text)

        # 8. Emit event
        event_type = ACTION_COMPLETED if result.success else ACTION_FAILED
        self.bus.emit(Event(
            type     = event_type,
            tick     = tick,
            agent_id = agent.id,
            data     = {
                "agent_name":   agent.name,
                "action":       intent.action,
                "target":       intent.target,
                "reasoning":    intent.reasoning,
                "outcome":      result.outcome_text,
                "side_effects": result.side_effects,
                "location":     loc,
            },
        ))

        return intent, result

    def _build_observation(
        self,
        agent:          Agent,
        state_snap:     dict[str, Any],
        state_advice:   str,
        state_str:      str,
        inventory_view: dict[str, Any],
    ) -> dict[str, Any]:
        """
        Assembles the agent's observation for this tick.

        Layer order (later layers win on key conflict):
          1. ObservationRegistry providers  — domain / capability-filtered additions
          2. world.observe()               — authoritative host / world data
          3. VisibilityWorld.nearby_agents — if world doesn't supply it
          4. Engine-injected identity      — agent_id, inventory, state_ext, etc.

        Mutates the world_obs dict in place — worlds are expected to return
        freshly-allocated dicts (HostedWorld already copies; local worlds compute
        per-call). This avoids the {**spread, ...} double-copy that was on the
        hot path for every agent every tick.
        """
        # 1. Collect registry provider slices (lowest priority — fills gaps)
        if self.obs_registry is not None:
            registry_obs = self.obs_registry.collect(agent.id, agent.capabilities, self.world)
        else:
            registry_obs = _EMPTY_DICT

        world_obs = self.world.observe(agent.id)

        # Merge registry into world_obs: only set keys world didn't provide
        for k, v in registry_obs.items():
            if k not in world_obs:
                world_obs[k] = v

        if "nearby_agents" not in world_obs:
            if self._world_has_visibility:
                nearby = self.world.get_nearby_agents(agent.id)
                # Materialize each AgentView lazily; common case is empty list.
                if nearby:
                    world_obs["nearby_agents"] = [
                        {"id": a.id, "name": a.name, "inventory": a.inventory, "ext": a.ext}
                        for a in nearby
                    ]
                else:
                    world_obs["nearby_agents"] = []
            else:
                world_obs["nearby_agents"] = []

        world_obs["agent_id"]     = agent.id
        world_obs["agent_name"]   = agent.name
        world_obs["inventory"]    = inventory_view
        world_obs["state_ext"]    = state_snap
        world_obs["state_advice"] = state_advice
        world_obs["state_str"]    = state_str
        return world_obs
