"""
examples/village/sim/brain.py
──────────────────────────────
Deterministic brains for village NPCs.

  VillagerBrain       — cycles through a POI list, idles N ticks at each stop
  SocialVillagerBrain — same routing, but initiates social interactions when
                        nearby agents are detected at a POI

YAML config:
    brain:
      class: examples.village.sim.brain.VillagerBrain
      pois:
        - [0.0, 0.0]    # TownSquare
        - [6.0, 4.0]    # Well
      idle_ticks: 3
      arrival_threshold: 1.2

    brain:
      class: examples.village.sim.brain.SocialVillagerBrain
      pois: [...]
      idle_ticks: 3
      social_chance: 0.5   # probability of greeting a nearby agent when idle
"""
from __future__ import annotations

import math
import random
from typing import Any

from src.contracts.action import ActionSchema, Intent
from src.contracts.brain import BrainContext
from src.contracts.world import AgentView

Observation = dict[str, Any]


class VillagerBrain:
    """
    Cycles through a list of POIs, pausing idle_ticks ticks at each stop
    before advancing to the next. Accepts llm_config via **kwargs (ignored).
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
        self._idle_ticks  = idle_ticks
        self._threshold   = arrival_threshold
        self._state: dict[str, dict[str, Any]] = {}

    def _get(self, agent_id: str) -> dict[str, Any]:
        if agent_id not in self._state:
            self._state[agent_id] = {
                "idx":        0,
                "idle_count": 0,
                "tx":         self._pois[0][0],
                "tz":         self._pois[0][1],
            }
        return self._state[agent_id]

    async def decide(
        self,
        agent:       AgentView,
        observation: Observation,
        actions:     list[ActionSchema],
        context:     BrainContext,
    ) -> Intent:
        s  = self._get(agent.id)
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
            idx           = (s["idx"] + 1) % len(self._pois)
            s["idx"]      = idx
            s["idle_count"] = 0
            s["tx"], s["tz"] = self._pois[idx]
            tx, tz        = s["tx"], s["tz"]

        return Intent(
            action="navigate",
            parameters={"target_x": tx, "target_z": tz},
            reasoning=f"heading to POI {s['idx']} ({tx:.1f}, {tz:.1f})",
        )


# ── SocialVillagerBrain ───────────────────────────────────────────────────────

_GREETINGS: list[str] = [
    "Good day to you!",
    "Well met, friend.",
    "Fine weather today.",
    "How fares the day?",
    "Good to see you here.",
    "Have you heard any news?",
]

_IDLE_COMMENTS: list[str] = [
    "A good day's work ahead.",
    "These old bones are tired.",
    "The market seems busy.",
    "Lovely morning in the village.",
    "Another peaceful day.",
]


class SocialVillagerBrain(VillagerBrain):
    """
    POI-cycling brain that initiates greetings when idle at a POI with
    nearby agents. Prefers 'socialize' if the agent has that capability;
    falls back to 'speak' for agents without the social capability tag.

    Each agent it has already greeted is tracked per-session to avoid
    spamming the same pair every idle tick (cooldown: 6 ticks).
    """

    def __init__(
        self,
        pois: list[list[float]],
        idle_ticks: int = 2,
        arrival_threshold: float = 1.2,
        social_chance: float = 0.45,
        speak_chance:  float = 0.25,
        **kwargs: Any,
    ) -> None:
        super().__init__(pois, idle_ticks, arrival_threshold, **kwargs)
        self._social_chance = social_chance
        self._speak_chance  = speak_chance
        self._greeted: dict[str, dict[str, int]] = {}  # agent_id → {other_id → last_tick}

    def _greeted_at(self, agent_id: str) -> dict[str, int]:
        if agent_id not in self._greeted:
            self._greeted[agent_id] = {}
        return self._greeted[agent_id]

    async def decide(
        self,
        agent:       AgentView,
        observation: Observation,
        actions:     list[ActionSchema],
        context:     BrainContext,
    ) -> Intent:
        s    = self._get(agent.id)
        px   = float(observation.get("position_x", 0.0))
        pz   = float(observation.get("position_z", 0.0))
        tx, tz = s["tx"], s["tz"]
        dist = math.sqrt((px - tx) ** 2 + (pz - tz) ** 2)
        at_poi  = dist < self._threshold
        idling  = at_poi and s["idle_count"] > 0

        if idling:
            nearby  = observation.get("nearby_agents", [])
            greeted = self._greeted_at(agent.id)
            tick    = context.tick

            # Filter: only consider agents not recently greeted (cooldown 6 ticks)
            fresh = [
                a for a in nearby
                if tick - greeted.get(a["id"], -999) >= 6
            ]

            action_names = {sc.name for sc in actions}

            if fresh and "socialize" in action_names and random.random() < self._social_chance:
                target = random.choice(fresh)
                greeted[target["id"]] = tick
                msg = random.choice(_GREETINGS)
                return Intent(
                    action="socialize",
                    parameters={"target_id": target["id"], "message": msg},
                    reasoning=f"greeting {target['name']} at POI",
                )

            if "speak" in action_names and random.random() < self._speak_chance:
                msg = random.choice(_GREETINGS + _IDLE_COMMENTS)
                return Intent(
                    action="speak",
                    parameters={"message": msg},
                    reasoning="passing comment while idle",
                )

        return await super().decide(agent, observation, actions, context)
