"""
examples/corporate/sim/world.py
────────────────────────────────
CorporateWorld: implements the World contract using an org graph, not a spatial grid.

Key differences from the medieval spatial world:
  - No x/y coordinates or movement
  - Adjacency = same department OR direct reporting link
  - get_nearby_agents returns all org members (small company)
  - Observation is org-structural: role, reports, department, budget, events
  - world.apply() handles budget transfers and cross-agent state mutations
"""
from __future__ import annotations

import random as _random_module
from collections import deque
from typing import Any

import networkx as nx

from src.contracts.world import AgentView
from src.contracts.action import ActionResult


class CorporateWorld:
    QUARTERS = ["Q1", "Q2", "Q3", "Q4"]
    MARKETS  = ["bull", "bear", "volatile", "stable", "contracting"]

    def __init__(self) -> None:
        # Default rng; Simulation overwrites self.rng with its seeded canonical instance.
        self.rng:         _random_module.Random = _random_module.Random()
        self.graph:       nx.DiGraph        = nx.DiGraph()  # edges: manager → report
        self.departments: dict[str, str]    = {}  # agent_id → dept
        self.roles:       dict[str, str]    = {}  # agent_id → role
        self._tick   = 0
        self.quarter = "Q1"
        self.market  = "stable"
        self._events: deque[dict] = deque(maxlen=30)
        self._agents: list[Any]   = []

    def register_agents(self, agents: list[Any]) -> None:
        self._agents = agents
        # Set starting budget based on org role
        for a in agents:
            role = self.roles.get(a.id, "employee")
            if "budget" not in a.inventory:
                a.inventory["budget"] = {
                    "executive": 500,
                    "manager":   200,
                    "employee":  0,
                }.get(role, 0)

    # ── World protocol ────────────────────────────────────────────────────────

    def tick(self) -> None:
        self._tick  += 1
        self.quarter = self.QUARTERS[(self._tick // 3) % 4]
        self.market  = self.rng.choice(self.MARKETS)

    @property
    def current_tick(self) -> int:
        return self._tick

    @property
    def metadata(self) -> dict[str, Any]:
        return {
            "tick":    self._tick,
            "quarter": self.quarter,
            "market":  self.market,
        }

    def place_agent(
        self,
        agent_id:   str,
        department: str       = "General",
        role:       str       = "employee",
        manager:    str | None = None,
    ) -> None:
        self.graph.add_node(agent_id)
        self.departments[agent_id] = department
        self.roles[agent_id]       = role
        if manager and manager in self.graph.nodes:
            self.graph.add_edge(manager, agent_id)

    def observe(self, agent_id: str) -> dict[str, Any]:
        dept     = self.departments.get(agent_id, "Unknown")
        role     = self.roles.get(agent_id, "employee")
        reports  = list(self.graph.successors(agent_id))
        managers = list(self.graph.predecessors(agent_id))

        agent  = next((a for a in self._agents if a.id == agent_id), None)
        budget = agent.inventory.get("budget", 0) if agent else 0

        colleagues = [
            aid for aid, d in self.departments.items()
            if d == dept and aid != agent_id
        ]

        return {
            "department":     dept,
            "role":           role,
            "reports_to":     managers[0] if managers else "none (top of org)",
            "direct_reports": reports,
            "colleagues":     colleagues,
            "budget_$k":      budget,
            "recent_events":  [self._fmt_event(e) for e in list(self._events)[-5:]],
        }

    def get_nearby_agents(self, agent_id: str) -> list[AgentView]:
        # In a small org, everyone is visible to everyone
        return [AgentView.from_agent(a) for a in self._agents if a.id != agent_id]

    def get_agent(self, agent_id: str) -> AgentView | None:
        for a in self._agents:
            if a.id == agent_id:
                return AgentView.from_agent(a)
        return None

    def are_adjacent(self, id1: str, id2: str) -> bool:
        """Adjacent = same department OR one hop in the org hierarchy."""
        if self.departments.get(id1) == self.departments.get(id2):
            return True
        return self.graph.has_edge(id1, id2) or self.graph.has_edge(id2, id1)

    def get_world_data(self) -> dict[str, Any]:
        manager_of = {
            aid: (list(self.graph.predecessors(aid))[0]
                  if list(self.graph.predecessors(aid)) else None)
            for aid in self.graph.nodes
        }
        return {
            "_graph":      self.graph,
            "_manager_of": manager_of,
            "_reports_of": {aid: list(self.graph.successors(aid)) for aid in self.graph.nodes},
            "_roles":      dict(self.roles),
            "_depts":      dict(self.departments),
            "tick":        self._tick,
        }

    def apply(self, agent_id: str, result: ActionResult) -> None:
        """Apply world-side mutations: cross-agent state, budget, org events."""
        m = result.state_mutations

        # Log org event
        if "event" in m and isinstance(m["event"], dict):
            self._events.append({**m["event"], "tick": self._tick})

        # Cross-agent effects
        target_id = m.get("target_id")
        if not target_id:
            return
        target = next((a for a in self._agents if a.id == target_id), None)
        if not target:
            return

        # Budget transfer (via target_inventory)
        for item, delta in m.get("target_inventory", {}).items():
            if isinstance(delta, (int, float)):
                target.inventory[item] = max(0, target.inventory.get(item, 0) + int(delta))

        # General state extension mutations on target
        if "target_state_mutations" in m and target.state_ext:
            target.state_ext.apply_mutations(m["target_state_mutations"])

    # ── Display helpers (not part of the protocol) ────────────────────────────

    def org_summary(self) -> str:
        lines = []
        for aid in self.graph.nodes:
            depth    = "  " * len(nx.ancestors(self.graph, aid))
            role     = self.roles.get(aid, "?")
            dept     = self.departments.get(aid, "?")
            a        = next((x for x in self._agents if x.id == aid), None)
            name     = a.name if a else aid
            budget   = a.inventory.get("budget", 0) if a else 0
            ext      = a.state_ext.snapshot() if (a and a.state_ext) else {}
            inf      = ext.get("influence", "?")
            rep      = ext.get("reputation", "?")
            lines.append(f"{depth}[{role}] {name} ({dept}) — $k:{budget} inf:{inf} rep:{rep}")
        return "\n".join(lines) if lines else "(no agents)"

    def _fmt_event(self, e: dict) -> str:
        t    = e.get("type", "?")
        tick = e.get("tick", "?")
        if t == "gossip":
            return f"t{tick}: {e.get('actor')} gossiped about {e.get('about')}"
        if t == "alliance":
            return f"t{tick}: {e.get('actor')} formed alliance with {e.get('target')}"
        if t == "budget_approved":
            return f"t{tick}: {e.get('actor')} got ${e.get('amount')}k budget"
        if t == "sabotage":
            return f"t{tick}: {e.get('actor')} undermined {e.get('target')}"
        if t == "pitch":
            return f"t{tick}: {e.get('actor')} pitched '{e.get('idea')}'"
        if t == "meeting":
            return f"t{tick}: {e.get('organizer')} held meeting"
        return f"t{tick}: {t}"
