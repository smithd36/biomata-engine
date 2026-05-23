"""
src/transport/grpc/conversions.py
──────────────────────────────────────────────────────────────
Conversion helpers between Python dicts and protobuf Struct messages.

All inbound proto Structs (observations, metadata, parameters) go through
struct_to_dict() before entering the service layer.

All outbound dicts (decisions, engine_commands, event data) go through
dict_to_struct() before being placed in proto response messages.

Protobuf Struct only supports JSON-serializable values:
  str, float/int, bool, None, nested dicts (→ Struct), lists (→ ListValue).

Non-serializable values in an outbound dict are coerced to str() by
safe_dict_to_struct() so the transport never raises on unexpected data.

Performance
───────────
At 100–500 agents, the outbound path runs N×(parameters + engine_commands)
times per tick plus once per event. safe_dict_to_struct() optimistically
tries Struct.update() — which is the fast C-extension path — and only
falls back to the recursive _sanitize() copy on TypeError/ValueError.
This avoids paying the recursive-allocation cost for payloads that are
already JSON-safe (the common case).
"""
from __future__ import annotations

from typing import Any

from google.protobuf import json_format
from google.protobuf.struct_pb2 import Struct


def struct_to_dict(s: Struct) -> dict[str, Any]:
    """Convert a protobuf Struct to a Python dict.

    Returns an empty dict for an empty or missing Struct.
    """
    if s is None:
        return {}
    return json_format.MessageToDict(s)


def dict_to_struct(d: dict[str, Any] | None) -> Struct:
    """Convert a Python dict to a protobuf Struct.

    All values must be JSON-serializable (str, int, float, bool, None,
    list, dict). Raises ValueError on non-serializable values.
    """
    s = Struct()
    if d:
        s.update(d)
    return s


def safe_dict_to_struct(d: dict[str, Any] | None) -> Struct:
    """Like dict_to_struct but coerces non-serializable values to str().

    Optimistic fast path: most outbound dicts are already JSON-safe (string
    keys, primitive/list/dict values). Struct.update() handles them in the
    protobuf C extension without an extra Python-side copy. We only pay the
    recursive _sanitize() cost when update() actually raises.
    """
    s = Struct()
    if not d:
        return s
    try:
        s.update(d)
    except (TypeError, ValueError):
        # Some value wasn't JSON-compatible — fall back to recursive coercion.
        s = Struct()
        s.update(_sanitize(d))
    return s


def _sanitize(obj: Any) -> Any:
    """Recursively coerce non-JSON-compatible values to str."""
    if isinstance(obj, dict):
        return {k: _sanitize(v) for k, v in obj.items()}
    if isinstance(obj, list):
        return [_sanitize(v) for v in obj]
    if isinstance(obj, (str, int, float, bool)) or obj is None:
        return obj
    return str(obj)
