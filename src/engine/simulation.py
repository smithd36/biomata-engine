"""
src/engine/simulation.py
──────────────────────────────────
Simulation is the engine's top-level orchestrator.

Owns: tick loop, scheduling, tick-level events.
Does NOT own: world logic, action semantics, social systems, display, LLM calls.

Construct via Simulation.from_config(path) or directly.
"""
from __future__ import annotations

import asyncio
import random
from dataclasses import dataclass

from src.contracts.world import World
from src.engine.agent import Agent
from src.engine.agent_runtime import AgentRuntime
from src.engine.event_bus import (
    EventBus, Event, TICK_START, TICK_END, AGENT_STEP_ERROR,
)
from src.engine.registry import ActionRegistry
from src.engine.scheduler import Scheduler, SimultaneousScheduler


@dataclass
class SimulationConfig:
    ticks:     int = 20
    seed:      int = 42
    log_level: str = "normal"   # "normal" | "verbose" | "quiet"


class Simulation:
    """
    The engine. Wire it up with your world, agents, registry, and scheduler.

        sim = Simulation(agents=..., world=..., registry=...)
        await sim.run()

    from_config() is the recommended entry point for YAML-driven sims.
    """

    def __init__(
        self,
        agents:    list[Agent],
        world:     World,
        registry:  ActionRegistry,
        bus:       EventBus         | None = None,
        scheduler: Scheduler        | None = None,
        config:    SimulationConfig  | None = None,
    ):
        self.agents    = agents
        self.world     = world
        self.registry  = registry
        self.bus       = bus       or EventBus()
        self.scheduler = scheduler or SimultaneousScheduler()
        self.config    = config    or SimulationConfig()

        # Canonical seeded RNG — injected into the world so handlers use context.rng
        self.rng = random.Random(self.config.seed)
        self.world.rng = self.rng

        self._runtime  = AgentRuntime(
            registry = self.registry,
            world    = self.world,
            bus      = self.bus,
        )

        if hasattr(self.world, "register_agents"):
            self.world.register_agents(self.agents)

    # ── Public API ────────────────────────────────────────────────────────────

    async def run(self) -> None:
        for _ in range(self.config.ticks):
            await self._tick()

    async def run_tick(self) -> None:
        """Run a single tick — useful for step-by-step external control."""
        await self._tick()

    # ── Internal ──────────────────────────────────────────────────────────────

    async def _tick(self) -> None:
        self.world.tick()
        tick = self.world.current_tick

        self.bus.emit(Event(
            type="tick_start", tick=tick, agent_id="engine",
            data=self.world.metadata,
        ))

        results = await self.scheduler.run_tick(
            agents  = self.agents,
            step_fn = self._runtime.step,
        )

        for agent, result in results:
            if isinstance(result, Exception):
                self.bus.emit(Event(
                    type=AGENT_STEP_ERROR, tick=tick, agent_id=agent.id,
                    data={"error": str(result), "agent_name": agent.name},
                ))

        self.bus.emit(Event(
            type=TICK_END, tick=tick, agent_id="engine",
            data={"agent_count": len(self.agents)},
        ))

    # ── Config-driven construction ────────────────────────────────────────────

    @classmethod
    def from_config(cls, path: str) -> "Simulation":
        """Load a simulation from a YAML config file."""
        from src.config.loader import load_simulation
        return load_simulation(path)
