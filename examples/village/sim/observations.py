"""
examples/village/sim/observations.py
──────────────────────────────────────
ObservationProvider implementations for the village simulation.

Providers compute observation slices from HostedWorld's pushed state.
They access world._observations (the raw per-agent dicts from Unity) via
duck-typing — no import of HostedWorld so these stay world-agnostic.

Registered in obs_registry.py with ObservationSchemas and capability tags.
"""
from __future__ import annotations

import math
from typing import Any


# ── Village POI table ─────────────────────────────────────────────────────────
# Must stay in sync with the Unity scene and sim.yaml agent brain waypoints.

VILLAGE_POIS: list[tuple[str, float, float]] = [
    ("TownSquare", 0.0,   0.0),
    ("Well",       6.0,   4.0),
    ("Market",    14.0,   0.0),
    ("Tavern",   -10.0,   5.0),
    ("Farm",       2.0, -14.0),
    ("NorthGate",  0.0,  14.0),
    ("SouthGate",  0.0, -14.0),
]


def _agent_pos(obs: dict[str, Any]) -> tuple[float, float]:
    return float(obs.get("position_x", 0.0)), float(obs.get("position_z", 0.0))


# ── Providers ─────────────────────────────────────────────────────────────────

class TimeOfDayProvider:
    """
    Injects a tick-derived time-of-day label and the raw tick number.

    Produces:
      simulation_tick : int
      time_of_day     : "morning" | "afternoon" | "evening" | "night"
    """
    _PHASES = ("morning", "afternoon", "evening", "night")

    def observe(self, agent_id: str, capabilities: frozenset[str], world: Any) -> dict[str, Any]:
        tick  = world.current_tick
        phase = self._PHASES[(tick // 8) % len(self._PHASES)]
        return {"simulation_tick": tick, "time_of_day": phase}


class SelfStateProvider:
    """
    Computes the agent's current nearest POI and a compact position string.

    Produces:
      nearest_poi : str   e.g. "Market (2.4m)"
      position    : str   e.g. "(13.8, -0.2)"
    """

    def observe(self, agent_id: str, capabilities: frozenset[str], world: Any) -> dict[str, Any]:
        all_obs = getattr(world, "_observations", {})
        obs = all_obs.get(agent_id, {})
        ax, az = _agent_pos(obs)

        best_name, best_dist = "unknown", float("inf")
        for name, px, pz in VILLAGE_POIS:
            d = math.sqrt((ax - px) ** 2 + (az - pz) ** 2)
            if d < best_dist:
                best_dist, best_name = d, name

        return {
            "nearest_poi": f"{best_name} ({best_dist:.1f}m)",
            "position":    f"({ax:.1f}, {az:.1f})",
        }


class NearbyPoiProvider:
    """
    Lists the closest POIs and their distances.

    Produces:
      nearby_pois : list[{"name": str, "x": float, "z": float, "distance": float}]
    """

    def __init__(self, top_n: int = 4) -> None:
        self._top_n = top_n

    def observe(self, agent_id: str, capabilities: frozenset[str], world: Any) -> dict[str, Any]:
        all_obs = getattr(world, "_observations", {})
        obs = all_obs.get(agent_id, {})
        ax, az = _agent_pos(obs)

        with_dist = sorted(
            (math.sqrt((ax - px) ** 2 + (az - pz) ** 2), name, px, pz)
            for name, px, pz in VILLAGE_POIS
        )
        pois = [
            {"name": name, "x": px, "z": pz, "distance": round(d, 1)}
            for d, name, px, pz in with_dist[: self._top_n]
        ]
        return {"nearby_pois": pois}


class NearbyAgentsProvider:
    """
    Computes agents within sensor_radius using positions pushed from Unity.
    Fills in the standard 'nearby_agents' slot consumed by the LLM prompt
    builder, enriched with role information.

    Produces:
      nearby_agents : list[{"id", "name", "distance", "role", "inventory", "ext"}]
      nearby_count  : int
    """

    def __init__(self, sensor_radius: float = 8.0, roles: dict[str, str] | None = None) -> None:
        self._radius = sensor_radius
        self._roles  = roles or {}

    def observe(self, agent_id: str, capabilities: frozenset[str], world: Any) -> dict[str, Any]:
        all_obs = getattr(world, "_observations", {})
        obs = all_obs.get(agent_id, {})
        ax, az = _agent_pos(obs)

        nearby: list[dict[str, Any]] = []
        for other_id, other_obs in all_obs.items():
            if other_id == agent_id:
                continue
            bx, bz = _agent_pos(other_obs)
            dist = math.sqrt((ax - bx) ** 2 + (az - bz) ** 2)
            if dist <= self._radius:
                nearby.append({
                    "id":       other_id,
                    "name":     other_obs.get("agent_name", other_id),
                    "distance": round(dist, 1),
                    "role":     self._roles.get(other_id, "villager"),
                    "inventory": {},
                    "ext":      {},
                })

        nearby.sort(key=lambda x: x["distance"])
        return {"nearby_agents": nearby}


class SocialMemoryProvider:
    """
    Injects a text summary of the agent's current relationships.

    Reads from the canonical VillageRelationships (SocialSystem) instance.
    Agent names are resolved via VillageRelationships._names, populated by
    the engine calling add_agent() during simulation setup.

    Produces:
      social_relationships : str
    """

    def __init__(self, relationships: Any) -> None:
        self._rels = relationships

    def observe(self, agent_id: str, capabilities: frozenset[str], world: Any) -> dict[str, Any]:
        summary = self._rels.summary_for(agent_id)
        return {"social_relationships": summary}
