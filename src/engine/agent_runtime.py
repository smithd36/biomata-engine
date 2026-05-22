"""
src/engine/agent_runtime.py
─────────────────────────────────────
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

    async def step(self, agent: Agent) -> tuple:
        """Returns (intent, ActionResult)."""
        tick = self.world.current_tick

        # 1. Tick simulation-specific state
        if agent.state_ext:
            agent.state_ext.tick()

        # 2. Build observation (world perception only)
        observation = self._build_observation(agent)

        # 3. Brain decides — receives typed agent view, schemas, and context
        view    = AgentView.from_agent(agent)
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

        # 4. Dispatch
        result = self.registry.dispatch(intent, view, self.world)

        # 5a. Apply state_ext mutations
        if agent.state_ext and result.state_mutations:
            agent.state_ext.apply_mutations(result.state_mutations)

        # 5b. Apply inventory mutations (int deltas only)
        for item, delta in result.state_mutations.get("inventory", {}).items():
            if isinstance(delta, int) and isinstance(item, str):
                agent.inventory[item] = max(0, agent.inventory.get(item, 0) + delta)

        # 5c. Let world apply cross-agent effects
        self.world.apply(agent.id, result)

        # 6. Memory
        loc     = observation.get("location", "?")
        obs_str = str(loc)
        if agent.state_ext:
            snap    = agent.state_ext.snapshot()
            obs_str += " " + " ".join(f"{k}={v}" for k, v in snap.items())
        agent.memory.store(tick, obs_str, intent, result.outcome_text)

        # 7. Emit event
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
                "location":     observation.get("location", "?"),
            },
        ))

        return intent, result

    def _build_observation(self, agent: Agent) -> dict[str, Any]:
        """
        Pure world perception for this agent.
        Agent identity (name, inventory, ext) is passed to brain separately via AgentView.
        """
        world_obs = self.world.observe(agent.id)
        nearby: list[AgentView] = (
            self.world.get_nearby_agents(agent.id)
            if isinstance(self.world, VisibilityWorld)
            else []
        )

        return {
            **world_obs,
            "nearby_agents": [
                {"id": a.id, "name": a.name, "inventory": a.inventory, "ext": a.ext}
                for a in nearby
            ],
            # Kept for backward compatibility with existing Brain implementations:
            "agent_id":   agent.id,
            "agent_name": agent.name,
            "inventory":  dict(agent.inventory),
            "state_ext":  agent.state_ext.snapshot() if agent.state_ext else {},
            "state_advice": agent.state_ext.urgent_advice() if agent.state_ext else "",
            "state_str":    agent.state_ext.to_prompt_str() if agent.state_ext else "",
        }
