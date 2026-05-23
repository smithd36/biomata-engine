"""
src/transport/websocket/protocol.py
──────────────────────────────────────────────────────────────
On-the-wire protocol for the WebSocket transport.

Three frame shapes (all JSON text frames):

  Request  (client → server)
      {"type": "req", "id": "<uuid>", "method": "<name>", "params": {...}}

  Response (server → client, correlated by id)
      {"type": "res", "id": "<uuid>", "ok": true,  "result": {...}}
      {"type": "res", "id": "<uuid>", "ok": false, "error": "..."}

  Event    (server → client, unsolicited)
      {"type": "evt", "event_type": "tick_end", "tick": 5,
       "agent_id": "engine", "data": {...}, "session_id": "..."}

Why JSON, not protobuf-over-WS
──────────────────────────────
Protobuf would shave bytes but doubles the toolchain burden (codegen for Python
+ C# clients, framing protocol on top of WS text frames, a type-tag for every
message). For local-network gameplay traffic at 100–500 NPCs and 30 Hz, JSON
overhead is well below 1 ms per tick on modern hardware — far below the LLM
brain cost that dominates. JSON also stays curl-able and reproducible from the
browser console, which is invaluable when integrating new hosts.

Method constants — used by both ends of the wire — are defined here. The
Method enum mirrors the gRPC RPC surface 1:1 (HealthCheck, RegisterAgent, Tick,
Pause, Resume, Snapshot, Restore) plus the event-subscription methods.
"""
from __future__ import annotations

from typing import Any, Final


# ── Frame type tags ───────────────────────────────────────────────────────────

MSG_REQUEST:  Final[str] = "req"
MSG_RESPONSE: Final[str] = "res"
MSG_EVENT:    Final[str] = "evt"


# ── Method names ──────────────────────────────────────────────────────────────
# These mirror the gRPC RPCs. Both ends of the wire reference them by string;
# Method is a typed namespace so the constants are discoverable from the IDE.

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


# ── Frame builders ────────────────────────────────────────────────────────────
# Centralised so the wire format has exactly one definition. Use these from
# the handler to avoid drift.

def build_response(req_id: str, result: dict[str, Any]) -> dict[str, Any]:
    """Successful response. result is method-specific JSON-safe data."""
    return {
        "type":   MSG_RESPONSE,
        "id":     req_id,
        "ok":     True,
        "result": result,
    }


def build_error(req_id: str | None, error: str) -> dict[str, Any]:
    """Failure response. req_id may be None when the failure is parser-level."""
    return {
        "type":  MSG_RESPONSE,
        "id":    req_id,
        "ok":    False,
        "error": error,
    }


def build_event(
    session_id: str,
    event_type: str,
    tick:       int,
    agent_id:   str,
    data:       dict[str, Any],
) -> dict[str, Any]:
    """Unsolicited server-pushed event. Carries the same fields as the
    service-layer ServiceEvent so adapter code can map them directly."""
    return {
        "type":       MSG_EVENT,
        "session_id": session_id,
        "event_type": event_type,
        "tick":       tick,
        "agent_id":   agent_id,
        "data":       data,
    }
