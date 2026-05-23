"""
src/transport/websocket/server.py
──────────────────────────────────────────────────────────────
WebSocketServer — lifecycle wrapper around `websockets.serve`.

Mirrors the GrpcServer surface (start, stop, serve, from_config,
from_simulation) so transport-agnostic tests and embedding code work the
same way against either transport.

Usage — programmatic
────────────────────
    from src.plugins.external.world import HostedWorld
    from src.engine.simulation     import Simulation
    from src.service               import create_session
    from src.transport.websocket   import WebSocketServer

    world   = HostedWorld()
    sim     = Simulation(agents=[...], world=world, registry=registry)
    session = create_session(sim)
    server  = WebSocketServer(session, port=8765)
    await server.start()
    await server.wait_for_termination()

Usage — from YAML
─────────────────
    server = await WebSocketServer.from_config("sim.yaml", port=8765)
    await server.start()
    await server.wait_for_termination()

Standalone process
──────────────────
    biomata-ws --config sim.yaml --port 8765
"""
from __future__ import annotations

import asyncio
import logging
import signal
from typing import Any

import websockets

from src.service import SimulationSession, create_session
from src.transport.websocket.handler import ConnectionHandler


logger = logging.getLogger(__name__)


class WebSocketServer:
    """
    Hosts one SimulationSession over a WebSocket port.

    A single SimulationSession is shared across all active connections —
    same model as the gRPC transport. Each connection gets its own
    ConnectionHandler with its own event subscription and queue.

    Parameters
    ──────────
    session            — the SimulationSession to expose.
    host               — bind host (default "0.0.0.0" = all interfaces).
    port               — port number (8765 by default; 0 = OS-assigned).
    event_queue_size   — per-connection event buffer; drops with logged
                         warning when exceeded.
    max_size           — max WebSocket frame size in bytes (default 16 MiB —
                         needed for large snapshot frames).
    """

    def __init__(
        self,
        session:          SimulationSession,
        host:             str = "0.0.0.0",
        port:             int = 8765,
        event_queue_size: int = 2048,
        max_size:         int = 16 * 1024 * 1024,
    ) -> None:
        self._session          = session
        self._host             = host
        self._port             = port
        self._event_queue_size = event_queue_size
        self._max_size         = max_size
        self._server:          Any = None
        self._bound_port:      int | None = None

    # ── Lifecycle ─────────────────────────────────────────────────────────────

    async def start(self) -> int:
        """Bind and start accepting connections. Returns the bound port."""
        self._server = await websockets.serve(
            self._on_connection,
            host       = self._host,
            port       = self._port,
            max_size   = self._max_size,
            # Disable per-message permessage-deflate by default — for local
            # gameplay traffic the CPU cost outweighs the bandwidth saving.
            compression = None,
        )

        # websockets >= 12 exposes sockets via .sockets; pick the first one
        # to report the bound port (matters when port=0 was requested).
        socks = getattr(self._server, "sockets", None) or []
        if socks:
            self._bound_port = socks[0].getsockname()[1]
        else:
            self._bound_port = self._port

        logger.info(
            "WebSocket server listening on ws://%s:%d",
            self._host, self._bound_port,
        )
        return self._bound_port

    async def stop(self) -> None:
        """Close the server and wait for in-flight connections to drain."""
        if self._server is None:
            return
        self._server.close()
        await self._server.wait_closed()
        logger.info("WebSocket server stopped")

    async def wait_for_termination(self) -> None:
        """Block until the server is closed (use after start() in a long-running process)."""
        if self._server is None:
            return
        await self._server.wait_closed()

    @property
    def port(self) -> int | None:
        return self._bound_port

    # ── Connection handler ──────────────────────────────────────────────────

    async def _on_connection(self, ws: Any) -> None:
        peer = getattr(ws, "remote_address", None)
        logger.debug("ws: connection from %s", peer)
        handler = ConnectionHandler(
            session          = self._session,
            websocket        = ws,
            event_queue_size = self._event_queue_size,
        )
        try:
            await handler.run()
        except Exception:    # noqa: BLE001
            logger.exception("ws: unhandled error from %s", peer)
        finally:
            logger.debug("ws: disconnected %s", peer)

    # ── Factory methods ───────────────────────────────────────────────────────

    @classmethod
    def from_simulation(
        cls,
        simulation: Any,
        session_id: str | None = None,
        **kwargs: Any,
    ) -> "WebSocketServer":
        """Create a WebSocketServer from a pre-built Simulation."""
        session = create_session(simulation, session_id=session_id)
        return cls(session, **kwargs)

    @classmethod
    async def from_config(
        cls,
        config_path: str,
        session_id:  str | None = None,
        **kwargs:    Any,
    ) -> "WebSocketServer":
        """Create a WebSocketServer from a YAML sim config."""
        from src.engine.simulation import Simulation
        sim = Simulation.from_config(config_path)
        return cls.from_simulation(sim, session_id=session_id, **kwargs)

    # ── Convenience: run until signal ─────────────────────────────────────────

    async def serve(self) -> None:
        """Start, block on SIGINT/SIGTERM, then stop cleanly."""
        await self.start()
        loop = asyncio.get_running_loop()
        stop_event = asyncio.Event()

        def _signal_handler() -> None:
            logger.info("shutdown signal received")
            stop_event.set()

        for sig in (signal.SIGINT, signal.SIGTERM):
            try:
                loop.add_signal_handler(sig, _signal_handler)
            except (NotImplementedError, OSError):
                # Windows doesn't support add_signal_handler for all signals
                pass

        await stop_event.wait()
        await self.stop()
