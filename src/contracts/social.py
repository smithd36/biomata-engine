from __future__ import annotations

from typing import Any, Protocol, runtime_checkable

# ── Social ────────────────────────────────────────────────────────────────────

@runtime_checkable
class SocialSystem(Protocol):
    """
    Tracks inter-agent relationships.
    Builtin: WeightedGraphSocial (directed graph with float weights).
    Future: FactionTable, ReputationMatrix, AllianceSystem.
    """
    def add_agent(self, agent_id: str, name: str) -> None: ...

    def update(self, from_id: str, to_id: str, delta: float) -> None: ...

    def relationship(self, from_id: str, to_id: str) -> float: ...

    def describe(self, agent_id: str) -> str:
        """Human-readable summary of this agent's relationships."""
        ...

    def serialize(self) -> bytes:
        """Capture the full relationship graph as opaque bytes for snapshotting."""
        ...

    def restore(self, data: bytes) -> None:
        """Restore the relationship graph from bytes produced by serialize()."""
        ...