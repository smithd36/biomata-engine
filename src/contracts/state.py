from __future__ import annotations

from typing import Any, Protocol, runtime_checkable

# ── AgentStateExtension ───────────────────────────────────────────────────────

@runtime_checkable
class AgentStateExtension(Protocol):
    """
    Simulation-specific per-agent state (vitals, stress, pheromones, etc.).
    The engine calls tick() and apply_mutations(); everything else is
    simulation-defined.
    """
    def tick(self) -> None: ...

    def apply_mutations(self, mutations: dict[str, Any]) -> None: ...

    def snapshot(self) -> dict[str, Any]:
        """Shallow copy — callers must not mutate."""
        ...

    def to_prompt_str(self) -> str: ...

    def urgent_advice(self) -> str:
        """One-line warning for the LLM. Return '' if nothing urgent."""
        ...

    def serialize(self) -> bytes: ...

    def restore(self, data: bytes) -> None: ...