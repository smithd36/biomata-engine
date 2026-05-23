"""
src/contracts/snapshot.py
─────────────────────────────────────────────────────────
Snapshot protocol and data types for deterministic simulation save/restore.

SimulationSnapshot is the complete, self-contained serialized state of a
Simulation at a given tick. Design properties:

  opaque at component level  — each plugin serializes its own mutable state
                               to bytes; the engine never inspects those bytes
  versioned                  — 'version' guards against loading incompatible files
  partial-safe               — components that don't implement Snapshotable are
                               recorded as None; restore() skips them gracefully
  stable agent references    — restore() mutates agents in-place, so all existing
                               object references (Brain, EventBus subscribers, etc.)
                               remain valid after restore

Usage
─────
    snapshot = sim.snapshot()
    sim.restore(snapshot)

    # File persistence:
    sim.save_snapshot("checkpoints/tick_10.pkl")
    sim.load_snapshot("checkpoints/tick_10.pkl")
"""
from __future__ import annotations

import pickle
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Protocol, runtime_checkable


SNAPSHOT_VERSION = "1"


# ── Snapshotable protocol ─────────────────────────────────────────────────────

@runtime_checkable
class Snapshotable(Protocol):
    """
    Optional protocol for simulation components that participate in
    sim.snapshot() / sim.restore().

    Components implementing this are automatically included in
    SimulationSnapshot. Those that don't are recorded as None — a partial
    snapshot still useful for inspection but unable to guarantee a complete
    deterministic restore.

    Implementing rules:
      - serialize() must capture ALL mutable runtime state.
      - Immutable config (model names, file paths, personality) need not be
        included — those are re-supplied by the original YAML on restore.
      - Do NOT include references to other simulation objects (agents, world,
        bus) — they are wired back by Simulation.restore() after component
        restoration is complete.
      - restore() must be idempotent: calling it twice with the same bytes
        must yield the same state as calling it once.
    """

    def serialize(self) -> bytes:
        """Capture full mutable runtime state as opaque bytes."""
        ...

    def restore(self, data: bytes) -> None:
        """Restore mutable runtime state from bytes produced by serialize()."""
        ...


# ── Snapshot data types ───────────────────────────────────────────────────────

@dataclass
class AgentSnapshot:
    """Serialized state of one Agent at a point in time."""
    id:        str
    name:      str
    inventory: dict[str, Any]
    memory:    bytes               # Memory.serialize()
    state_ext: bytes | None        # AgentStateExtension.serialize(), or None if absent
    brain:     bytes | None        # Brain.serialize() if Snapshotable, else None


@dataclass
class SimulationSnapshot:
    """
    Complete, self-contained snapshot of a Simulation at one tick.

    Fields are None when the corresponding component does not implement
    Snapshotable. A snapshot with None world or social fields can be used
    for memory/inventory inspection but cannot deterministically restore
    world-owned state across those dimensions.
    """
    version:    str                  = SNAPSHOT_VERSION
    tick:       int                  = 0
    rng_state:  Any                  = None   # random.Random.getstate() tuple
    config:     dict[str, Any]       = field(default_factory=dict)
    agents:     list[AgentSnapshot]  = field(default_factory=list)
    social:     bytes | None         = None   # SocialSystem.serialize() or None
    world:      bytes | None         = None   # World.serialize() if Snapshotable
    scheduler:  bytes | None         = None   # Scheduler.serialize() if Snapshotable
    created_at: str                  = field(
        default_factory=lambda: datetime.now(timezone.utc).isoformat()
    )

    def is_complete(self) -> bool:
        """True when both world and social state are captured."""
        return self.world is not None and self.social is not None

    def missing_components(self) -> list[str]:
        """Names of components whose state could not be captured."""
        missing = []
        if self.world is None:
            missing.append("world")
        if self.social is None:
            missing.append("social")
        for a in self.agents:
            if a.state_ext is None:
                missing.append(f"agent:{a.id}:state_ext")
            if a.brain is None:
                missing.append(f"agent:{a.id}:brain")
        return missing


# ── Snapshot error ────────────────────────────────────────────────────────────

class SnapshotError(Exception):
    """Raised when a snapshot cannot be taken or restored."""


# ── File persistence helpers ──────────────────────────────────────────────────

def save_to_file(snapshot: SimulationSnapshot, path: str | Path) -> None:
    """Persist a snapshot to disk via pickle."""
    p = Path(path)
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_bytes(pickle.dumps(snapshot))


def load_from_file(path: str | Path) -> SimulationSnapshot:
    """Load and validate a snapshot from a file produced by save_to_file()."""
    p = Path(path)
    if not p.exists():
        raise SnapshotError(f"Snapshot file not found: {p}")
    obj = pickle.loads(p.read_bytes())
    if not isinstance(obj, SimulationSnapshot):
        raise SnapshotError(
            f"{p} does not contain a SimulationSnapshot "
            f"(got {type(obj).__name__})"
        )
    if obj.version != SNAPSHOT_VERSION:
        raise SnapshotError(
            f"Snapshot version mismatch: expected {SNAPSHOT_VERSION!r}, "
            f"got {obj.version!r}"
        )
    return obj
