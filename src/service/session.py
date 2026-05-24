"""
src/service/session.py
──────────────────────────────────────────────────────────────
SimulationSession: concrete SimulationController implementation.

Wraps a Simulation and exposes a transport-agnostic control interface:
tick stepping, lifecycle management (pause/resume/shutdown), event
subscriptions, and snapshot/restore.

Transport adapters (WebSocket, gRPC, Unity bridge) hold a reference to
SimulationSession (or the SimulationController Protocol) and call its
methods — they never import Simulation directly.

create_session()
────────────────
Factory that wires up a Simulation with an EventStreamAdapter and returns
a ready-to-use SimulationSession. Accepts the same keyword arguments as
the Simulation constructor, plus an optional session_id.

External-world pattern
──────────────────────
    world   = HostedWorld()
    session = create_session(agents=..., world=world, registry=...)

    world.push_observation(agent_id, obs)
    response = await session.step(StepRequest(...))
    commands = response.engine_commands()

YAML-driven pattern
───────────────────
    sim     = Simulation.from_config("sim.yaml")
    session = SimulationSession(sim)
    await session.run()
"""
from __future__ import annotations

import asyncio
import uuid
from typing import Any

from src.contracts.snapshot import SimulationSnapshot, Snapshotable
from src.engine.event_bus import EventBus
from src.engine.simulation import Simulation, SimulationConfig
from src.service.dto import (
    AgentDecisionDTO, ServiceEvent, SimulationStatus,
    StepRequest, StepResponse,
)
from src.service.events import EventStreamAdapter
from src.service.interfaces import EventHandler, SessionState, TickMode


class SessionError(Exception):
    """Raised when a session operation is invalid for the current state."""


class SimulationSession:
    """
    Transport-agnostic wrapper around Simulation.

    Implements SimulationController structurally (duck-typing) — no
    inheritance from the Protocol is required or desired.

    Tick modes
    ──────────
    HOST_DRIVEN (default)
        The external client drives timing by calling step().
        pause() / resume() / run() raise SessionError.
        Lifecycle: CREATED → RUNNING (per step) → STOPPED

    AUTONOMOUS
        The session drives its own tick loop via run().
        step() raises SessionError.
        Lifecycle: CREATED → RUNNING → PAUSED ↔ RUNNING → STOPPED
    """

    def __init__(
        self,
        simulation: Simulation,
        session_id: str | None = None,
        tick_mode:  TickMode   = TickMode.HOST_DRIVEN,
    ) -> None:
        self._sim        = simulation
        self._session_id = session_id or str(uuid.uuid4())
        self._tick_mode  = tick_mode
        self._state      = SessionState.CREATED
        self._paused     = asyncio.Event()
        self._paused.set()   # not paused initially — set means "not paused"
        self._adapter    = EventStreamAdapter(simulation.bus, self._session_id)

    @property
    def tick_mode(self) -> TickMode:
        return self._tick_mode

    # ── SimulationController properties ──────────────────────────────────────

    @property
    def session_id(self) -> str:
        return self._session_id

    @property
    def state(self) -> SessionState:
        return self._state

    @property
    def tick(self) -> int:
        return self._sim.world.current_tick

    # ── Tick control ──────────────────────────────────────────────────────────

    async def step(self, request: StepRequest | None = None) -> StepResponse:
        """
        Execute one cognition tick and return a StepResponse.

        Only valid in HOST_DRIVEN mode. Raises SessionError in AUTONOMOUS mode
        or when the session is STOPPED / ERROR.
        """
        if self._tick_mode == TickMode.AUTONOMOUS:
            raise SessionError(
                "Cannot step: session is autonomous — the backend drives tick "
                "timing. Subscribe to events and use pause/resume to control "
                "the loop."
            )
        if self._state in (SessionState.STOPPED, SessionState.ERROR):
            raise SessionError(
                f"Cannot step: session is {self._state.value}"
            )

        # Fan out observations and metadata for HostedWorld-style worlds
        if request:
            world = self._sim.world
            if request.world_metadata and hasattr(world, "push_metadata"):
                world.push_metadata(request.world_metadata)
            for obs_dto in request.agent_observations:
                if hasattr(world, "push_observation"):
                    world.push_observation(obs_dto.agent_id, obs_dto.observation)

        prior_state = self._state
        self._state = SessionState.RUNNING
        try:
            summary = await self._sim.run_tick()
        except Exception:
            self._state = SessionState.ERROR
            raise

        # Restore PAUSED if we were paused going in; otherwise stay RUNNING
        self._state = SessionState.PAUSED if prior_state == SessionState.PAUSED else SessionState.RUNNING

        # Build outbound DTOs. Defensive copies are only paid when the
        # underlying dict/list is non-empty — the empty-dict shortcut matters
        # at 100–500 agents where many actions carry no params or commands.
        decisions = []
        for ar in summary.agent_results:
            params = ar.intent.parameters
            cmds   = ar.result.engine_commands
            decisions.append(AgentDecisionDTO(
                agent_id        = ar.agent_id,
                agent_name      = ar.agent_name,
                action          = ar.intent.action,
                parameters      = dict(params) if params else {},
                outcome_text    = ar.result.outcome_text,
                engine_commands = list(cmds) if cmds else [],
            ))

        return StepResponse(
            tick      = summary.tick,
            decisions = decisions,
            errors    = list(summary.errors),
        )

    async def run(self) -> None:
        """
        Run all configured ticks to completion. AUTONOMOUS mode only.

        Honours pause() / resume() between ticks. Exits early on shutdown().
        Raises SessionError if called in HOST_DRIVEN mode or when stopped.
        """
        if self._tick_mode == TickMode.HOST_DRIVEN:
            raise SessionError(
                "Cannot run: session is host-driven — call step() for each "
                "tick instead."
            )
        if self._state == SessionState.STOPPED:
            raise SessionError("Cannot run: session is stopped")

        self._state = SessionState.RUNNING
        try:
            for _ in range(self._sim.config.ticks):
                if self._state == SessionState.STOPPED:
                    break
                # Wait if paused (paused clears the event)
                await self._paused.wait()
                if self._state == SessionState.STOPPED:
                    break
                await self._sim.run_tick()
        except Exception:
            self._state = SessionState.ERROR
            raise
        else:
            if self._state != SessionState.STOPPED:
                self._state = SessionState.STOPPED

    # ── Lifecycle control ─────────────────────────────────────────────────────

    def pause(self) -> None:
        """
        Suspend the autonomous tick loop after the current tick completes.
        AUTONOMOUS mode only. Raises SessionError in HOST_DRIVEN mode.
        """
        if self._tick_mode == TickMode.HOST_DRIVEN:
            raise SessionError(
                "Cannot pause: session is host-driven — the client controls "
                "timing by calling tick."
            )
        if self._state == SessionState.RUNNING:
            self._state = SessionState.PAUSED
            self._paused.clear()

    def resume(self) -> None:
        """
        Resume a paused autonomous tick loop.
        AUTONOMOUS mode only. Raises SessionError in HOST_DRIVEN mode.
        """
        if self._tick_mode == TickMode.HOST_DRIVEN:
            raise SessionError(
                "Cannot resume: session is host-driven — the client controls "
                "timing by calling tick."
            )
        if self._state == SessionState.PAUSED:
            self._state = SessionState.RUNNING
            self._paused.set()

    def shutdown(self) -> None:
        """
        Stop the session permanently. Any in-progress tick completes first.
        Detaches the event stream adapter from the bus.
        """
        self._state = SessionState.STOPPED
        self._paused.set()   # unblock run() if paused
        self._adapter.close()

    # ── Snapshot API ──────────────────────────────────────────────────────────

    def snapshot(self) -> SimulationSnapshot:
        """Delegate to Simulation.snapshot()."""
        return self._sim.snapshot()

    def restore(self, snapshot: SimulationSnapshot) -> None:
        """Delegate to Simulation.restore()."""
        self._sim.restore(snapshot)

    # ── Event subscription ────────────────────────────────────────────────────

    def subscribe(self, event_type: str | None, handler: EventHandler) -> str:
        """
        Register a handler for ServiceEvents of event_type.
        event_type=None receives all events.
        Returns a subscription_id for unsubscribe().
        """
        return self._adapter.subscribe(event_type, handler)

    def unsubscribe(self, subscription_id: str) -> None:
        """Remove a subscription by its ID."""
        self._adapter.unsubscribe(subscription_id)

    # ── Status ────────────────────────────────────────────────────────────────

    def status(self) -> SimulationStatus:
        """Return a full status snapshot of the current session."""
        return SimulationStatus(
            session_id    = self._session_id,
            state         = self._state.value,
            tick          = self.tick,
            config_ticks  = self._sim.config.ticks,
            agent_count   = len(self._sim.agents),
            has_world_snap= isinstance(self._sim.world, Snapshotable),
            tick_mode     = self._tick_mode.value,
        )


# ── Factory ───────────────────────────────────────────────────────────────────

def create_session(
    simulation:  Simulation,
    session_id:  str | None = None,
    tick_mode:   TickMode   = TickMode.HOST_DRIVEN,
) -> SimulationSession:
    """
    Create a SimulationSession from a pre-built Simulation.

    tick_mode selects who drives timing:

    HOST_DRIVEN (default) — client calls step()/tick on demand:
        world   = HostedWorld()
        sim     = Simulation(agents=..., world=world, registry=...)
        session = create_session(sim, tick_mode=TickMode.HOST_DRIVEN)
        response = await session.step(StepRequest(...))

    AUTONOMOUS — backend self-ticks at configured rate:
        sim     = Simulation.from_config("sim.yaml")
        session = create_session(sim, tick_mode=TickMode.AUTONOMOUS)
        await session.run()   # honours pause() / resume()
    """
    return SimulationSession(simulation, session_id=session_id, tick_mode=tick_mode)
