"""
src/transport/grpc/server.py
──────────────────────────────────────────────────────────────
GrpcServer: lifecycle wrapper for the async gRPC server.

Usage — programmatic (recommended for tests and embedding):

    world   = HostedWorld()
    sim     = Simulation(agents=[...], world=world, registry=registry)
    session = create_session(sim)
    server  = GrpcServer(session, port=50051)

    await server.start()
    await server.wait_for_termination()   # blocks until shutdown

Usage — from YAML config:

    server = await GrpcServer.from_config("sim.yaml", port=50051)
    await server.start()
    await server.wait_for_termination()

Usage — standalone process (python -m src.transport.grpc):

    biomata-grpc --config sim.yaml --port 50051

TLS / auth
──────────
Pass a ``credentials`` argument (grpc.ChannelCredentials) to use TLS.
For mTLS, construct grpc.ssl_server_credentials() and pass it in.
Default is insecure (no TLS) — suitable for localhost / Unity same-machine.

Graceful shutdown
─────────────────
stop(grace=5.0) sends a grace period during which in-flight RPCs complete
before the server is forcibly terminated.
"""
from __future__ import annotations

import asyncio
import logging
import signal
from typing import Any

import grpc
import grpc.aio

from src.service import SimulationSession, create_session
from src.transport.grpc.generated.simulation_pb2_grpc import (
    add_SimulationServiceServicer_to_server,
)
from src.transport.grpc.servicer import SimulationServicer

logger = logging.getLogger(__name__)


class GrpcServer:
    """
    Manages the lifecycle of an async gRPC server for one simulation session.

    Parameters
    ──────────
    session     — SimulationSession to expose over gRPC.
    host        — bind address (default "[::]:50051" = all interfaces, IPv4+6).
    port        — port number (ignored if host already contains a port).
    credentials — grpc.ServerCredentials for TLS; None = insecure.
    options     — list of (key, value) gRPC channel options.
    max_workers — thread pool size for the gRPC server executor.
    """

    def __init__(
        self,
        session:     SimulationSession,
        host:        str  = "[::]",
        port:        int  = 50051,
        credentials: Any  = None,
        options:     list | None = None,
        max_workers: int  = 4,
    ) -> None:
        self._session     = session
        self._host        = host
        self._port        = port
        self._credentials = credentials
        self._options     = options or [
            ("grpc.max_send_message_length",    64 * 1024 * 1024),   # 64 MB
            ("grpc.max_receive_message_length", 64 * 1024 * 1024),
        ]
        self._max_workers = max_workers
        self._server: grpc.aio.Server | None = None
        self._bound_port: int | None = None

    # ── Lifecycle ─────────────────────────────────────────────────────────────

    async def start(self) -> int:
        """
        Start the gRPC server and begin accepting connections.

        Returns the port the server is listening on (useful when port=0 is
        used to request an OS-assigned ephemeral port in tests).
        """
        servicer   = SimulationServicer(self._session)
        self._server = grpc.aio.server(options=self._options)

        add_SimulationServiceServicer_to_server(servicer, self._server)

        listen_addr = f"{self._host}:{self._port}"
        if self._credentials:
            self._bound_port = self._server.add_secure_port(
                listen_addr, self._credentials
            )
        else:
            self._bound_port = self._server.add_insecure_port(listen_addr)

        await self._server.start()
        logger.info("gRPC server listening on %s (port %d)", listen_addr, self._bound_port)
        return self._bound_port

    async def stop(self, grace: float = 5.0) -> None:
        """Initiate graceful shutdown. In-flight RPCs have `grace` seconds to complete."""
        if self._server:
            await self._server.stop(grace)
            logger.info("gRPC server stopped")

    async def wait_for_termination(self) -> None:
        """Block until the server terminates (use after start() in a long-running process)."""
        if self._server:
            await self._server.wait_for_termination()

    @property
    def port(self) -> int | None:
        """Actual bound port — available after start()."""
        return self._bound_port

    # ── Factory methods ───────────────────────────────────────────────────────

    @classmethod
    def from_simulation(
        cls,
        simulation: Any,
        session_id: str | None = None,
        **kwargs: Any,
    ) -> "GrpcServer":
        """Create a GrpcServer from a pre-built Simulation."""
        session = create_session(simulation, session_id=session_id)
        return cls(session, **kwargs)

    @classmethod
    async def from_config(
        cls,
        config_path: str,
        session_id:  str | None = None,
        **kwargs: Any,
    ) -> "GrpcServer":
        """
        Create a GrpcServer from a YAML sim config file.

        Equivalent to:
            sim = Simulation.from_config(config_path)
            GrpcServer.from_simulation(sim, **kwargs)
        """
        from src.engine.simulation import Simulation
        sim = Simulation.from_config(config_path)
        return cls.from_simulation(sim, session_id=session_id, **kwargs)

    # ── Convenience: run until signal ─────────────────────────────────────────

    async def serve(self) -> None:
        """
        Start the server and block until SIGINT or SIGTERM.

        Suitable for standalone process usage:

            asyncio.run(server.serve())
        """
        await self.start()
        loop = asyncio.get_running_loop()

        stop_event = asyncio.Event()

        def _signal_handler():
            logger.info("Shutdown signal received")
            stop_event.set()

        for sig in (signal.SIGINT, signal.SIGTERM):
            try:
                loop.add_signal_handler(sig, _signal_handler)
            except (NotImplementedError, OSError):
                # Windows doesn't support add_signal_handler for all signals
                pass

        await stop_event.wait()
        await self.stop()


# ── Standalone entry point ────────────────────────────────────────────────────

def _parse_args() -> Any:
    import argparse
    p = argparse.ArgumentParser(description="biomata-engine gRPC server")
    p.add_argument("--config", required=True, help="Path to sim.yaml")
    p.add_argument("--host",   default="[::]", help="Bind host (default: [::] = all interfaces)")
    p.add_argument("--port",   type=int, default=50051, help="Port (default: 50051)")
    p.add_argument("--log-level", default="INFO",
                   choices=["DEBUG", "INFO", "WARNING", "ERROR"])
    return p.parse_args()


async def _main() -> None:
    args = _parse_args()
    logging.basicConfig(
        level    = getattr(logging, args.log_level),
        format   = "%(asctime)s %(levelname)s %(name)s — %(message)s",
    )
    server = await GrpcServer.from_config(args.config, host=args.host, port=args.port)
    await server.serve()


def main() -> None:
    """Console entry point: biomata-grpc --config sim.yaml --port 50051"""
    asyncio.run(_main())
