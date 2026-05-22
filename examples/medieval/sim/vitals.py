"""
sim/vitals.py
─────────────
MedievalVitals is the AgentStateExtension for the bundled medieval simulation.
It implements exactly what was previously baked into Agent (hunger, energy, health)
but now lives in user-space — the engine base class has no knowledge of it.

Swap this out entirely for a different genre:
  - CorporateSim: stress, productivity, budget
  - AntColony: pheromone level, task assignment, carry capacity
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import Any


_ADVICE = {
    "starving":  "⚠ STARVING (hunger>70). You MUST gather_food or die.",
    "exhausted": "⚠ EXHAUSTED (energy<20). You MUST rest now.",
    "hungry":    "Getting hungry — consider gather_food soon.",
    "tired":     "Tired — consider resting.",
    "fine":      "",
}


@dataclass
class MedievalVitals:
    """
    Tracks health / hunger / energy for a single medieval-sim agent.
    Implements AgentStateExtension (structurally, via duck-typing).
    """
    health: int = 100
    hunger: int = 0
    energy: int = 100

    # Tunable per-simulation
    hunger_per_tick: int = 5
    energy_per_tick: int = 8
    starvation_threshold: int = 70
    exhaustion_threshold: int = 20
    starvation_damage: int = 10

    def tick(self) -> None:
        self.hunger = min(100, self.hunger + self.hunger_per_tick)
        self.energy = max(0,   self.energy - self.energy_per_tick)
        if self.hunger >= 90:
            self.health = max(0, self.health - self.starvation_damage)

    def apply_mutations(self, mutations: dict[str, Any]) -> None:
        """
        Supported keys:
          hunger_delta  : int  (negative = less hungry)
          energy_delta  : int
          health_delta  : int
          hunger_set    : int  (absolute override)
          energy_set    : int
        """
        if "hunger_delta" in mutations:
            self.hunger = max(0, min(100, self.hunger + mutations["hunger_delta"]))
        if "energy_delta" in mutations:
            self.energy = max(0, min(100, self.energy + mutations["energy_delta"]))
        if "health_delta" in mutations:
            self.health = max(0, min(100, self.health + mutations["health_delta"]))
        if "hunger_set" in mutations:
            self.hunger = max(0, min(100, mutations["hunger_set"]))
        if "energy_set" in mutations:
            self.energy = max(0, min(100, mutations["energy_set"]))

    def snapshot(self) -> dict[str, Any]:
        return {
            "health": self.health,
            "hunger": self.hunger,
            "energy": self.energy,
        }

    def to_prompt_str(self) -> str:
        return (
            f"Health: {self.health}/100 | "
            f"Hunger: {self.hunger}/100 | "
            f"Energy: {self.energy}/100"
        )

    def urgent_advice(self) -> str:
        if self.hunger > self.starvation_threshold:
            return _ADVICE["starving"]
        if self.energy < self.exhaustion_threshold:
            return _ADVICE["exhausted"]
        if self.hunger > 40:
            return _ADVICE["hungry"]
        if self.energy < 50:
            return _ADVICE["tired"]
        return _ADVICE["fine"]

    # ── Auto-eat convenience (called by simulation, not engine) ───────────────
    def maybe_auto_eat(self, inventory: dict[str, Any]) -> int:
        """
        If hungry and food available, consume up to 3 units.
        Returns amount eaten so the caller can update inventory.
        """
        if self.hunger > 60 and inventory.get("food", 0) > 0:
            eat = min(inventory["food"], 3)
            self.hunger = max(0, self.hunger - eat * 8)
            return eat
        return 0