"""
src/transport/websocket/
──────────────────────────────────────────────────────────────
WebSocket transport for the biomata-engine simulation service.

A parallel transport to the gRPC one — both target the same service-layer
SimulationSession, so the engine, world, and brain logic are completely
unaware of which transport drives them.

When to choose WebSocket over gRPC
──────────────────────────────────
- Unity / game engine clients (gRPC's Grpc.Net.Client has fragile compile-time
  dependencies on Unity 6's restricted .NET Standard 2.1 reference assemblies).
- Browser / WebGL clients (WebSocket is natively supported; HTTP/2 sockets
  needed by gRPC are not).
- Simple debugging / curl-able protocol — JSON over WS is text-readable.

When to keep gRPC
─────────────────
- Server-to-server or research integrations where typed proto contracts and
  HTTP/2 streaming flow control matter.
- Any client where Grpc.Net.Client or a generated stub already works cleanly.

Public entry points
───────────────────
  WebSocketServer     — start/stop a WebSocket server bound to one SimulationSession.
  start_from_config() — convenience for the biomata-ws CLI.
"""
from src.transport.websocket.server   import WebSocketServer
from src.transport.websocket.protocol import (
    MSG_REQUEST,
    MSG_RESPONSE,
    MSG_EVENT,
    Method,
)

__all__ = [
    "WebSocketServer",
    "MSG_REQUEST",
    "MSG_RESPONSE",
    "MSG_EVENT",
    "Method",
]
