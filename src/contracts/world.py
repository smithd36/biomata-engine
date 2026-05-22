"""
src/contracts/world.py
───────────────────────────────
World and WorldContext are the minimal engine-facing contracts.
Neither implies a spatial domain.

Optional capability protocols let handlers and the engine declare exactly
what they require — without forcing every world to implement all methods:

  VisibilityWorld   — world can enumerate agents visible to an agent
                      (used by engine to build nearby_agents observation)
  SpatialWorld      — world supports proximity / adjacency queries
                      (used by handlers like Speak, Trade, Attack, Gossip)
  PlaceableWorld    — world supports placing agents at init time
                      (used by the config loader for YAML `position:` fields)

A world implementation satisfies whichever protocols it implements.
MedievalWorld and CorporateWorld both implement all three — the engine and
loader check capability at runtime via isinstance.
"""
from __future__ import annotations

import random as _random
from dataclasses import dataclass, field
from typing import Any, Protocol, runtime_checkable


@dataclass
class AgentView:
    """Read-only snapshot of an agent. Created fresh each tick."""
    id:        str
    name:      str
    inventory: dict[str, Any]
    ext:       dict[str, Any] = field(default_factory=dict)

    @classmethod
    def from_agent(cls, agent: Any) -> "AgentView":
        ext = {}
        if hasattr(agent, "state_ext") and agent.state_ext is not None:
            ext = agent.state_ext.snapshot()
        return cls(
            id        = agent.id,
            name      = agent.name,
            inventory = dict(agent.inventory),
            ext       = ext,
        )


# ── Core engine contracts ─────────────────────────────────────────────────────

@runtime_checkable
class World(Protocol):
    """
    Minimal world interface the engine requires.
    Implementations can be grid-based, graph-based, continuous, or abstract.
    """

    def observe(self, agent_id: str) -> dict[str, Any]:
        """
        Return this agent's full perception of the world.
        Shape is entirely simulation-defined; injected into the brain prompt.
        """
        ...

    def apply(self, agent_id: str, result: "ActionResult") -> None:  # noqa: F821
        """Apply world-side effects of an ActionResult (cross-agent mutations, events, etc.)."""
        ...

    def tick(self) -> None:
        """Advance the world by one tick (season change, regen, decay, etc.)."""
        ...

    @property
    def current_tick(self) -> int: ...

    @property
    def metadata(self) -> dict[str, Any]:
        """Tick-level context injected into every brain prompt: tick, season, etc."""
        ...


@runtime_checkable
class WorldContext(Protocol):
    """
    Minimal handler context passed to ActionHandler.execute().
    Handlers read from this; they never mutate the world.
    Most worlds also satisfy the optional capability protocols below.
    """
    rng: _random.Random   # canonical RNG; injected by Simulation from its seeded instance
    def get_agent(self, agent_id: str) -> AgentView | None: ...
    def get_world_data(self) -> dict[str, Any]: ...


# ── Optional capability protocols ─────────────────────────────────────────────

@runtime_checkable
class SpatialWorld(WorldContext, Protocol):
    """
    World that supports proximity / adjacency queries.

    Satisfies WorldContext (get_agent, get_world_data) plus are_adjacent.
    Both spatial grids and org-graph worlds implement this — adjacency
    semantics are domain-defined (physical distance vs. org proximity).

    Handlers that need adjacency checks should use this as the context type,
    or use the are_adjacent() helper which degrades gracefully when absent.
    """
    def are_adjacent(self, id1: str, id2: str) -> bool: ...


@runtime_checkable
class VisibilityWorld(Protocol):
    """
    World that can enumerate agents visible or relevant to a given agent.
    Used by the engine to build the nearby_agents field in observations.
    Optional — if absent, nearby_agents is an empty list.
    """
    def get_nearby_agents(self, agent_id: str) -> list[AgentView]: ...


@runtime_checkable
class PlaceableWorld(Protocol):
    """
    World that supports placing agents during initialization.
    Used by the config loader when YAML `position:` fields are present.
    Optional — if absent, the loader skips placement entirely.
    """
    def place_agent(self, agent_id: str, **kwargs: Any) -> None: ...
