"""
src/transport/websocket/handler.py
──────────────────────────────────────────────────────────────────────────────
ConnectionHandler — per-connection adapter between one WebSocket and the
shared SimulationSession.

One instance per connected client. Lifecycle:

  1. Server accepts a new WebSocket → instantiates a ConnectionHandler.
  2. run() sends the hello frame, then loops: read frames, dispatch request
     frames to session methods, send response frames back.
  3. Event frames are pushed asynchronously by an event pump started on the
     first subscribe_events call.
  4. On disconnect (clean or faulted): unsubscribes from the session, drains
     pending tasks, exits.

Protocol: see docs/websocket-protocol.md and src/transport/websocket/protocol.py

The handler never imports from src.engine — only from src.service.

Error mapping
─────────────
  ProtocolError(code)       → ok=False, structured error object with that code
  SessionError              → ok=False, ErrorCode.SESSION_ERROR
  ValueError/TypeError      → ok=False, ErrorCode.INVALID_PARAMS
  Unexpected exception      → ok=False, ErrorCode.INTERNAL_ERROR (logged WARNING)
  JSON parse failure        → ok=False, ErrorCode.PARSE_ERROR   (req_id=null)
  Unknown method            → ok=False, ErrorCode.METHOD_NOT_FOUND
  Missing method field      → ok=False, ErrorCode.INVALID_REQUEST

Backpressure
────────────
Event delivery uses an asyncio.Queue. If the consumer can't keep up the queue
saturates and new events are dropped with a single batched warning.
Default queue size: 2048.
"""
from __future__ import annotations

import asyncio
import base64
import hmac
import json
import logging
import os
import pickle
from typing import Any

from src.contracts.snapshot import SimulationSnapshot, SnapshotError
from src.engine.agent import Agent
from src.plugins.builtin.simple_memory.memory import SimpleMemory
from src.service import (
    AgentObservationDTO,
    ServiceEvent,
    SessionError,
    SimulationSession,
    StepRequest,
)
from src.transport.websocket.protocol import (
    MSG_REQUEST,
    PROTOCOL_VERSION,
    ErrorCode,
    Method,
    ProtocolError,
    build_error,
    build_event,
    build_hello,
    build_response,
)


logger = logging.getLogger(__name__)

_DEFAULT_EVENT_QUEUE_SIZE = 2048

# ── Snapshot signing ──────────────────────────────────────────────────────────
# Ephemeral key generated once per process. Snapshots are signed on the way
# out and verified on the way in, so a client cannot inject arbitrary pickle
# data even if the port is reachable from an untrusted host.
# The key is NOT persisted — snapshots from a previous process will not
# verify and must be re-created. This is intentional: the snapshot wire format
# is an internal round-trip mechanism, not a durable persistence format.
_SIGNING_KEY: bytes = os.urandom(32)


def _sign(data: bytes) -> str:
    return hmac.new(_SIGNING_KEY, data, "sha256").hexdigest()


def _verify(data: bytes, tag: str) -> bool:
    try:
        given = bytes.fromhex(tag)
    except (ValueError, TypeError):
        return False
    expected = hmac.new(_SIGNING_KEY, data, "sha256").digest()
    return hmac.compare_digest(expected, given)


def _import_dotted(dotted: str) -> Any:
    """Resolve a dotted Python path to its target (class or function)."""
    from src.config.loader import _import as _loader_import
    return _loader_import(dotted)


class ConnectionHandler:
    """
    Drives one WebSocket connection against one SimulationSession.

    The same SimulationSession instance is shared across all active
    connections (the WebSocketServer owns it). Each handler keeps its own
    event subscription so connection-scoped filtering and disconnect cleanup
    work correctly.
    """

    def __init__(
        self,
        session:          SimulationSession,
        websocket:        Any,                          # websockets.WebSocketServerProtocol
        event_queue_size: int = _DEFAULT_EVENT_QUEUE_SIZE,
    ) -> None:
        self._session     = session
        self._sim         = session._sim
        self._world       = session._sim.world
        self._ws          = websocket
        self._sub_id:     str | None = None
        self._filter:     set[str] | None = None

        self._event_queue:    asyncio.Queue[ServiceEvent] = asyncio.Queue(maxsize=event_queue_size)
        self._event_pump:     asyncio.Task | None = None
        self._event_seq:      int = 0
        self._dropped_events: int = 0

    # ── Main loop ─────────────────────────────────────────────────────────────

    async def run(self) -> None:
        """Send hello, then receive frames until the connection closes."""
        try:
            await self._send(build_hello(
                session_id = self._session.session_id,
                tick_mode  = self._session.tick_mode,
            ))
            async for raw in self._ws:
                await self._on_frame(raw)
        except Exception as exc:
            logger.debug("ws connection ended: %s", exc)
        finally:
            await self._cleanup()

    async def _cleanup(self) -> None:
        if self._sub_id is not None:
            try:
                self._session.unsubscribe(self._sub_id)
            except Exception:   # noqa: BLE001
                pass
            self._sub_id = None
        if self._event_pump is not None:
            self._event_pump.cancel()
            try:
                await self._event_pump
            except (asyncio.CancelledError, Exception):  # noqa: BLE001
                pass
            self._event_pump = None
        if self._dropped_events:
            logger.warning(
                "ws: closing with %d unsent events (consumer backpressure).",
                self._dropped_events,
            )

    # ── Frame parsing ─────────────────────────────────────────────────────────

    async def _on_frame(self, raw: str | bytes) -> None:
        if isinstance(raw, bytes):
            raw = raw.decode("utf-8", errors="replace")

        try:
            msg = json.loads(raw)
        except json.JSONDecodeError as exc:
            await self._send(build_error(None, f"invalid JSON: {exc}", ErrorCode.PARSE_ERROR))
            return

        if not isinstance(msg, dict) or msg.get("type") != MSG_REQUEST:
            return  # Ignore non-request frames

        req_id = msg.get("id")
        method = msg.get("method")
        params = msg.get("params") or {}

        # Warn on protocol version mismatch but do not reject — allows older
        # clients to keep working during a version transition.
        client_v = msg.get("v")
        if client_v is not None and client_v != PROTOCOL_VERSION:
            logger.warning(
                "ws: client protocol v%s differs from server v%s",
                client_v, PROTOCOL_VERSION,
            )

        if not isinstance(method, str):
            await self._send(build_error(req_id, "missing or invalid 'method' field",
                                         ErrorCode.INVALID_REQUEST))
            return

        try:
            result = await self._dispatch(method, params)
            await self._send(build_response(req_id, result))
        except ProtocolError as exc:
            await self._send(build_error(req_id, str(exc), exc.code))
        except SessionError as exc:
            await self._send(build_error(req_id, str(exc), ErrorCode.SESSION_ERROR))
        except (ValueError, TypeError) as exc:
            await self._send(build_error(req_id, str(exc), ErrorCode.INVALID_PARAMS))
        except Exception as exc:   # noqa: BLE001
            logger.exception("ws: unhandled error in method '%s'", method)
            await self._send(build_error(req_id, str(exc), ErrorCode.INTERNAL_ERROR))

    async def _send(self, payload: dict[str, Any]) -> None:
        await self._ws.send(json.dumps(payload, separators=(",", ":")))

    # ── Dispatch ──────────────────────────────────────────────────────────────

    async def _dispatch(self, method: str, params: dict[str, Any]) -> dict[str, Any]:
        if method == Method.HEALTH_CHECK:
            return self._handle_health_check()
        if method == Method.REGISTER_AGENT:
            return self._handle_register_agent(params)
        if method == Method.REMOVE_AGENT:
            return self._handle_remove_agent(params)
        if method == Method.SEND_OBSERVATION:
            return self._handle_send_observation(params)
        if method == Method.TICK:
            return await self._handle_tick(params)
        if method == Method.PAUSE:
            return self._handle_pause()
        if method == Method.RESUME:
            return self._handle_resume()
        if method == Method.SNAPSHOT:
            return self._handle_snapshot()
        if method == Method.RESTORE:
            return self._handle_restore(params)
        if method == Method.SUBSCRIBE_EVENTS:
            return self._handle_subscribe_events(params)
        if method == Method.UNSUBSCRIBE_EVENTS:
            return self._handle_unsubscribe_events()
        raise ProtocolError(f"unknown method '{method}'", ErrorCode.METHOD_NOT_FOUND)

    # ── Method handlers ───────────────────────────────────────────────────────

    def _handle_health_check(self) -> dict[str, Any]:
        st = self._session.status()
        return {
            "status":        "ok",
            "session_state": st.state,
            "tick":          st.tick,
            "agent_count":   st.agent_count,
            "tick_mode":     st.tick_mode,
        }

    def _handle_register_agent(self, params: dict[str, Any]) -> dict[str, Any]:
        agent_id     = params.get("agent_id")
        agent_name   = params.get("agent_name")
        brain_class  = params.get("brain_class")
        brain_kwargs = params.get("brain_config")  or {}
        memory_class = params.get("memory_class")  or ""
        memory_kw    = params.get("memory_config") or {}

        if not agent_id:
            raise ProtocolError("agent_id is required", ErrorCode.INVALID_PARAMS)
        if not agent_name:
            raise ProtocolError("agent_name is required", ErrorCode.INVALID_PARAMS)
        if not brain_class:
            raise ProtocolError("brain_class is required", ErrorCode.INVALID_PARAMS)

        if any(a.id == agent_id for a in self._sim.agents):
            raise ProtocolError(
                f"agent '{agent_id}' already registered",
                ErrorCode.AGENT_EXISTS,
            )

        try:
            brain_cls = _import_dotted(brain_class)
            brain     = brain_cls(**brain_kwargs)
            memory    = _import_dotted(memory_class)(**memory_kw) if memory_class else SimpleMemory()
            agent     = Agent(id=agent_id, name=agent_name, brain=brain, memory=memory)
        except (ImportError, AttributeError) as exc:
            raise ProtocolError(f"import error: {exc}", ErrorCode.IMPORT_ERROR) from exc
        except Exception as exc:    # noqa: BLE001
            raise ProtocolError(str(exc), ErrorCode.INVALID_PARAMS) from exc

        self._sim.agents.append(agent)
        if hasattr(self._world, "register_agents"):
            self._world.register_agents(self._sim.agents)
        return {"agent_id": agent_id}

    def _handle_remove_agent(self, params: dict[str, Any]) -> dict[str, Any]:
        agent_id = params.get("agent_id")
        if not agent_id:
            raise ProtocolError("agent_id is required", ErrorCode.INVALID_PARAMS)
        before = len(self._sim.agents)
        self._sim.agents = [a for a in self._sim.agents if a.id != agent_id]
        if len(self._sim.agents) == before:
            raise ProtocolError(f"agent '{agent_id}' not found", ErrorCode.AGENT_NOT_FOUND)
        if hasattr(self._world, "register_agents"):
            self._world.register_agents(self._sim.agents)
        return {"agent_id": agent_id}

    def _handle_send_observation(self, params: dict[str, Any]) -> dict[str, Any]:
        agent_id = params.get("agent_id")
        obs      = params.get("observation") or {}
        if not agent_id:
            raise ProtocolError("agent_id is required", ErrorCode.INVALID_PARAMS)
        if not hasattr(self._world, "push_observation"):
            raise ProtocolError(
                "world does not support push_observation (requires HostedWorld)",
                ErrorCode.INVALID_PARAMS,
            )
        self._world.push_observation(agent_id, obs)
        return {"agent_id": agent_id}

    async def _handle_tick(self, params: dict[str, Any]) -> dict[str, Any]:
        obs_list = params.get("agent_observations") or []
        metadata = params.get("world_metadata")    or {}

        step_req = StepRequest(
            agent_observations = [
                AgentObservationDTO(
                    agent_id    = o.get("agent_id"),
                    observation = o.get("observation") or {},
                )
                for o in obs_list
                if isinstance(o, dict) and o.get("agent_id")
            ],
            world_metadata = metadata,
        )
        resp = await self._session.step(step_req)
        return {
            "tick": resp.tick,
            "decisions": [
                {
                    "agent_id":        d.agent_id,
                    "agent_name":      d.agent_name,
                    "action":          d.action,
                    "parameters":      d.parameters,
                    "outcome_text":    d.outcome_text,
                    "engine_commands": d.engine_commands,
                    "error":           d.error,
                }
                for d in resp.decisions
            ],
            "errors": [{"agent_id": aid, "message": msg} for aid, msg in resp.errors],
        }

    def _handle_pause(self) -> dict[str, Any]:
        self._session.pause()
        return {"state": self._session.status().state}

    def _handle_resume(self) -> dict[str, Any]:
        self._session.resume()
        return {"state": self._session.status().state}

    def _handle_snapshot(self) -> dict[str, Any]:
        snap = self._session.snapshot()
        raw  = pickle.dumps(snap)
        return {
            "data_b64":    base64.b64encode(raw).decode("ascii"),
            "hmac_sha256": _sign(raw),
            "tick":        snap.tick,
            "created_at":  snap.created_at,
        }

    def _handle_restore(self, params: dict[str, Any]) -> dict[str, Any]:
        b64 = params.get("data_b64")
        tag = params.get("hmac_sha256")
        if not isinstance(b64, str):
            raise ProtocolError("data_b64 (base64 string) is required", ErrorCode.INVALID_PARAMS)

        try:
            raw = base64.b64decode(b64, validate=True)
        except Exception:
            logger.warning("ws: snapshot restore rejected — invalid base64 encoding")
            raise ProtocolError("data_b64 is not valid base64", ErrorCode.SNAPSHOT_INVALID)

        if not isinstance(tag, str) or not _verify(raw, tag):
            logger.warning("ws: snapshot restore rejected — HMAC verification failed")
            raise ProtocolError(
                "snapshot HMAC verification failed — "
                "snapshot was not produced by this server process",
                ErrorCode.SNAPSHOT_INVALID,
            )

        try:
            snap = pickle.loads(raw)
        except Exception as exc:
            logger.warning("ws: snapshot restore rejected — deserialization failed: %s", exc)
            raise ProtocolError(
                "snapshot data could not be deserialized", ErrorCode.SNAPSHOT_INVALID
            ) from exc

        if not isinstance(snap, SimulationSnapshot):
            logger.warning(
                "ws: snapshot restore rejected — unexpected type %r", type(snap).__name__
            )
            raise ProtocolError(
                f"snapshot has unexpected type {type(snap).__name__!r}",
                ErrorCode.SNAPSHOT_INVALID,
            )

        try:
            self._session.restore(snap)
        except SnapshotError as exc:
            raise ProtocolError(str(exc), ErrorCode.INTERNAL_ERROR) from exc

        return {"tick": snap.tick}

    # ── Event subscription ────────────────────────────────────────────────────

    def _handle_subscribe_events(self, params: dict[str, Any]) -> dict[str, Any]:
        # Idempotent — re-subscribing with a new filter updates the filter.
        types = params.get("event_types")
        self._filter = set(types) if types else None
        if self._sub_id is None:
            self._sub_id = self._session.subscribe(None, self._on_session_event)
        if self._event_pump is None:
            self._event_pump = asyncio.create_task(self._pump_events())
        return {"subscribed": True, "event_types": list(self._filter or [])}

    def _handle_unsubscribe_events(self) -> dict[str, Any]:
        if self._sub_id is not None:
            self._session.unsubscribe(self._sub_id)
            self._sub_id = None
        return {"unsubscribed": True}

    def _on_session_event(self, ev: ServiceEvent) -> None:
        """Called synchronously from the engine bus — must not block."""
        if self._filter and ev.event_type not in self._filter:
            return
        try:
            self._event_queue.put_nowait(ev)
        except asyncio.QueueFull:
            self._dropped_events += 1

    async def _pump_events(self) -> None:
        """Drain queued events to the WebSocket. One task per handler."""
        try:
            while True:
                ev = await self._event_queue.get()
                self._event_seq += 1
                frame = build_event(
                    session_id = ev.session_id,
                    event_type = ev.event_type,
                    tick       = ev.tick,
                    agent_id   = ev.agent_id,
                    data       = ev.data,
                    seq        = self._event_seq,
                )
                try:
                    await self._ws.send(json.dumps(frame, separators=(",", ":")))
                except Exception:   # noqa: BLE001
                    return
        except asyncio.CancelledError:
            return
