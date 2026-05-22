"""
examples/medieval/sim/world.py
───────────────────────────────
MedievalWorld: implements the World contract for the medieval simulation.
Grid-based, with seasons, weather, and resource regeneration.
"""
from __future__ import annotations

import random as _random_module
from dataclasses import dataclass, field
from typing import Any

from src.contracts.world import AgentView
from src.contracts.action import ActionResult
from examples.medieval.sim.spatial import Grid


@dataclass
class WorldEvent:
    tick: int
    agent_id: str
    agent_name: str
    action: str
    outcome: str
    location: str = ""


class MedievalWorld:
    SEASONS = ["spring", "summer", "autumn", "winter"]
    WEATHER = ["clear", "rainy", "stormy", "foggy", "sunny"]

    def __init__(self, width: int = 5, height: int = 5, seed: int = 42):
        # Default rng; Simulation overwrites self.rng with its seeded canonical instance.
        self.rng     = _random_module.Random(seed)
        self.grid    = Grid(width=width, height=height)
        self._tick   = 0
        self.season  = "spring"
        self.weather = "clear"
        self.events: list[WorldEvent] = []
        self._agents: list[Any] = []

    def register_agents(self, agents: list[Any]) -> None:
        self._agents = agents

    # ── World protocol ────────────────────────────────────────────────────────

    def tick(self) -> None:
        self._tick  += 1
        self.season  = self.SEASONS[(self._tick // 10) % 4]
        self.weather = self.rng.choice(self.WEATHER)
        self.grid.regen_all()

    @property
    def current_tick(self) -> int:
        return self._tick

    @property
    def metadata(self) -> dict[str, Any]:
        return {"tick": self._tick, "season": self.season, "weather": self.weather}

    def observe(self, agent_id: str) -> dict[str, Any]:
        cell = self.grid.cell_for(agent_id)
        if not cell:
            return {"location": "unknown"}
        nbrs = self.grid.neighbors(cell.x, cell.y)
        return {
            "location":       f"{cell.name} ({cell.x},{cell.y})",
            "adjacent_cells": {d: c.name for d, c in nbrs.items() if c},
            "local_food":     cell.local_food,
            "local_wood":     cell.local_wood,
        }

    def get_nearby_agents(self, agent_id: str) -> list[AgentView]:
        visible_ids = self.grid.agents_in_range(agent_id)
        return [
            AgentView.from_agent(a)
            for a in self._agents if a.id in visible_ids
        ]

    def get_agent(self, agent_id: str) -> AgentView | None:
        for a in self._agents:
            if a.id == agent_id:
                return AgentView.from_agent(a)
        return None

    def are_adjacent(self, id1: str, id2: str) -> bool:
        return self.grid.are_adjacent(id1, id2)

    def get_world_data(self) -> dict[str, Any]:
        return {"_grid": self.grid, "tick": self._tick}

    def place_agent(self, agent_id: str, x: int = 0, y: int = 0) -> None:
        self.grid.place_agent(agent_id, x, y)

    def apply(self, agent_id: str, result: ActionResult) -> None:
        """Apply cross-agent mutations (target inventory, health, etc.)."""
        m = result.state_mutations
        target_id = m.get("target_id")
        if not target_id:
            return
        target = next((a for a in self._agents if a.id == target_id), None)
        if not target:
            return
        for item, delta in m.get("target_inventory", {}).items():
            if isinstance(delta, int):
                target.inventory[item] = max(0, target.inventory.get(item, 0) + delta)
        if "target_health_delta" in m and target.state_ext:
            target.state_ext.apply_mutations({"health_delta": m["target_health_delta"]})

    # ── Display helpers (example-specific, not part of the World protocol) ────

    def ascii_map(self, agents: list) -> str:
        return self.grid.ascii_map(agents)
