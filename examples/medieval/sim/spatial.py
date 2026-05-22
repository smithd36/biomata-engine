"""
examples/medieval/sim/spatial.py
──────────────────────────────────
Grid and Cell — spatial primitives for the medieval simulation.
Moved here from the old core/ — they are simulation-specific, not engine infrastructure.
"""
from __future__ import annotations
from dataclasses import dataclass, field
from typing import Optional

LOCATION_TYPES = {
    "village":  {"food_rate": 2,  "wood_rate": 0, "shelter": True,  "description": "a quiet village"},
    "forest":   {"food_rate": 4,  "wood_rate": 6, "shelter": False, "description": "a dense forest"},
    "river":    {"food_rate": 6,  "wood_rate": 1, "shelter": False, "description": "a flowing river"},
    "ruins":    {"food_rate": 1,  "wood_rate": 2, "shelter": False, "description": "ancient ruins"},
    "market":   {"food_rate": 0,  "wood_rate": 0, "shelter": True,  "description": "a busy market"},
    "plains":   {"food_rate": 2,  "wood_rate": 1, "shelter": False, "description": "open plains"},
    "mountain": {"food_rate": 1,  "wood_rate": 3, "shelter": False, "description": "rocky mountains"},
}

@dataclass
class Cell:
    x: int
    y: int
    location_type: str = "plains"
    local_food: int = 20
    local_wood: int = 10
    occupants: list[str] = field(default_factory=list)

    @property
    def name(self) -> str:
        return LOCATION_TYPES[self.location_type]["description"]

    def regen(self):
        props = LOCATION_TYPES[self.location_type]
        self.local_food = min(50, self.local_food + props["food_rate"])
        self.local_wood = min(50, self.local_wood + props["wood_rate"])


@dataclass
class Grid:
    width: int = 5
    height: int = 5
    cells: dict[tuple[int, int], Cell] = field(default_factory=dict)

    def __post_init__(self):
        if not self.cells:
            self._generate()

    def _generate(self):
        layout = {
            (2, 2): "village", (0, 0): "forest", (4, 0): "forest",
            (0, 4): "river",   (4, 4): "ruins",  (2, 0): "market",
            (0, 2): "mountain",(4, 2): "plains", (2, 4): "river",
        }
        for y in range(self.height):
            for x in range(self.width):
                loc  = layout.get((x, y), "plains")
                food = LOCATION_TYPES[loc]["food_rate"] * 5
                wood = LOCATION_TYPES[loc]["wood_rate"] * 5
                self.cells[(x, y)] = Cell(x=x, y=y, location_type=loc,
                                          local_food=food, local_wood=wood)

    def get(self, x: int, y: int) -> Optional[Cell]:
        return self.cells.get((x, y))

    def neighbors(self, x: int, y: int) -> dict[str, Optional[Cell]]:
        return {
            "north": self.get(x, y - 1),
            "south": self.get(x, y + 1),
            "west":  self.get(x - 1, y),
            "east":  self.get(x + 1, y),
        }

    def cell_for(self, agent_id: str) -> Optional[Cell]:
        for cell in self.cells.values():
            if agent_id in cell.occupants:
                return cell
        return None

    def move_agent(self, agent_id: str, direction: str) -> tuple[bool, str]:
        current = self.cell_for(agent_id)
        if not current:
            return False, "agent not found on grid"
        target = self.neighbors(current.x, current.y).get(direction)
        if not target:
            return False, f"no cell to the {direction}"
        current.occupants.remove(agent_id)
        target.occupants.append(agent_id)
        return True, f"moved {direction} to {target.name} ({target.x},{target.y})"

    def place_agent(self, agent_id: str, x: int, y: int):
        cell = self.get(x, y)
        if cell and agent_id not in cell.occupants:
            cell.occupants.append(agent_id)

    def agents_in_range(self, agent_id: str, include_adjacent: bool = True) -> list[str]:
        cell = self.cell_for(agent_id)
        if not cell:
            return []
        visible = set(cell.occupants)
        if include_adjacent:
            for nbr in self.neighbors(cell.x, cell.y).values():
                if nbr:
                    visible.update(nbr.occupants)
        visible.discard(agent_id)
        return list(visible)

    def are_adjacent(self, id1: str, id2: str) -> bool:
        c1 = self.cell_for(id1)
        c2 = self.cell_for(id2)
        if not c1 or not c2:
            return False
        if c1 == c2:
            return True
        return (abs(c1.x - c2.x) + abs(c1.y - c2.y)) == 1

    def regen_all(self):
        for cell in self.cells.values():
            cell.regen()

    def ascii_map(self, agents: list) -> str:
        agent_pos: dict[tuple, list[str]] = {}
        for a in agents:
            cell = self.cell_for(a.id)
            if cell:
                agent_pos.setdefault((cell.x, cell.y), []).append(a.name[0])
        lines = []
        for y in range(self.height):
            row = []
            for x in range(self.width):
                cell   = self.cells[(x, y)]
                symbol = {"village":"V","forest":"F","river":"~","ruins":"R",
                          "market":"M","plains":".","mountain":"^"}.get(cell.location_type,"?")
                names  = agent_pos.get((x, y), [])
                row.append(f"[{','.join(names)}]" if names else f" {symbol} ")
            lines.append(" ".join(row))
        return "\n".join(lines)