"""
src/transport/grpc/servicer.py
──────────────────────────────────────────────────────────────
SimulationServicer: async gRPC servicer for the biomata simulation engine.

Implements every RPC in SimulationService (simulation.proto) by delegating
to a SimulationSession (service layer) and a HostedWorld or any World that
supports push_observation / push_metadata.

Design rules
────────────
- Never imports from src.engine directly; only from src.service and
  src.contracts. (Exception: Agent construction for RegisterAgent, which
  must build a live object — that's done via src.config.loader._import.)
- All public RPC methods catch exceptions and return structured errors or
  call context.abort() with an appropriate gRPC status code rather than
  leaking tracebacks to the client.
- StreamEvents uses an asyncio.Queue per active stream so the synchronous
  engine EventBus feeds the async gRPC server without thread locks.

Error mapping
─────────────
  SessionError / bad state  → FAILED_PRECONDITION
  Agent / resource not found → NOT_FOUND
  Bad argument               → INVALID_ARGUMENT
  Unexpected engine error    → INTERNAL
"""
from __future__ import annotations

import asyncio
import logging
import pickle
from typing import Any

import grpc

logger = logging.getLogger(__name__)

# Per-stream event queue capacity. At 500 agents the action_completed firing
# rate is ~500/tick; a 2k buffer absorbs ~4 ticks of backlog before drops.
# Override via SimulationServicer(session, event_queue_size=...).
_DEFAULT_EVENT_QUEUE_SIZE = 2048

from src.contracts.snapshot import SnapshotError
from src.engine.agent import Agent
from src.plugins.builtin.simple_memory.memory import SimpleMemory
from src.service import (
    AgentObservationDTO,
    ServiceEvent,
    SessionError,
    SimulationSession,
    StepRequest,
)
from src.transport.grpc.conversions import (
    dict_to_struct,
    safe_dict_to_struct,
    struct_to_dict,
)
from src.transport.grpc.generated import simulation_pb2 as pb
from src.transport.grpc.generated.simulation_pb2_grpc import (
    SimulationServiceServicer,
)


def _import(dotted: str) -> Any:
    """Lightweight re-export of loader._import for dynamic class resolution."""
    from src.config.loader import _import as _loader_import
    return _loader_import(dotted)


class SimulationServicer(SimulationServiceServicer):
    """
    Async gRPC servicer that wraps a SimulationSession.

    Instantiate once and pass to
    ``add_SimulationServiceServicer_to_server(servicer, grpc_server)``.

    Parameters
    ──────────
    session  — the SimulationSession to drive.
    """

    def __init__(
        self,
        session: SimulationSession,
        event_queue_size: int = _DEFAULT_EVENT_QUEUE_SIZE,
    ) -> None:
        self._session = session
        self._sim     = session._sim          # direct access for agent mutation
        self._world   = session._sim.world    # for push_observation / push_metadata
        self._event_queue_size = event_queue_size

    # ── Health ────────────────────────────────────────────────────────────────

    async def HealthCheck(
        self,
        request: pb.HealthCheckRequest,
        context: grpc.aio.ServicerContext,
    ) -> pb.HealthCheckResponse:
        st = self._session.status()
        return pb.HealthCheckResponse(
            status        = "ok",
            session_state = st.state,
            tick          = st.tick,
            agent_count   = st.agent_count,
        )

    # ── Agent registration ────────────────────────────────────────────────────

    async def RegisterAgent(
        self,
        request: pb.RegisterAgentRequest,
        context: grpc.aio.ServicerContext,
    ) -> pb.RegisterAgentResponse:
        cfg = request.config
        if not cfg.agent_id:
            await context.abort(grpc.StatusCode.INVALID_ARGUMENT, "agent_id is required")
        if not cfg.agent_name:
            await context.abort(grpc.StatusCode.INVALID_ARGUMENT, "agent_name is required")
        if not cfg.brain_class:
            await context.abort(grpc.StatusCode.INVALID_ARGUMENT, "brain_class is required")

        # Reject duplicate IDs
        if any(a.id == cfg.agent_id for a in self._sim.agents):
            return pb.RegisterAgentResponse(
                success=False,
                error=f"Agent '{cfg.agent_id}' is already registered",
            )

        try:
            brain_cls    = _import(cfg.brain_class)
            brain_kwargs = struct_to_dict(cfg.brain_config)
            brain        = brain_cls(**brain_kwargs)

            if cfg.memory_class:
                mem_cls    = _import(cfg.memory_class)
                mem_kwargs = struct_to_dict(cfg.memory_config)
                memory     = mem_cls(**mem_kwargs)
            else:
                memory = SimpleMemory()

            agent = Agent(
                id     = cfg.agent_id,
                name   = cfg.agent_name,
                brain  = brain,
                memory = memory,
            )

            self._sim.agents.append(agent)
            if hasattr(self._world, "register_agents"):
                self._world.register_agents(self._sim.agents)

            return pb.RegisterAgentResponse(success=True)

        except (ImportError, AttributeError) as exc:
            return pb.RegisterAgentResponse(success=False, error=f"Import error: {exc}")
        except Exception as exc:
            return pb.RegisterAgentResponse(success=False, error=str(exc))

    async def RemoveAgent(
        self,
        request: pb.RemoveAgentRequest,
        context: grpc.aio.ServicerContext,
    ) -> pb.RemoveAgentResponse:
        agent_id = request.agent_id
        before   = len(self._sim.agents)
        self._sim.agents = [a for a in self._sim.agents if a.id != agent_id]

        if len(self._sim.agents) == before:
            return pb.RemoveAgentResponse(
                success=False,
                error=f"Agent '{agent_id}' not found",
            )

        if hasattr(self._world, "register_agents"):
            self._world.register_agents(self._sim.agents)

        return pb.RemoveAgentResponse(success=True)

    # ── Observations ──────────────────────────────────────────────────────────

    async def SendObservation(
        self,
        request: pb.SendObservationRequest,
        context: grpc.aio.ServicerContext,
    ) -> pb.SendObservationResponse:
        if not request.agent_id:
            await context.abort(grpc.StatusCode.INVALID_ARGUMENT, "agent_id is required")

        if not hasattr(self._world, "push_observation"):
            return pb.SendObservationResponse(
                success=False,
                error="World does not support push_observation (not a HostedWorld)",
            )

        obs = struct_to_dict(request.observation)
        self._world.push_observation(request.agent_id, obs)
        return pb.SendObservationResponse(success=True)

    # ── Tick ──────────────────────────────────────────────────────────────────

    async def TickSimulation(
        self,
        request: pb.TickRequest,
        context: grpc.aio.ServicerContext,
    ) -> pb.TickResponse:
        try:
            step_req = StepRequest(
                agent_observations=[
                    AgentObservationDTO(
                        agent_id    = obs.agent_id,
                        observation = struct_to_dict(obs.observation),
                    )
                    for obs in request.agent_observations
                ],
                world_metadata = struct_to_dict(request.world_metadata),
            )
            resp = await self._session.step(step_req)

        except SessionError as exc:
            await context.abort(grpc.StatusCode.FAILED_PRECONDITION, str(exc))
        except Exception as exc:
            await context.abort(grpc.StatusCode.INTERNAL, f"Tick failed: {exc}")

        # Build pb.AgentDecision objects without paying Struct construction
        # cost for empty parameters/engine_commands (common case for many
        # actions like "idle", "wait", and any action without params).
        decisions = []
        for d in resp.decisions:
            decision = pb.AgentDecision(
                agent_id     = d.agent_id,
                agent_name   = d.agent_name,
                action       = d.action,
                outcome_text = d.outcome_text,
                error        = d.error or "",
            )
            if d.parameters:
                decision.parameters.CopyFrom(safe_dict_to_struct(d.parameters))
            if d.engine_commands:
                # Append individually to avoid an intermediate Python list of Structs
                add = decision.engine_commands.add
                for cmd in d.engine_commands:
                    if cmd:
                        add().CopyFrom(safe_dict_to_struct(cmd))
                    else:
                        add()
            decisions.append(decision)

        errors = [
            pb.AgentError(agent_id=aid, message=msg)
            for aid, msg in resp.errors
        ]
        return pb.TickResponse(tick=resp.tick, decisions=decisions, errors=errors)

    # ── Lifecycle ─────────────────────────────────────────────────────────────

    async def PauseSimulation(
        self,
        request: pb.PauseRequest,
        context: grpc.aio.ServicerContext,
    ) -> pb.PauseResponse:
        self._session.pause()
        st = self._session.status()
        return pb.PauseResponse(success=True, state=st.state)

    async def ResumeSimulation(
        self,
        request: pb.ResumeRequest,
        context: grpc.aio.ServicerContext,
    ) -> pb.ResumeResponse:
        self._session.resume()
        st = self._session.status()
        return pb.ResumeResponse(success=True, state=st.state)

    # ── Snapshot / Restore ────────────────────────────────────────────────────

    async def Snapshot(
        self,
        request: pb.SnapshotRequest,
        context: grpc.aio.ServicerContext,
    ) -> pb.SnapshotResponse:
        try:
            snap       = self._session.snapshot()
            snap_bytes = pickle.dumps(snap)
            return pb.SnapshotResponse(
                snapshot_data = snap_bytes,
                tick          = snap.tick,
                created_at    = snap.created_at,
            )
        except Exception as exc:
            await context.abort(grpc.StatusCode.INTERNAL, f"Snapshot failed: {exc}")

    async def Restore(
        self,
        request: pb.RestoreRequest,
        context: grpc.aio.ServicerContext,
    ) -> pb.RestoreResponse:
        if not request.snapshot_data:
            await context.abort(grpc.StatusCode.INVALID_ARGUMENT, "snapshot_data is required")
        try:
            snap = pickle.loads(request.snapshot_data)
            self._session.restore(snap)
            return pb.RestoreResponse(success=True, tick=snap.tick)
        except SnapshotError as exc:
            return pb.RestoreResponse(success=False, error=str(exc))
        except Exception as exc:
            return pb.RestoreResponse(success=False, error=f"Restore failed: {exc}")

    # ── Event streaming ───────────────────────────────────────────────────────

    async def StreamEvents(
        self,
        request: pb.StreamEventsRequest,
        context: grpc.aio.ServicerContext,
    ):
        """Server-side streaming: yield EventMessage for each engine event.

        The client remains connected until it disconnects or the server shuts
        down. Each event is placed on a per-stream asyncio.Queue by the
        synchronous EventBus callback and drained here.

        event_types filtering is applied in the callback to avoid enqueuing
        events the client doesn't want.
        """
        queue: asyncio.Queue[ServiceEvent] = asyncio.Queue(maxsize=self._event_queue_size)
        filter_set = set(request.event_types) if request.event_types else None
        # Track drops so we can log a single summary rather than spamming.
        drop_counter = [0]

        def _on_event(svc_event: ServiceEvent) -> None:
            if filter_set and svc_event.event_type not in filter_set:
                return
            try:
                queue.put_nowait(svc_event)
            except asyncio.QueueFull:
                # Drop the new event rather than blocking the synchronous engine.
                # Logged in batches by the consumer so we don't drown the log.
                drop_counter[0] += 1

        sub_id = self._session.subscribe(None, _on_event)
        try:
            while True:
                try:
                    svc_event = await asyncio.wait_for(queue.get(), timeout=1.0)
                except asyncio.TimeoutError:
                    # No event in the last second; loop back and wait again.
                    # gRPC aio cancels this coroutine when the client disconnects,
                    # so we don't need to poll an is_active() flag.
                    if drop_counter[0]:
                        logger.warning(
                            "StreamEvents: dropped %d events (queue capacity %d). "
                            "Increase event_queue_size or filter event_types.",
                            drop_counter[0], self._event_queue_size,
                        )
                        drop_counter[0] = 0
                    continue
                except asyncio.CancelledError:
                    break

                # Build pb.EventMessage with minimal overhead — skip the Struct
                # field entirely when event data is empty (TICK_START/TICK_END
                # commonly carry only metadata which may be empty).
                msg = pb.EventMessage(
                    session_id = svc_event.session_id,
                    event_type = svc_event.event_type,
                    tick       = svc_event.tick,
                    agent_id   = svc_event.agent_id,
                )
                if svc_event.data:
                    msg.data.CopyFrom(safe_dict_to_struct(svc_event.data))
                yield msg
        except asyncio.CancelledError:
            pass
        finally:
            if drop_counter[0]:
                logger.warning(
                    "StreamEvents: stream closing with %d dropped events.",
                    drop_counter[0],
                )
            self._session.unsubscribe(sub_id)
