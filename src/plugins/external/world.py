"""
src/plugins/external/world.py
─────────────────────────────────────────
HostedWorld: a World implementation for externally-authoritative state.

The external host (game engine, physics server, remote simulation, test harness)
owns all world state. Python owns cognition and decision-making.

Data flows
──────────
  Host → Python   push_observation(agent_id, obs)   before each run_tick()
  Host → Python   push_metadata(metadata)            before each run_tick()
  Python → Host   collect_commands()                 after  each run_tick()

Usage
─────
    world = HostedWorld()
    sim   = Simulation(agents=[...], world=world, registry=registry)

    # Each tick, push state from the host:
    world.push_metadata({"time_of_day": "noon", "weather": "clear"})
    for agent_id, obs in host_observations.items():
        world.push_observation(agent_id, obs)

    # Run one cognition cycle:
    summary = await sim.run_tick()

    # Retrieve structured commands to relay back to the host:
    commands = world.collect_commands()
    # equivalently: summary.engine_commands()

Observation dict format
───────────────────────
Push whatever the Brain needs. The engine merges agent identity fields
(agent_id, agent_name, inventory, state_ext) on top automatically.
Include 'nearby_agents' if the host tracks per-agent visibility — the
engine will use it as-is instead of calling VisibilityWorld.get_nearby_agents():

    {
        "location":     "marketplace",
        "nearby_agents": [
            {"id": "a2", "name": "Bob", "inventory": {}, "ext": {}}
        ],
        ... any domain-specific fields ...
    }

engine_commands format
──────────────────────
Each ActionHandler defines the shape of its engine_commands entries.
HostedWorld wraps every command with the originating agent_id:

    {"agent_id": "agent_001", "type": "navigate", "destination": {...}}
    {"agent_id": "agent_002", "type": "set_animation", "clip": "idle"}

Handlers populate engine_commands in ActionResult; HostedWorld.apply()
collects them. TickSummary.engine_commands() is an alternative accessor
that reads directly from the returned ActionResults without going through
the world.

Handler pattern for externally-driven actions
─────────────────────────────────────────────
    class MoveHandler:
        def execute(self, agent, intent, context) -> ActionResult:
            direction = intent.parameters.get("direction", "north")
            return ActionResult(
                success=True,
                outcome_text=f"moving {direction}",
                engine_commands=[{"type": "navigate", "direction": direction}],
            )

Note: state_mutations still work normally — Python-side state (vitals,
inventory, memory, social) is applied by the engine as usual. Only
world-side spatial/physical effects need to go through engine_commands.
"""
from __future__ import annotations

import pickle
import random as _random_module
from typing import Any

from src.contracts.action import ActionResult
from src.contracts.world import AgentView


class HostedWorld:
    """
    World implementation for externally-authoritative state.

    Satisfies the World, WorldContext, and ExternalWorld protocols via
    structural duck-typing — no inheritance from abstract bases is needed.

    tick() advances only the internal counter; it never computes world
    state, regenerates resources, or modifies stored observations. State
    is driven entirely by push_observation() and push_metadata() calls
    made by the integrator before each run_tick().
    """

    def __init__(self) -> None:
        # Simulation overwrites this with its seeded canonical RNG instance.
        self.rng: _random_module.Random = _random_module.Random()

        self._tick:             int                        = 0
        self._observations:     dict[str, dict[str, Any]]  = {}
        self._metadata:         dict[str, Any]             = {}
        self._pending_commands: list[dict[str, Any]]       = []
        self._agents:           list[Any]                  = []

    # ── ExternalWorld interface ────────────────────────────────────────────────

    def push_observation(self, agent_id: str, observation: dict[str, Any]) -> None:
        """
        Receive the host's current perception for agent_id.
        Overwrites any prior observation for this agent.
        Call once per agent before each sim.run_tick().
        """
        self._observations[agent_id] = observation

    def push_metadata(self, metadata: dict[str, Any]) -> None:
        """
        Receive world-level metadata. Replaces the previous metadata entirely.
        Values surface in World.metadata and BrainContext.metadata.
        """
        self._metadata = dict(metadata)

    def collect_commands(self) -> list[dict[str, Any]]:
        """
        Drain and return engine_commands accumulated during the last tick.
        Each entry includes {"agent_id": ..., "type": ..., ...}.
        Clears the internal buffer — call exactly once per tick after run_tick().
        """
        commands, self._pending_commands = self._pending_commands, []
        return commands

    # ── World protocol ─────────────────────────────────────────────────────────

    def observe(self, agent_id: str) -> dict[str, Any]:
        """Return the most recently pushed observation for this agent."""
        return dict(self._observations.get(agent_id, {}))

    def apply(self, agent_id: str, result: ActionResult) -> None:
        """
        Collect engine_commands from the completed ActionResult.
        Cross-agent physical mutations are expressed as engine_commands for
        the host to execute; Python does not apply them locally here.
        Python-side effects (inventory, vitals, social) are applied by the
        engine before apply() is called, so state_mutations are already done.
        """
        for cmd in result.engine_commands:
            self._pending_commands.append({"agent_id": agent_id, **cmd})

    def tick(self) -> None:
        """Advance the internal tick counter. World state is driven by push_* calls."""
        self._tick += 1

    @property
    def current_tick(self) -> int:
        return self._tick

    @property
    def metadata(self) -> dict[str, Any]:
        """Return the most recently pushed world metadata."""
        return dict(self._metadata)

    # ── WorldContext protocol ──────────────────────────────────────────────────

    def get_agent(self, agent_id: str) -> AgentView | None:
        for a in self._agents:
            if a.id == agent_id:
                return AgentView.from_agent(a)
        return None

    def get_world_data(self) -> dict[str, Any]:
        """
        Return data available to ActionHandlers via context.get_world_data().
        Hosts can push structured handler data via push_metadata():
            world.push_metadata({"_manager_of": {...}, "_roles": {...}})
        Handlers that depend on world-specific keys must document those keys.
        """
        return dict(self._metadata)

    # ── Snapshotable ──────────────────────────────────────────────────────────

    def serialize(self) -> bytes:
        """
        Capture tick, pushed observations, metadata, and any pending commands.
        _agents is NOT serialized — Simulation.restore() re-registers agents.
        """
        return pickle.dumps({
            "tick":             self._tick,
            "observations":     dict(self._observations),
            "metadata":         dict(self._metadata),
            "pending_commands": list(self._pending_commands),
        })

    def restore(self, data: bytes) -> None:
        state                  = pickle.loads(data)
        self._tick             = state["tick"]
        self._observations     = state["observations"]
        self._metadata         = state["metadata"]
        self._pending_commands = state["pending_commands"]
        # self._agents and self.rng are re-bound by Simulation.restore()

    # ── Engine registration hook ───────────────────────────────────────────────

    def register_agents(self, agents: list[Any]) -> None:
        """Called by Simulation.__init__ when the full agent list is known."""
        self._agents = agents
