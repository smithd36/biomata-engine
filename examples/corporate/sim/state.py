"""
examples/corporate/sim/state.py
────────────────────────────────
EmployeeVitals: per-agent state for the corporate simulation.

Tracks stress, influence, and reputation — no physical vitals.
Demonstrates that AgentStateExtension is completely domain-agnostic.
"""
from __future__ import annotations

import pickle
from dataclasses import dataclass, field
from typing import Any


_ROLE_INFLUENCE = {"executive": 80, "manager": 50, "employee": 20}


@dataclass
class EmployeeVitals:
    """
    Corporate-domain AgentStateExtension.
    Structurally satisfies AgentStateExtension (duck-typed Protocol).
    """
    role:       str = "employee"
    stress:     int = 20
    influence:  int = field(init=False)
    reputation: int = 50

    def __post_init__(self) -> None:
        self.influence = _ROLE_INFLUENCE.get(self.role, 20)

    # ── AgentStateExtension protocol ──────────────────────────────────────────

    def tick(self) -> None:
        self.stress    = min(100, self.stress + 4)
        self.influence = max(0,   self.influence - 1)

    def apply_mutations(self, mutations: dict[str, Any]) -> None:
        if "stress_delta" in mutations:
            self.stress     = max(0, min(100, self.stress    + int(mutations["stress_delta"])))
        if "influence_delta" in mutations:
            self.influence  = max(0, min(100, self.influence + int(mutations["influence_delta"])))
        if "reputation_delta" in mutations:
            self.reputation = max(0, min(100, self.reputation + int(mutations["reputation_delta"])))
        # stress_set / influence_set for absolute overrides
        if "stress_set" in mutations:
            self.stress     = max(0, min(100, int(mutations["stress_set"])))

    def snapshot(self) -> dict[str, Any]:
        return {
            "role":       self.role,
            "stress":     self.stress,
            "influence":  self.influence,
            "reputation": self.reputation,
        }

    def to_prompt_str(self) -> str:
        return (
            f"Role: {self.role} | "
            f"Stress: {self.stress}/100 | "
            f"Influence: {self.influence}/100 | "
            f"Reputation: {self.reputation}/100"
        )

    def urgent_advice(self) -> str:
        if self.stress > 85:
            return "⚠ BURNOUT IMMINENT: stress is critical. Delegate or idle to recover."
        if self.reputation < 15:
            return "⚠ REPUTATION CRISIS: you are at risk of being fired."
        if self.influence < 8:
            return "⚠ MARGINALIZED: almost no influence left. Build alliances urgently."
        return ""

    def serialize(self) -> bytes:
        return pickle.dumps(self.snapshot())

    def restore(self, data: bytes) -> None:
        d = pickle.loads(data)
        self.role       = d["role"]
        self.stress     = d["stress"]
        self.influence  = d["influence"]
        self.reputation = d["reputation"]
