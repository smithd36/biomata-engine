"""
examples/patrol/sim/brain.py
Deterministic waypoint-patrol brain — no LLM required.

Cycles through a fixed list of world-space (x, z) waypoints.
When the agent arrives within arrival_threshold of the current target it
advances to the next one in the list, wrapping around.

Designed for the patrol visual demo where Unity drives ticks and provides
position observations from TransformObservationProvider.

YAML config:
    brain:
      class: examples.patrol.sim.brain.WaypointBrain
      waypoints:
        - [0.0, 0.0]
        - [8.0, 0.0]
        - [8.0, 8.0]
        - [0.0, 8.0]
      arrival_threshold: 0.8
"""
from __future__ import annotations

import math
from typing import Any

from src.contracts.action import ActionSchema, Intent
from src.contracts.brain import BrainContext
from src.contracts.world import AgentView

Observation = dict[str, Any]


class WaypointBrain:
    """
    Cycles through a fixed waypoint list.  Brain state (current waypoint index
    per agent) is held in memory so consecutive ticks advance naturally.

    The loader always passes ``llm_config`` as a kwarg; **kwargs absorbs it.
    """

    def __init__(
        self,
        waypoints: list[list[float]],
        arrival_threshold: float = 0.8,
        **kwargs: Any,
    ) -> None:
        if not waypoints:
            raise ValueError("WaypointBrain requires at least one waypoint.")
        self._waypoints: list[tuple[float, float]] = [
            (float(w[0]), float(w[1])) for w in waypoints
        ]
        self._threshold = arrival_threshold
        self._indices: dict[str, int] = {}

    async def decide(
        self,
        agent: AgentView,
        observation: Observation,
        actions: list[ActionSchema],
        context: BrainContext,
    ) -> Intent:
        idx = self._indices.get(agent.id, 0)
        wx, wz = self._waypoints[idx]

        px = float(observation.get("position_x", 0.0))
        pz = float(observation.get("position_z", 0.0))

        dist = math.sqrt((px - wx) ** 2 + (pz - wz) ** 2)
        if dist < self._threshold:
            idx = (idx + 1) % len(self._waypoints)
            self._indices[agent.id] = idx
            wx, wz = self._waypoints[idx]

        return Intent(
            action="navigate",
            parameters={"target_x": wx, "target_z": wz},
            reasoning=(
                f"waypoint {idx}/{len(self._waypoints) - 1} "
                f"({wx:.1f}, {wz:.1f})  dist={dist:.2f}"
            ),
        )
