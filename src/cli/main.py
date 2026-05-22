"""
src/cli/main.py
────────────────────────
CLI entry point.

Usage:
  src run sim.yaml
  python -m src.cli.main run sim.yaml
"""
from __future__ import annotations

import asyncio
import sys

from src.engine.simulation import Simulation
from src.engine.event_bus import EventLogSubscriber


async def _run(path: str) -> None:
    sim = Simulation.from_config(path)
    log = EventLogSubscriber()
    sim.bus.subscribe("*", log)
    await sim.run()

    print("\n=== Simulation complete ===")
    for line in log.tail(30):
        print(line)


def main() -> None:
    if len(sys.argv) < 3 or sys.argv[1] != "run":
        print("Usage: src run <sim.yaml>")
        sys.exit(1)
    asyncio.run(_run(sys.argv[2]))


if __name__ == "__main__":
    main()
