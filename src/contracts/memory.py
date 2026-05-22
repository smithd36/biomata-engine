from __future__ import annotations

from typing import Any, Protocol, runtime_checkable
from .action import Intent


# ── Memory ────────────────────────────────────────────────────────────────────

@runtime_checkable
class Memory(Protocol):
    """
    Per-agent episodic memory.
    Builtin: SimpleMemory (rolling deque of strings).
    Future: VectorMemory, SymbolicMemory, NoMemory.
    """
    def store(self, tick: int, observation: str, intent: Intent, outcome: str) -> None: ...

    def recall(self, n: int = 6) -> str:
        """Return the n most recent memories as a formatted string."""
        ...

    def serialize(self) -> bytes:
        """Full snapshot for checkpointing."""
        ...

    def restore(self, data: bytes) -> None:
        """Restore from a checkpoint snapshot."""
        ...