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
class POITraversal:
    """
    Optional traversal metadata for POIs that connect two areas (Phase 4).

    When ``is_portal`` is ``True`` the POI acts as a spatial transition point —
    a door, staircase, elevator, or any boundary crossing.  Unity handles the
    physical transition; Python's role is to surface the metadata in observations
    so the brain can reason about connectivity.

    ``connects_to`` is the ``id`` (Unity GameObject name) of the destination POI
    where the agent resumes after crossing.  Unity looks up the destination POI's
    ``exit`` anchor (from ``BiomataPOIData``) and places the agent there.

    Absent fields have safe defaults — existing ``POI`` objects that do not
    carry traversal data are unaffected.
    """
    is_portal:   bool       = False
    connects_to: str | None = None


def parse_poi_traversal(poi_obs: "dict[str, Any]") -> POITraversal | None:
    """
    Parse traversal metadata from a POI observation dict.

    Returns a ``POITraversal`` when the dict contains a ``"traversal"`` sub-dict
    with ``"is_portal": true``; returns ``None`` otherwise.

    Safe to call on any POI observation regardless of version — missing or
    non-dict ``"traversal"`` values produce ``None``, not an error.

    Example::

        for poi in observation.get("nearby_pois", []):
            traversal = parse_poi_traversal(poi)
            if traversal and traversal.is_portal:
                # brain knows this POI is a portal to traversal.connects_to
    """
    raw = poi_obs.get("traversal")
    if not isinstance(raw, dict):
        return None
    if not raw.get("is_portal"):
        return None
    return POITraversal(
        is_portal   = True,
        connects_to = raw.get("connects_to") or None,
    )


@dataclass
class POI:
    """
    A point of interest in the world.

    v1 (position-based): populate only ``id`` and ``position``.
    v2 (structured): additionally carry ``type``, ``anchors``, and ``traversal``.

    All v2 fields are optional with safe defaults so any existing code that
    constructs POI objects or dicts with only ``id`` + ``position`` continues
    to work without modification.  ``position`` is and will remain the primary
    location field — it is never removed.

    ``anchors`` maps anchor name → ``[x, y, z]`` coordinate list, e.g.::

        {"approach": [1.0, 0.0, 2.5], "entry": [1.0, 0.0, 1.0]}

    This matches the array format emitted by Unity's ``POIObservationProvider``
    in Phase 1+.

    ``traversal`` carries optional portal semantics (Phase 4+).  When present,
    the POI represents a spatial transition.  Use ``parse_poi_traversal(poi_dict)``
    to read it from an observation dict.
    """
    id:        str
    position:  dict[str, float]
    type:      str                              = "location"
    anchors:   dict[str, list[float]] | None   = None
    traversal: POITraversal | None             = None


def _ensure_3d(coords: list) -> list[float]:
    """Pad a 2-element [x, z] coordinate list to [x, 0.0, z]."""
    if len(coords) == 2:
        return [float(coords[0]), 0.0, float(coords[1])]
    return [float(coords[0]), float(coords[1]), float(coords[2])]


def resolve_poi_target(
    poi: dict[str, Any],
    anchor: str = "approach",
) -> list[float] | None:
    """
    .. deprecated::
        Do NOT use in movement paths.  Unity (``MoveActionHandler``) is the sole
        authority for resolving POI → world coordinates.  Python must emit a
        symbolic ``{"destination": poi_id, "anchor": anchor}`` command and let Unity
        resolve the anchor from the live scene Transform at execution time.

        This function is retained for non-movement callers (e.g. range checks,
        observation processing) that read POI position data without issuing
        engine_commands.

    Extract the best movement target from a POI observation dict.

    Preference order (Phase 2 anchor-aware):
      1. ``anchors[anchor]``  — named anchor position (Phase 1+ array format)
      2. ``position``         — v2 ``[x, y, z]`` array (Phase 1+)
      3. ``x`` / ``z``        — v1 flat keys (Phase 0 / legacy)

    Returns a ``[x, y, z]`` list, or ``None`` if no usable position data exists.
    """
    # Prefer named anchor (v2 array format from Phase 1 Unity emission)
    anchors_data = poi.get("anchors")
    if isinstance(anchors_data, dict):
        anchor_pos = anchors_data.get(anchor)
        if isinstance(anchor_pos, (list, tuple)) and len(anchor_pos) >= 2:
            return _ensure_3d(list(anchor_pos))

    # v2 position array
    pos = poi.get("position")
    if isinstance(pos, (list, tuple)) and len(pos) >= 2:
        return _ensure_3d(list(pos))

    # v1 flat keys — legacy fallback, always safe
    x = poi.get("x")
    z = poi.get("z")
    if x is not None and z is not None:
        y = poi.get("y", 0.0)
        return [float(x), float(y), float(z)]

    return None


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


@runtime_checkable
class ExternalWorld(World, Protocol):
    """
    World whose authoritative state is owned by an external host
    (game engine, physics server, remote simulation, test harness).

    Inverts the normal data-flow: instead of Python computing observations
    from local state, the host pushes observations in; instead of Python
    mutating world state, structured commands flow back out.

    Data flow per tick:
        Host → Python : push_observation(), push_metadata()   (before run_tick)
        Python → Host : collect_commands()                    (after  run_tick)

    The engine itself never calls push_* or collect_commands — those are for
    the integrator layer sitting between the host and the Simulation.

    isinstance(world, ExternalWorld) returns True when all three methods are
    present, which is the recommended way to detect external-world mode.
    """

    def push_observation(self, agent_id: str, observation: dict[str, Any]) -> None:
        """
        Inject the host's current perception for agent_id.
        Called once per agent before each sim.run_tick().

        The observation dict is returned verbatim from World.observe() and
        then merged with agent identity fields by AgentRuntime. Include
        'nearby_agents' here if the host tracks per-agent visibility:

            [{"id": "a2", "name": "Bob", "inventory": {}, "ext": {}}]

        The engine will use this list as-is rather than calling
        VisibilityWorld.get_nearby_agents().
        """
        ...

    def push_metadata(self, metadata: dict[str, Any]) -> None:
        """
        Inject world-level metadata for the upcoming tick.
        Replaces any previously pushed metadata in its entirety.
        Appears in World.metadata and therefore in BrainContext.metadata.
        """
        ...

    def collect_commands(self) -> list[dict[str, Any]]:
        """
        Drain and return engine_commands accumulated during the last tick.
        Each entry includes the originating agent_id plus handler-defined fields.

        Format (defined by ActionHandlers, not core engine):
            {"agent_id": "agent_001", "type": "navigate", "destination": {...}}

        Clears the internal buffer — intended to be called exactly once per tick.
        Returns [] when no commands were produced.
        """
        ...
