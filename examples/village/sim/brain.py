"""
examples/village/sim/brain.py
Deterministic POI-cycling brain for village NPCs.

Cycles through a fixed list of points of interest, idling for `idle_ticks`
ticks when it arrives before advancing to the next POI.

YAML config:
    brain:
      class: examples.village.sim.brain.VillagerBrain
      pois:
        - [0.0, 0.0]    # TownSquare
        - [6.0, 4.0]    # Well
        - [14.0, 0.0]   # Market
      idle_ticks: 3
      arrival_threshold: 1.2
"""
from __future__ import annotations

import math
from typing import Any

from src.contracts.action import ActionSchema, Intent
from src.contracts.brain import BrainContext
from src.contracts.world import AgentView

Observation = dict[str, Any]


class VillagerBrain:
    """
    Cycles through a list of POIs, pausing idle_ticks ticks at each stop
    before advancing. Accepts llm_config via **kwargs (ignored).
    """

    def __init__(
        self,
        pois: list[list[float]],
        idle_ticks: int = 2,
        arrival_threshold: float = 1.2,
        **kwargs: Any,
    ) -> None:
        if not pois:
            raise ValueError("VillagerBrain requires at least one POI.")
        self._pois: list[tuple[float, float]] = [
            (float(p[0]), float(p[1])) for p in pois
        ]
        self._idle_ticks = idle_ticks
        self._threshold = arrival_threshold
        self._state: dict[str, dict[str, Any]] = {}

    def _get(self, agent_id: str) -> dict[str, Any]:
        if agent_id not in self._state:
            self._state[agent_id] = {
                "idx": 0,
                "idle_count": 0,
                "tx": self._pois[0][0],
                "tz": self._pois[0][1],
            }
        return self._state[agent_id]

    async def decide(
        self,
        agent: AgentView,
        observation: Observation,
        actions: list[ActionSchema],
        context: BrainContext,
    ) -> Intent:
        s = self._get(agent.id)
        px = float(observation.get("position_x", 0.0))
        pz = float(observation.get("position_z", 0.0))
        tx, tz = s["tx"], s["tz"]
        dist = math.sqrt((px - tx) ** 2 + (pz - tz) ** 2)

        if dist < self._threshold:
            if s["idle_count"] < self._idle_ticks:
                s["idle_count"] += 1
                return Intent(
                    action="idle",
                    reasoning=(
                        f"at POI {s['idx']} ({tx:.1f}, {tz:.1f}) — "
                        f"idling {s['idle_count']}/{self._idle_ticks}"
                    ),
                )
            idx = (s["idx"] + 1) % len(self._pois)
            s["idx"] = idx
            s["idle_count"] = 0
            s["tx"], s["tz"] = self._pois[idx]
            tx, tz = s["tx"], s["tz"]

        return Intent(
            action="navigate",
            parameters={"target_x": tx, "target_z": tz},
            reasoning=f"heading to POI {s['idx']} ({tx:.1f}, {tz:.1f})",
        )
