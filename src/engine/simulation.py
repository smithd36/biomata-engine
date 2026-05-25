"""
src/engine/simulation.py
──────────────────────────────────
Simulation is the engine's top-level orchestrator.

Owns: tick loop, scheduling, tick-level events, snapshot/restore.
Does NOT own: world logic, action semantics, social systems, display, LLM calls.

Construct via Simulation.from_config(path) or directly.
"""
from __future__ import annotations

import asyncio
import logging
import random
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

_logger = logging.getLogger(__name__)

from src.contracts.action import Intent, ActionResult
from src.contracts.world import World
from src.engine.agent import Agent
from src.engine.agent_runtime import AgentRuntime
from src.engine.event_bus import (
    EventBus, Event, TICK_START, TICK_END, AGENT_STEP_ERROR,
)
from src.engine.obs_registry import ObservationRegistry
from src.engine.registry import ActionRegistry
from src.engine.scheduler import Scheduler, SimultaneousScheduler


@dataclass
class SimulationConfig:
    ticks:     int = 20
    seed:      int = 42
    log_level: str = "normal"   # "normal" | "verbose" | "quiet"


# ── Per-tick result types ─────────────────────────────────────────────────────

@dataclass
class AgentTickResult:
    """Outcome of a single agent's step within one tick."""
    agent_id:   str
    agent_name: str
    intent:     Intent
    result:     ActionResult


@dataclass
class TickSummary:
    """
    Aggregated results for a completed tick.

    Returned by Simulation.run_tick() so integrators (external-world bridges,
    test harnesses, step-debuggers) can inspect every decision without
    subscribing to the EventBus.

    engine_commands() is a convenience accessor over agent_results that
    collects all ActionResult.engine_commands in step order — useful when
    the integrator needs to relay decisions back to a host engine.
    """
    tick:          int
    agent_results: list[AgentTickResult]
    errors:        list[tuple[str, str]]   # (agent_id, error_message)

    def engine_commands(self) -> list[dict[str, Any]]:
        """All engine_commands from every agent this tick, in step order."""
        return [
            cmd
            for ar in self.agent_results
            for cmd in ar.result.engine_commands
        ]


# ── Simulation ────────────────────────────────────────────────────────────────

class Simulation:
    """
    The engine. Wire it up with your world, agents, registry, and scheduler.

        sim = Simulation(agents=..., world=..., registry=...)
        await sim.run()

    For external-world integration, drive tick-by-tick:

        world.push_observation(agent_id, obs)
        summary = await sim.run_tick()
        commands = summary.engine_commands()

    For save/restore:

        snap = sim.snapshot()
        sim.restore(snap)
        sim.save_snapshot("checkpoints/tick_10.pkl")
        sim.load_snapshot("checkpoints/tick_10.pkl")

    from_config() is the recommended entry point for YAML-driven sims.
    """

    def __init__(
        self,
        agents:       list[Agent],
        world:        World,
        registry:     ActionRegistry,
        bus:          EventBus              | None = None,
        scheduler:    Scheduler             | None = None,
        config:       SimulationConfig       | None = None,
        social:       Any                   | None = None,
        obs_registry: ObservationRegistry   | None = None,
    ):
        self.agents       = agents
        self.world        = world
        self.registry     = registry
        self.bus          = bus          or EventBus()
        self.scheduler    = scheduler    or SimultaneousScheduler()
        self.config       = config       or SimulationConfig()
        self.social       = social       # SocialSystem | None — held for snapshot support
        self.obs_registry = obs_registry # ObservationRegistry | None

        # Canonical seeded RNG — injected into the world so handlers use context.rng
        self.rng = random.Random(self.config.seed)
        self.world.rng = self.rng

        self._runtime  = AgentRuntime(
            registry     = self.registry,
            world        = self.world,
            bus          = self.bus,
            obs_registry = self.obs_registry,
        )

        if hasattr(self.world, "register_agents"):
            self.world.register_agents(self.agents)

    # ── Public API ────────────────────────────────────────────────────────────

    def close(self) -> None:
        """
        Release resources held by agents' brains.

        Calls brain.close() on every agent whose brain implements the
        Closeable protocol (src.contracts.brain.Closeable).  Errors from
        individual close() calls are logged and skipped so all brains are
        always attempted.

        Use as a context manager for automatic teardown:

            with Simulation(...) as sim:
                await sim.run()
        """
        from src.contracts.brain import Closeable
        for agent in self.agents:
            if isinstance(agent.brain, Closeable):
                try:
                    agent.brain.close()
                except Exception as exc:
                    _logger.warning(
                        "Brain.close() raised for agent %r: %s",
                        agent.id,
                        exc,
                        exc_info=True,
                    )

    def __enter__(self) -> "Simulation":
        return self

    def __exit__(self, *_: object) -> None:
        self.close()

    async def run(self) -> None:
        """Run all configured ticks to completion."""
        for _ in range(self.config.ticks):
            await self._tick()

    async def run_tick(self) -> TickSummary:
        """
        Run a single tick and return a structured summary of every agent's
        decision and outcome.

        Intended for external-world integrations and step-by-step control:

            summary = await sim.run_tick()
            for ar in summary.agent_results:
                print(ar.agent_id, ar.intent.action, ar.result.outcome_text)
            commands = summary.engine_commands()  # relay to host engine
        """
        return await self._tick()

    # ── Snapshot / restore ────────────────────────────────────────────────────

    def snapshot(self) -> "SimulationSnapshot":  # noqa: F821
        """
        Capture the complete simulation state at the current tick.

        Components that implement the Snapshotable protocol are serialized;
        those that don't contribute None to their snapshot field (partial
        snapshot). Check snapshot.is_complete() to verify all state was
        captured.

        The returned SimulationSnapshot is a pure-Python object — pickle it,
        copy it, or pass it to restore() on another Simulation with the same
        structure.
        """
        from src.contracts.snapshot import (
            SimulationSnapshot, AgentSnapshot, Snapshotable,
        )

        agent_snaps: list[AgentSnapshot] = []
        for agent in self.agents:
            brain_bytes: bytes | None = (
                agent.brain.serialize()
                if isinstance(agent.brain, Snapshotable)
                else None
            )
            state_ext_bytes: bytes | None = (
                agent.state_ext.serialize()
                if agent.state_ext is not None and isinstance(agent.state_ext, Snapshotable)
                else None
            )
            agent_snaps.append(AgentSnapshot(
                id        = agent.id,
                name      = agent.name,
                inventory = dict(agent.inventory),
                memory    = agent.memory.serialize(),
                state_ext = state_ext_bytes,
                brain     = brain_bytes,
            ))

        social_bytes: bytes | None = (
            self.social.serialize()
            if self.social is not None and isinstance(self.social, Snapshotable)
            else None
        )
        world_bytes: bytes | None = (
            self.world.serialize()
            if isinstance(self.world, Snapshotable)
            else None
        )
        scheduler_bytes: bytes | None = (
            self.scheduler.serialize()
            if isinstance(self.scheduler, Snapshotable)
            else None
        )

        return SimulationSnapshot(
            tick      = self.world.current_tick,
            rng_state = self.rng.getstate(),
            config    = {
                "ticks":     self.config.ticks,
                "seed":      self.config.seed,
                "log_level": self.config.log_level,
            },
            agents    = agent_snaps,
            social    = social_bytes,
            world     = world_bytes,
            scheduler = scheduler_bytes,
        )

    def restore(self, snapshot: "SimulationSnapshot") -> None:  # noqa: F821
        """
        Restore the simulation to the state captured in snapshot.

        Agents are mutated in-place — all existing object references (Brain
        instances, EventBus subscribers, handler objects) remain valid after
        restore. No new objects are created.

        If a component's snapshot field is None (because it wasn't Snapshotable
        when captured), that component is left in its current state and the
        restore continues for all other components.

        Raises SnapshotError if the snapshot version is incompatible.
        """
        from src.contracts.snapshot import (
            Snapshotable, SnapshotError, SNAPSHOT_VERSION,
        )

        if snapshot.version != SNAPSHOT_VERSION:
            raise SnapshotError(
                f"Snapshot version {snapshot.version!r} is incompatible "
                f"with engine version {SNAPSHOT_VERSION!r}"
            )

        # 1. RNG — restored first so all downstream randomness is correct
        if snapshot.rng_state is not None:
            self.rng.setstate(snapshot.rng_state)

        # 2. World — restores tick counter and domain state (grid, org graph, etc.)
        if snapshot.world is not None and isinstance(self.world, Snapshotable):
            self.world.restore(snapshot.world)

        # 3. Per-agent state — mutate in place to keep object references stable
        agent_map = {a.id: a for a in self.agents}
        for snap_agent in snapshot.agents:
            agent = agent_map.get(snap_agent.id)
            if agent is None:
                continue  # agent present in snapshot but not in current sim — skip

            agent.inventory = dict(snap_agent.inventory)
            agent.memory.restore(snap_agent.memory)

            if snap_agent.state_ext is not None and agent.state_ext is not None:
                if isinstance(agent.state_ext, Snapshotable):
                    agent.state_ext.restore(snap_agent.state_ext)

            if snap_agent.brain is not None and isinstance(agent.brain, Snapshotable):
                agent.brain.restore(snap_agent.brain)

        # 4. Social system
        if (self.social is not None
                and snapshot.social is not None
                and isinstance(self.social, Snapshotable)):
            self.social.restore(snapshot.social)

        # 5. Scheduler (for future schedulers that carry runtime state)
        if (snapshot.scheduler is not None
                and isinstance(self.scheduler, Snapshotable)):
            self.scheduler.restore(snapshot.scheduler)

        # 6. Re-bind the canonical RNG and agent list to the world.
        #    world.restore() may have replaced internal data structures
        #    (e.g. a new Grid object), so we must re-inject these references.
        self.world.rng = self.rng
        if hasattr(self.world, "register_agents"):
            self.world.register_agents(self.agents)

    def save_snapshot(self, path: str | Path) -> None:
        """Snapshot and persist to disk. Convenience wrapper around snapshot() + save_to_file()."""
        from src.contracts.snapshot import save_to_file
        save_to_file(self.snapshot(), path)

    def load_snapshot(self, path: str | Path) -> None:
        """Load a snapshot from disk and restore. Convenience wrapper around load_from_file() + restore()."""
        from src.contracts.snapshot import load_from_file
        self.restore(load_from_file(path))

    # ── Internal ──────────────────────────────────────────────────────────────

    async def _tick(self) -> TickSummary:
        self.world.tick()
        tick = self.world.current_tick

        self.bus.emit(Event(
            type="tick_start", tick=tick, agent_id="engine",
            data=self.world.metadata,
        ))

        raw = await self.scheduler.run_tick(
            agents  = self.agents,
            step_fn = self._runtime.step,
        )

        agent_results: list[AgentTickResult] = []
        errors:        list[tuple[str, str]] = []

        for agent, step_result in raw:
            if isinstance(step_result, Exception):
                self.bus.emit(Event(
                    type=AGENT_STEP_ERROR, tick=tick, agent_id=agent.id,
                    data={"error": str(step_result), "agent_name": agent.name},
                ))
                errors.append((agent.id, str(step_result)))
            else:
                intent, action_result = step_result
                agent_results.append(AgentTickResult(
                    agent_id   = agent.id,
                    agent_name = agent.name,
                    intent     = intent,
                    result     = action_result,
                ))

        self.bus.emit(Event(
            type=TICK_END, tick=tick, agent_id="engine",
            data={"agent_count": len(self.agents)},
        ))

        return TickSummary(tick=tick, agent_results=agent_results, errors=errors)

    # ── Config-driven construction ────────────────────────────────────────────

    @classmethod
    def from_config(cls, path: str) -> "Simulation":
        """Load a simulation from a YAML config file."""
        from src.config.loader import load_simulation
        return load_simulation(path)
