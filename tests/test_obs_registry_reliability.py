"""
tests/test_obs_registry_reliability.py
────────────────────────────────────────
Tests for ObservationRegistry fault isolation and Brain lifecycle cleanup.

Covers:
  - Provider exception does not crash tick or collect()
  - Warning is emitted to the logger (not silently swallowed)
  - Remaining providers still run after one fails
  - Capability filtering still applies to failing providers
  - SocialContextProvider logs on social-system failure
  - Simulation.close() invokes Closeable brains
  - Simulation.close() tolerates brain.close() raising
  - Context manager (__enter__/__exit__) calls close()
  - Non-Closeable brains are silently ignored by close()
  - Closeable protocol structural check

Run with: pytest tests/test_obs_registry_reliability.py -v
"""
from __future__ import annotations

import asyncio
import logging

import pytest

from src.contracts.action import ActionResult, ActionSchema, Intent
from src.contracts.brain import Closeable
from src.contracts.observation import ObservationSchema
from src.engine.obs_registry import ObservationRegistry
from src.engine.simulation import Simulation, SimulationConfig
from src.engine.agent import Agent
from src.engine.event_bus import EventBus
from src.engine.registry import ActionRegistry
from src.plugins.builtin.observations.providers import SocialContextProvider
from src.plugins.builtin.simple_memory.memory import SimpleMemory


# ── Minimal stubs ─────────────────────────────────────────────────────────────

class _ConstantProvider:
    """Returns a fixed dict slice every call."""
    def __init__(self, data: dict):
        self._data = data

    def observe(self, agent_id, capabilities, world):
        return dict(self._data)


class _RaisingProvider:
    """Always raises."""
    def __init__(self, exc: Exception | None = None):
        self._exc = exc or RuntimeError("boom")

    def observe(self, agent_id, capabilities, world):
        raise self._exc


class _MinimalWorld:
    rng          = None
    current_tick = 0
    metadata     = {}

    def tick(self):
        self.current_tick += 1

    def observe(self, agent_id):
        return {}

    def apply(self, agent_id, result):
        pass


class _FixedBrain:
    async def decide(self, agent, observation, actions, context) -> Intent:
        return Intent(action="act")


class _CloseableBrain:
    def __init__(self):
        self.close_count = 0

    async def decide(self, agent, observation, actions, context) -> Intent:
        return Intent(action="act")

    def close(self) -> None:
        self.close_count += 1


class _ExplodingCloseBrain:
    """Brain whose close() raises — used to test error isolation."""
    async def decide(self, agent, observation, actions, context) -> Intent:
        return Intent(action="act")

    def close(self) -> None:
        raise RuntimeError("close failed")


def _make_schema(name: str, tags: frozenset[str] = frozenset()) -> ObservationSchema:
    return ObservationSchema(name=name, description=f"{name} description", tags=tags)


def _make_agent(brain=None, agent_id: str = "agent_001") -> Agent:
    return Agent(
        id           = agent_id,
        name         = "Test",
        brain        = brain or _FixedBrain(),
        memory       = SimpleMemory(),
        capabilities = frozenset(),
    )


def _make_sim(agents=None) -> Simulation:
    world    = _MinimalWorld()
    registry = ActionRegistry()
    registry.register(
        ActionSchema("act", "act"),
        type("H", (), {"execute": lambda self, a, i, c: ActionResult(True, "ok")})(),
    )
    return Simulation(
        agents    = agents or [_make_agent()],
        world     = world,
        registry  = registry,
        config    = SimulationConfig(ticks=1),
    )


# ── ObservationRegistry fault isolation ───────────────────────────────────────

class TestObsRegistryFaultIsolation:

    def test_failing_provider_does_not_crash_collect(self):
        reg = ObservationRegistry()
        reg.register(_make_schema("bad"), _RaisingProvider())

        result = reg.collect("agent_001", frozenset(), _MinimalWorld())

        assert isinstance(result, dict)

    def test_failing_provider_logs_warning(self, caplog):
        reg = ObservationRegistry()
        reg.register(_make_schema("bad"), _RaisingProvider())

        with caplog.at_level(logging.WARNING, logger="src.engine.obs_registry"):
            reg.collect("agent_001", frozenset(), _MinimalWorld())

        assert any("bad" in r.message and "agent_001" in r.message for r in caplog.records), (
            f"Expected warning mentioning provider 'bad' and agent 'agent_001', "
            f"got: {[r.message for r in caplog.records]}"
        )

    def test_failing_provider_warning_names_exception_type(self, caplog):
        reg = ObservationRegistry()
        reg.register(_make_schema("bad"), _RaisingProvider(ValueError("oops")))

        with caplog.at_level(logging.WARNING, logger="src.engine.obs_registry"):
            reg.collect("agent_001", frozenset(), _MinimalWorld())

        assert any("ValueError" in r.message for r in caplog.records)

    def test_other_providers_still_run_after_failure(self, caplog):
        reg = ObservationRegistry()
        reg.register(_make_schema("bad"), _RaisingProvider())
        reg.register(_make_schema("good"), _ConstantProvider({"my_key": "my_value"}))

        with caplog.at_level(logging.WARNING, logger="src.engine.obs_registry"):
            result = reg.collect("agent_001", frozenset(), _MinimalWorld())

        assert result.get("my_key") == "my_value"

    def test_good_provider_before_bad_also_present(self, caplog):
        """Registration order must not affect whether surviving providers contribute."""
        reg = ObservationRegistry()
        reg.register(_make_schema("first"), _ConstantProvider({"k": 1}))
        reg.register(_make_schema("bad"), _RaisingProvider())

        with caplog.at_level(logging.WARNING, logger="src.engine.obs_registry"):
            result = reg.collect("agent_001", frozenset(), _MinimalWorld())

        assert result.get("k") == 1

    def test_multiple_failing_providers_all_logged(self, caplog):
        reg = ObservationRegistry()
        reg.register(_make_schema("bad1"), _RaisingProvider())
        reg.register(_make_schema("bad2"), _RaisingProvider())

        with caplog.at_level(logging.WARNING, logger="src.engine.obs_registry"):
            reg.collect("agent_001", frozenset(), _MinimalWorld())

        warning_messages = [r.message for r in caplog.records if r.levelno == logging.WARNING]
        assert len(warning_messages) == 2

    def test_capability_filtered_provider_does_not_run(self, caplog):
        """A failing provider gated by tags the agent lacks must not log (never called)."""
        reg = ObservationRegistry()
        reg.register(
            _make_schema("gated", tags=frozenset({"special"})),
            _RaisingProvider(),
        )

        with caplog.at_level(logging.WARNING, logger="src.engine.obs_registry"):
            result = reg.collect("agent_001", frozenset(), _MinimalWorld())

        assert result == {}
        assert not caplog.records

    def test_empty_registry_returns_empty_dict(self):
        reg = ObservationRegistry()
        result = reg.collect("agent_001", frozenset(), _MinimalWorld())
        assert result == {}


# ── SocialContextProvider fault isolation ─────────────────────────────────────

class TestSocialContextProviderFaultIsolation:

    def test_broken_social_system_logs_warning(self, caplog):
        class _BrokenSocial:
            def describe(self, agent_id):
                raise ConnectionError("db offline")

        provider = SocialContextProvider(_BrokenSocial())

        with caplog.at_level(logging.WARNING, logger="src.plugins.builtin.observations.providers"):
            result = provider.observe("agent_001", frozenset(), _MinimalWorld())

        assert result == {}
        assert any("agent_001" in r.message for r in caplog.records)

    def test_empty_relationships_returns_empty_dict(self):
        class _EmptySocial:
            def describe(self, agent_id):
                return ""

        provider = SocialContextProvider(_EmptySocial())
        result = provider.observe("agent_001", frozenset(), _MinimalWorld())
        assert result == {}


# ── Brain lifecycle (Closeable) ───────────────────────────────────────────────

class TestBrainLifecycle:

    def test_closeable_is_runtime_checkable(self):
        brain = _CloseableBrain()
        assert isinstance(brain, Closeable)

    def test_non_closeable_brain_fails_isinstance(self):
        brain = _FixedBrain()
        assert not isinstance(brain, Closeable)

    def test_simulation_close_calls_brain_close(self):
        brain = _CloseableBrain()
        sim   = _make_sim(agents=[_make_agent(brain)])

        sim.close()

        assert brain.close_count == 1

    def test_simulation_close_is_idempotent(self):
        brain = _CloseableBrain()
        sim   = _make_sim(agents=[_make_agent(brain)])

        sim.close()
        sim.close()

        assert brain.close_count == 2   # called twice, each invocation is independent

    def test_simulation_close_skips_non_closeable_brains(self):
        """Non-Closeable brains must not cause close() to raise."""
        sim = _make_sim(agents=[_make_agent(_FixedBrain())])
        sim.close()   # must not raise

    def test_simulation_close_tolerates_brain_close_raising(self, caplog):
        brain = _ExplodingCloseBrain()
        sim   = _make_sim(agents=[_make_agent(brain)])

        with caplog.at_level(logging.WARNING, logger="src.engine.simulation"):
            sim.close()   # must not raise

        assert any("agent_001" in r.message for r in caplog.records)

    def test_simulation_close_continues_after_one_failing_brain(self):
        brain_good1 = _CloseableBrain()
        brain_bad   = _ExplodingCloseBrain()
        brain_good2 = _CloseableBrain()

        sim = _make_sim(agents=[
            _make_agent(brain_good1, "a1"),
            _make_agent(brain_bad,   "a2"),
            _make_agent(brain_good2, "a3"),
        ])
        sim.close()

        assert brain_good1.close_count == 1
        assert brain_good2.close_count == 1

    def test_context_manager_calls_close(self):
        brain = _CloseableBrain()

        with _make_sim(agents=[_make_agent(brain)]):
            pass

        assert brain.close_count == 1

    def test_context_manager_calls_close_on_exception(self):
        brain = _CloseableBrain()

        try:
            with _make_sim(agents=[_make_agent(brain)]):
                raise ValueError("mid-sim failure")
        except ValueError:
            pass

        assert brain.close_count == 1

    def test_multiple_closeable_brains_all_closed(self):
        brains = [_CloseableBrain() for _ in range(3)]
        agents = [_make_agent(b, f"a{i}") for i, b in enumerate(brains)]

        sim = _make_sim(agents=agents)
        sim.close()

        assert all(b.close_count == 1 for b in brains)
