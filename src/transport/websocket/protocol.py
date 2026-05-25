"""
src/transport/websocket/protocol.py
──────────────────────────────────────────────────────────────────────────────
Biomata WebSocket Protocol — v1

Authoritative Python-side definitions for the wire format.  All frame builders
live here so the handler never constructs raw dicts and the format has exactly
one definition.

See docs/websocket-protocol.md for the full human-readable spec.

Frame shapes (all JSON text frames, UTF-8):

  Server Hello (server → client, sent once on connect)
      {"type":"hlo", "v":1, "server":"biomata-engine",
       "server_version":"0.5.0", "session_id":"<uuid>",
       "capabilities":["tick","events","snapshot",...]}

  Request  (client → server)
      {"type":"req", "v":1, "id":"<uuid>", "method":"<name>", "params":{}}

  Response — success (server → client, correlated by id)
      {"type":"res", "v":1, "id":"<uuid>", "ok":true, "result":{}}

  Response — error (server → client, correlated by id)
      {"type":"res", "v":1, "id":"<uuid>", "ok":false,
       "error":{"code":-32601, "name":"METHOD_NOT_FOUND", "message":"..."}}

  Event    (server → client, unsolicited)
      {"type":"evt", "v":1, "session_id":"<uuid>", "seq":42,
       "event_type":"tick_end", "tick":5, "agent_id":"engine",
       "ts":"2026-05-23T14:32:01.123Z", "data":{}}

Why JSON, not protobuf-over-WS
──────────────────────────────
Protobuf would shave bytes but doubles the toolchain burden (codegen for Python
+ N client languages, a framing layer on top of WS text frames, a type-tag per
message). For local-network gameplay traffic at 100–500 NPCs / 30 Hz, JSON
overhead is well below 1 ms per tick — far below LLM brain latency that
dominates real workloads. JSON also stays curl-able and browser-debuggable,
which is invaluable when integrating a new host engine.
"""
from __future__ import annotations

from datetime import datetime, timezone
from typing import Any, Final

from src.service.interfaces import TickMode


# ── Protocol version ──────────────────────────────────────────────────────────

PROTOCOL_VERSION: Final[int] = 1
SERVER_VERSION:   Final[str] = "0.5.0"

# Capabilities advertised per tick mode.
# host_driven: tick is valid; pause/resume are not.
# autonomous:  pause/resume are valid; tick is not.
_CAPABILITIES_HOST_DRIVEN: Final[list[str]] = [
    "tick",
    "register_agent",
    "remove_agent",
    "send_observation",
    "snapshot",
    "restore",
    "events",
]
_CAPABILITIES_AUTONOMOUS: Final[list[str]] = [
    "pause",
    "resume",
    "register_agent",
    "remove_agent",
    "send_observation",
    "snapshot",
    "restore",
    "events",
]


# ── Frame type tags ───────────────────────────────────────────────────────────

MSG_HELLO:    Final[str] = "hlo"
MSG_REQUEST:  Final[str] = "req"
MSG_RESPONSE: Final[str] = "res"
MSG_EVENT:    Final[str] = "evt"


# ── Method names ──────────────────────────────────────────────────────────────
# Both ends reference methods by these string constants. The surface mirrors
# the gRPC RPC names 1:1 so adapters can map them without a lookup table.

class Method:
    HEALTH_CHECK       = "health_check"
    REGISTER_AGENT     = "register_agent"
    REMOVE_AGENT       = "remove_agent"
    SEND_OBSERVATION   = "send_observation"
    TICK               = "tick"
    PAUSE              = "pause"
    RESUME             = "resume"
    SNAPSHOT           = "snapshot"
    RESTORE            = "restore"
    SUBSCRIBE_EVENTS   = "subscribe_events"
    UNSUBSCRIBE_EVENTS = "unsubscribe_events"

    ALL: tuple[str, ...] = (
        HEALTH_CHECK, REGISTER_AGENT, REMOVE_AGENT, SEND_OBSERVATION,
        TICK, PAUSE, RESUME, SNAPSHOT, RESTORE,
        SUBSCRIBE_EVENTS, UNSUBSCRIBE_EVENTS,
    )


# ── Error codes ───────────────────────────────────────────────────────────────
# Transport-level error codes (-32xxx borrowed from JSON-RPC convention).
# Domain-level codes use small negative integers (-1 through -99).

class ErrorCode:
    # Transport / protocol errors
    PARSE_ERROR      = -32700   # Malformed JSON received
    INVALID_REQUEST  = -32600   # Required envelope field missing or wrong type
    METHOD_NOT_FOUND = -32601   # Unknown method string
    INVALID_PARAMS   = -32602   # Method params missing or invalid
    INTERNAL_ERROR   = -32603   # Unhandled server-side exception
    VERSION_MISMATCH = -32000   # Client protocol version not supported by server

    # Domain errors
    SESSION_ERROR    = -1       # Operation not valid in current session state
    AGENT_EXISTS     = -2       # agent_id already registered in this session
    AGENT_NOT_FOUND  = -3       # agent_id not present in this session
    IMPORT_ERROR     = -4       # Python dotted-path import failed
    SNAPSHOT_INVALID = -5       # Snapshot HMAC verification failed or data is malformed

    _NAMES: dict[int, str] = {
        -32700: "PARSE_ERROR",
        -32600: "INVALID_REQUEST",
        -32601: "METHOD_NOT_FOUND",
        -32602: "INVALID_PARAMS",
        -32603: "INTERNAL_ERROR",
        -32000: "VERSION_MISMATCH",
        -1:     "SESSION_ERROR",
        -2:     "AGENT_EXISTS",
        -3:     "AGENT_NOT_FOUND",
        -4:     "IMPORT_ERROR",
        -5:     "SNAPSHOT_INVALID",
    }

    @classmethod
    def name(cls, code: int) -> str:
        return cls._NAMES.get(code, "UNKNOWN_ERROR")


# ── Protocol exception ────────────────────────────────────────────────────────

class ProtocolError(Exception):
    """Raised inside dispatch handlers to produce a typed error response."""

    def __init__(self, message: str, code: int = ErrorCode.INTERNAL_ERROR) -> None:
        super().__init__(message)
        self.code = code


# ── Frame builders ────────────────────────────────────────────────────────────
# Use these exclusively — never construct raw dicts in the handler.

def build_hello(
    session_id: str,
    tick_mode:  TickMode = TickMode.HOST_DRIVEN,
) -> dict[str, Any]:
    """Server-initiated frame sent immediately on connect."""
    caps = (
        _CAPABILITIES_HOST_DRIVEN
        if tick_mode == TickMode.HOST_DRIVEN
        else _CAPABILITIES_AUTONOMOUS
    )
    return {
        "type":           MSG_HELLO,
        "v":              PROTOCOL_VERSION,
        "server":         "biomata-engine",
        "server_version": SERVER_VERSION,
        "session_id":     session_id,
        "tick_mode":      tick_mode.value,
        "capabilities":   caps,
    }


def build_response(req_id: str, result: dict[str, Any]) -> dict[str, Any]:
    """Successful response. result is method-specific, JSON-safe data."""
    return {
        "type":   MSG_RESPONSE,
        "v":      PROTOCOL_VERSION,
        "id":     req_id,
        "ok":     True,
        "result": result,
    }


def build_error(
    req_id:  str | None,
    message: str,
    code:    int = ErrorCode.INTERNAL_ERROR,
) -> dict[str, Any]:
    """Failure response. req_id may be None for parse-level failures."""
    return {
        "type": MSG_RESPONSE,
        "v":    PROTOCOL_VERSION,
        "id":   req_id,
        "ok":   False,
        "error": {
            "code":    code,
            "name":    ErrorCode.name(code),
            "message": message,
        },
    }


def build_event(
    session_id: str,
    event_type: str,
    tick:       int,
    agent_id:   str,
    data:       dict[str, Any],
    seq:        int = 0,
) -> dict[str, Any]:
    """Unsolicited server-pushed event.

    seq  — per-connection monotonically increasing counter; lets clients
           detect dropped frames.
    ts   — server-side UTC ISO-8601 timestamp with millisecond resolution.
    """
    return {
        "type":       MSG_EVENT,
        "v":          PROTOCOL_VERSION,
        "session_id": session_id,
        "seq":        seq,
        "event_type": event_type,
        "tick":       tick,
        "agent_id":   agent_id,
        "ts":         _now_iso(),
        "data":       data,
    }


def _now_iso() -> str:
    return (
        datetime.now(timezone.utc)
        .isoformat(timespec="milliseconds")
        .replace("+00:00", "Z")
    )
