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
        registry: ActionRegistry,
        world:    World,
        bus:      EventBus,
    ):
        self.registry = registry
        self.world    = world
        self.bus      = bus
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
        context = BrainContext(
            tick     = tick,
            memory   = agent.memory.recall(),
            metadata = self.world.metadata,
            emit     = self.bus.emit,
        )
        intent = await agent.brain.decide(
            agent       = view,
            observation = observation,
            actions     = self.registry.schemas(),
            context     = context,
        )

        # 5. Dispatch
        result = self.registry.dispatch(intent, view, self.world)

        # 6a. Apply state_ext mutations
        state_mutations = result.state_mutations
        if state_ext is not None and state_mutations:
            state_ext.apply_mutations(state_mutations)

        # 6b. Apply inventory mutations (int deltas only)
        if state_mutations:
            inv_deltas = state_mutations.get("inventory")
            if inv_deltas:
                agent_inv = agent.inventory
                for item, delta in inv_deltas.items():
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
        Pure world perception merged with engine-injected identity fields.

        nearby_agents priority:
          1. If world.observe() already includes 'nearby_agents' (e.g. HostedWorld
             receiving host-provided visibility data), that list is used as-is.
          2. Otherwise, query VisibilityWorld.get_nearby_agents() if available.
          3. Default: empty list.

        Mutates the world_obs dict in place — worlds are expected to return
        freshly-allocated dicts (HostedWorld already copies; local worlds compute
        per-call). This avoids the {**spread, ...} double-copy that was on the
        hot path for every agent every tick.
        """
        world_obs = self.world.observe(agent.id)

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
