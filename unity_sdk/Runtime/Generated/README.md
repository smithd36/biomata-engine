# Runtime/Generated/

This directory is reserved for future auto-generated transport stubs.

The Biomata SDK currently uses JSON over WebSocket as its sole transport. No
code generation is required — the wire format is defined in
`docs/websocket-protocol.md` and implemented in
`src/transport/websocket/protocol.py` (server) and
`Runtime/Transport/WebSocketTransport.cs` (client).
