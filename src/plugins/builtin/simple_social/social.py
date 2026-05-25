"""
src/plugins/builtin/simple_social/social.py
────────────────────────────────────────────────────
WeightedGraphSocial: directed graph of float relationships.
Implements the SocialSystem contract.
Moved from core/social.py — it's a plugin, not engine infrastructure.
"""
from __future__ import annotations

import pickle

import networkx as nx


class WeightedGraphSocial:
    def __init__(self):
        self.g = nx.DiGraph()

    def add_agent(self, agent_id: str, name: str) -> None:
        self.g.add_node(agent_id, name=name)

    def update(self, from_id: str, to_id: str, delta: float) -> None:
        if not self.g.has_edge(from_id, to_id):
            self.g.add_edge(from_id, to_id, weight=0.0)
        w = self.g[from_id][to_id]["weight"]
        self.g[from_id][to_id]["weight"] = max(-1.0, min(1.0, w + delta))

    def relationship(self, from_id: str, to_id: str) -> float:
        return round(self.g[from_id][to_id]["weight"], 2) \
            if self.g.has_edge(from_id, to_id) else 0.0

    def describe(self, from_id: str) -> str:
        if from_id not in self.g:
            return "No relationships yet."
        lines = []
        for _, to_id, d in self.g.out_edges(from_id, data=True):
            name  = self.g.nodes[to_id].get("name", to_id)
            w     = d["weight"]
            label = "ally" if w > 0.3 else "enemy" if w < -0.3 else "neutral"
            lines.append(f"{name}: {label} ({w:+.2f})")
        return ", ".join(lines) if lines else "No relationships yet."

    # ── Snapshotable ──────────────────────────────────────────────────────────

    def serialize(self) -> bytes:
        return pickle.dumps({
            "nodes": list(self.g.nodes(data=True)),
            "edges": list(self.g.edges(data=True)),
        })

    def restore(self, data: bytes) -> None:
        state     = pickle.loads(data)
        self.g    = nx.DiGraph()
        for node_id, attrs in state["nodes"]:
            self.g.add_node(node_id, **attrs)
        for u, v, attrs in state["edges"]:
            self.g.add_edge(u, v, **attrs)