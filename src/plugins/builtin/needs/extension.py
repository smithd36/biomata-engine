"""
src/plugins/builtin/needs/extension.py
────────────────────────────────────────
NeedsExtension — lightweight per-agent scalar-needs state.

Implements AgentStateExtension so it drops directly onto agent.state_ext
with no engine modifications required.

Example
-------
    agent = Agent(
        id="alice", name="Alice", brain=..., memory=...,
        state_ext=NeedsExtension(
            needs={"hunger": 50.0, "energy": 80.0},
            decay_rates={"hunger": 2.0, "energy": 1.0},
        ),
    )
"""
from __future__ import annotations

import pickle
from typing import Any


class NeedsExtension:
    """
    Stores simple scalar needs (hunger, energy, warmth, etc.).

    Parameters
    ----------
    needs
        Initial values, 0-100 by default.
    decay_rates
        How much each need falls per tick.  Missing keys → no decay.
    clamp
        (min, max) bounds applied on every write.  Default: (0.0, 100.0).
    """

    def __init__(
        self,
        needs: dict[str, float],
        decay_rates: dict[str, float] | None = None,
        clamp: tuple[float, float] = (0.0, 100.0),
    ) -> None:
        self.needs       = dict(needs)
        self.decay_rates = dict(decay_rates) if decay_rates else {}
        self._min, self._max = clamp

    # ── Public accessors ──────────────────────────────────────────────────────

    def get_need(self, name: str, default: float = 0.0) -> float:
        return self.needs.get(name, default)

    def set_need(self, name: str, value: float) -> None:
        self.needs[name] = max(self._min, min(self._max, value))

    # ── AgentStateExtension protocol ──────────────────────────────────────────

    def tick(self) -> None:
        """Apply decay rates once per simulation step."""
        for name, rate in self.decay_rates.items():
            if name in self.needs:
                self.needs[name] = max(
                    self._min, min(self._max, self.needs[name] - rate)
                )

    def apply_mutations(self, mutations: dict[str, Any]) -> None:
        """Apply deltas from ActionResult.mutations.ext."""
        for name, delta in mutations.items():
            if name in self.needs and isinstance(delta, (int, float)):
                self.needs[name] = max(
                    self._min, min(self._max, self.needs[name] + delta)
                )

    def snapshot(self) -> dict[str, Any]:
        return {"needs": dict(self.needs), "decay_rates": dict(self.decay_rates)}

    def to_prompt_str(self) -> str:
        if not self.needs:
            return ""
        return "needs: " + ", ".join(f"{k}={v:.1f}" for k, v in self.needs.items())

    def urgent_advice(self) -> str:
        """Warn the brain when any need is critically low (<= 10)."""
        critical = [k for k, v in self.needs.items() if v <= 10.0]
        return f"CRITICAL needs: {', '.join(critical)}" if critical else ""

    def serialize(self) -> bytes:
        return pickle.dumps({
            "needs":       self.needs,
            "decay_rates": self.decay_rates,
            "clamp":       (self._min, self._max),
        })

    def restore(self, data: bytes) -> None:
        state          = pickle.loads(data)
        self.needs       = state["needs"]
        self.decay_rates = state["decay_rates"]
        self._min, self._max = state.get("clamp", (0.0, 100.0))
