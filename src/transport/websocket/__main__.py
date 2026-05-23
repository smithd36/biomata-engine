"""
biomata-ws — standalone entry point for the WebSocket transport.

Usage
─────
    biomata-ws --config examples/corporate/sim.yaml --port 8765
"""
from __future__ import annotations

import argparse
import asyncio
import logging

from src.transport.websocket.server import WebSocketServer


def _parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(description="biomata-engine WebSocket server")
    p.add_argument("--config",   required=True, help="Path to sim.yaml")
    p.add_argument("--host",     default="0.0.0.0",
                   help="Bind host (default: 0.0.0.0 = all interfaces)")
    p.add_argument("--port",     type=int, default=8765,
                   help="Port (default: 8765)")
    p.add_argument("--log-level", default="INFO",
                   choices=["DEBUG", "INFO", "WARNING", "ERROR"])
    return p.parse_args()


async def _main() -> None:
    args = _parse_args()
    logging.basicConfig(
        level  = getattr(logging, args.log_level),
        format = "%(asctime)s %(levelname)s %(name)s — %(message)s",
    )
    server = await WebSocketServer.from_config(
        args.config, host=args.host, port=args.port,
    )
    await server.serve()


def main() -> None:
    """Console entry point — registered as biomata-ws in pyproject.toml."""
    asyncio.run(_main())


if __name__ == "__main__":
    main()
