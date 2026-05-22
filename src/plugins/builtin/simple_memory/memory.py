"""
src/plugins/builtin/simple_memory/memory.py
─────────────────────────────────────────────────────
SimpleMemory: rolling deque of formatted strings.
The default memory implementation — sufficient for most simulations.
"""
from __future__ import annotations

import pickle
from collections import deque

from src.contracts.action import Intent


class SimpleMemory:
    def __init__(self, capacity: int = 20):
        self._log: deque[str] = deque(maxlen=capacity)

    def store(self, tick: int, observation: str, intent: Intent, outcome: str) -> None:
        self._log.append(
            f"[t{tick}] {observation} → {intent.action}: {outcome[:70]}"
        )

    def recall(self, n: int = 6) -> str:
        items = list(self._log)[-n:]
        return "\n".join(items) if items else "No memories yet."

    def serialize(self) -> bytes:
        return pickle.dumps(list(self._log))

    def restore(self, data: bytes) -> None:
        self._log = deque(pickle.loads(data), maxlen=self._log.maxlen)
