"""
src/service/interfaces.py
──────────────────────────────────────────────────────────────
Service-layer protocols and enumerations.

SimulationController is the primary interface between any transport adapter
and the simulation engine. Transport code (WebSocket handlers, gRPC servicers,
Unity bridges) depend only on this protocol — never on Simulation directly.

Why a Protocol and not an ABC:
  - Allows existing Simulation-wrapping code to satisfy the interface without
    re-instantiation or inheritance gymnastics.
  - Consistent with the rest of the codebase's structural typing approach.
"""
from __future__ import annotations

from enum import Enum
from typing import Any, Callable, Protocol, runtime_checkable

from src.service.dto import (
    ServiceEvent, SimulationStatus, StepRequest, StepResponse,
)


# ── Session lifecycle ─────────────────────────────────────────────────────────

class SessionState(Enum):
    CREATED = "created"   # initialised, not yet ticked
    RUNNING = "running"   # run() loop active
    PAUSED  = "paused"    # run() loop suspended; step() still works
    STOPPED = "stopped"   # shutdown() called; no further ticks
    ERROR   = "error"     # unrecoverable error; check status()


# ── Event handler type ────────────────────────────────────────────────────────

EventHandler = Callable[[ServiceEvent], None]
"""Callable signature for service-layer event subscribers."""


# ── SimulationController protocol ─────────────────────────────────────────────

@runtime_checkable
class SimulationController(Protocol):
    """
    Transport-facing interface to a running simulation session.

    Transport adapters (WebSocket handlers, gRPC servicers, test harnesses)
    depend on this protocol exclusively. The core engine is never imported
    by transport code.

    Tick control
    ────────────
    step()       — execute one tick; for externally-clocked or interactive use.
    run()        — run all configured ticks autonomously (async, non-blocking
                   from the caller's perspective).
    pause()      — suspend the run() loop after the current tick completes.
    resume()     — resume a paused run() loop.
    shutdown()   — stop the session permanently; no further ticks are executed.

    Event streaming
    ───────────────
    subscribe()  — register a handler for a specific event_type (or None for
                   all events). Returns a subscription_id for unsubscribe().
    unsubscribe() — remove a registered handler.

    Snapshot API
    ────────────
    snapshot() / restore() — delegate to Simulation; same semantics.

    Introspection
    ─────────────
    session_id, state, tick — lightweight properties safe to call at any time.
    status()   — full SimulationStatus snapshot.
    """

    @property
    def session_id(self) -> str: ...

    @property
    def state(self) -> SessionState: ...

    @property
    def tick(self) -> int: ...

    async def step(self, request: StepRequest | None = None) -> StepResponse:
        """Execute one tick and return the full step response."""
        ...

    async def run(self) -> None:
        """Run all configured ticks to completion (or until paused/stopped)."""
        ...

    def pause(self) -> None:
        """Suspend the run() loop. Current tick completes before suspending."""
        ...

    def resume(self) -> None:
        """Resume a paused run() loop."""
        ...

    def shutdown(self) -> None:
        """Stop the session permanently."""
        ...

    def snapshot(self) -> Any:
        """Capture and return a SimulationSnapshot."""
        ...

    def restore(self, snapshot: Any) -> None:
        """Restore simulation state from a SimulationSnapshot."""
        ...

    def subscribe(self, event_type: str | None, handler: EventHandler) -> str:
        """
        Register handler for events of event_type (None = all events).
        Returns a subscription_id for use with unsubscribe().
        """
        ...

    def unsubscribe(self, subscription_id: str) -> None:
        """Remove a subscription by its ID."""
        ...

    def status(self) -> SimulationStatus:
        """Return a full status snapshot of the current session."""
        ...
