"""
tests/test_external_world.py
────────────────────────────
Tests for ExternalWorld protocol + HostedWorld + TickSummary.

Run with:  pytest tests/
"""
from __future__ import annotations

import pytest

from src.contracts.action import ActionResult, ActionSchema, Intent
from src.contracts.world import ExternalWorld
from src.engine.agent import Agent
from src.engine.registry import ActionRegistry
from src.engine.simulation import Simulation, TickSummary
from src.plugins.builtin.simple_memory.memory import SimpleMemory
from src.plugins.external.world import HostedWorld


# ── Minimal stubs ─────────────────────────────────────────────────────────────

class _FixedBrain:
    """Brain that always returns the same intent."""

    def __init__(self, intent: Intent):
        self._intent = intent

    async def decide(self, agent, observation, actions, context) -> Intent:
        return self._intent


class _EchoHandler:
    """Handler that returns one engine_command per call."""

    def execute(self, agent, intent, context) -> ActionResult:
        return ActionResult(
            success=True,
            outcome_text="echo",
            engine_commands=[{"type": "echo", "agent": agent.id}],
        )


class _NullHandler:
    """Handler with no engine_commands (baseline / backward-compat cases)."""

    def execute(self, agent, intent, context) -> ActionResult:
        return ActionResult(success=True, outcome_text="nothing")


def _make_sim(
    world: HostedWorld,
    intent: Intent | None = None,
    handler=None,
) -> Simulation:
    if intent is None:
        intent = Intent(action="act")
    if handler is None:
        handler = _EchoHandler()
    registry = ActionRegistry()
    registry.register(ActionSchema("act", "Test action."), handler)
    agent = Agent(
        id     = "agent_001",
        name   = "TestAgent",
        brain  = _FixedBrain(intent),
        memory = SimpleMemory(),
    )
    return Simulation(agents=[agent], world=world, registry=registry)


# ── Protocol structural check ─────────────────────────────────────────────────

def test_hosted_world_satisfies_external_world_protocol():
    assert isinstance(HostedWorld(), ExternalWorld)


# ── ActionResult.engine_commands — backward compatibility ─────────────────────

def test_engine_commands_defaults_to_empty_list():
    result = ActionResult(success=True, outcome_text="ok")
    assert result.engine_commands == []


def test_engine_commands_can_be_populated():
    cmd = {"type": "navigate", "destination": {"x": 1.0, "z": 2.0}}
    result = ActionResult(success=True, outcome_text="ok", engine_commands=[cmd])
    assert result.engine_commands == [cmd]


def test_existing_fields_unaffected_by_new_field():
    result = ActionResult(
        success=True,
        outcome_text="fine",
        state_mutations={"health_delta": -5},
        side_effects=[{"type": "social", "from": "a", "to": "b", "delta": 0.1}],
    )
    assert result.state_mutations == {"health_delta": -5}
    assert len(result.side_effects) == 1
    assert result.engine_commands == []


# ── HostedWorld: push / observe ───────────────────────────────────────────────

def test_push_observation_returned_by_observe():
    world = HostedWorld()
    obs = {"location": "town_square", "weather": "clear"}
    world.push_observation("agent_001", obs)
    assert world.observe("agent_001") == obs


def test_observe_unknown_agent_returns_empty_dict():
    world = HostedWorld()
    assert world.observe("nonexistent") == {}


def test_observe_returns_copy_not_reference():
    world = HostedWorld()
    obs = {"location": "forest"}
    world.push_observation("a1", obs)
    world.observe("a1")["location"] = "mutated"
    assert world.observe("a1")["location"] == "forest"


def test_push_metadata_returned_by_metadata_property():
    world = HostedWorld()
    meta = {"time_of_day": "noon", "season": "summer"}
    world.push_metadata(meta)
    assert world.metadata == meta


def test_push_metadata_replaces_previous():
    world = HostedWorld()
    world.push_metadata({"a": 1})
    world.push_metadata({"b": 2})
    assert world.metadata == {"b": 2}


def test_metadata_returns_copy_not_reference():
    world = HostedWorld()
    world.push_metadata({"key": "original"})
    world.metadata["key"] = "mutated"
    assert world.metadata["key"] == "original"


# ── HostedWorld: collect_commands ─────────────────────────────────────────────

def test_collect_commands_accumulates_from_apply():
    world = HostedWorld()
    result = ActionResult(
        success=True, outcome_text="x",
        engine_commands=[{"type": "navigate", "dest": "north"}],
    )
    world.apply("agent_001", result)
    cmds = world.collect_commands()
    assert len(cmds) == 1
    assert cmds[0]["type"] == "navigate"
    assert cmds[0]["agent_id"] == "agent_001"
    assert cmds[0]["dest"] == "north"


def test_collect_commands_clears_buffer():
    world = HostedWorld()
    world.apply("a1", ActionResult(success=True, outcome_text="x",
                                   engine_commands=[{"type": "t"}]))
    world.collect_commands()
    assert world.collect_commands() == []


def test_collect_commands_empty_when_no_engine_commands():
    world = HostedWorld()
    world.apply("a1", ActionResult(success=True, outcome_text="ok"))
    assert world.collect_commands() == []


def test_collect_commands_multiple_agents():
    world = HostedWorld()
    world.apply("a1", ActionResult(success=True, outcome_text="x",
                                   engine_commands=[{"type": "walk"}]))
    world.apply("a2", ActionResult(success=True, outcome_text="y",
                                   engine_commands=[{"type": "idle"}, {"type": "look"}]))
    cmds = world.collect_commands()
    assert len(cmds) == 3
    agent_ids = {c["agent_id"] for c in cmds}
    assert agent_ids == {"a1", "a2"}


# ── HostedWorld: tick counter ─────────────────────────────────────────────────

def test_tick_advances_counter():
    world = HostedWorld()
    assert world.current_tick == 0
    world.tick()
    world.tick()
    assert world.current_tick == 2


# ── Integration: TickSummary from run_tick() ──────────────────────────────────

@pytest.mark.asyncio
async def test_run_tick_returns_tick_summary():
    world = HostedWorld()
    world.push_observation("agent_001", {"location": "start"})
    sim = _make_sim(world)
    summary = await sim.run_tick()
    assert isinstance(summary, TickSummary)
    assert summary.tick == 1
    assert len(summary.agent_results) == 1
    assert summary.agent_results[0].agent_id == "agent_001"
    assert summary.agent_results[0].agent_name == "TestAgent"
    assert summary.agent_results[0].intent.action == "act"
    assert summary.errors == []


@pytest.mark.asyncio
async def test_tick_summary_engine_commands_convenience():
    world = HostedWorld()
    world.push_observation("agent_001", {"location": "start"})
    sim = _make_sim(world)
    summary = await sim.run_tick()
    cmds = summary.engine_commands()
    assert len(cmds) == 1
    assert cmds[0]["type"] == "echo"
    assert cmds[0]["agent"] == "agent_001"


@pytest.mark.asyncio
async def test_world_collect_and_summary_commands_consistent():
    """Both HostedWorld.collect_commands() and TickSummary.engine_commands() surface command data."""
    world = HostedWorld()
    world.push_observation("agent_001", {"location": "start"})
    sim = _make_sim(world)
    summary = await sim.run_tick()

    world_cmds   = world.collect_commands()
    summary_cmds = summary.engine_commands()

    # world_cmds include agent_id prefix; summary_cmds are raw handler output
    assert len(world_cmds) == 1
    assert world_cmds[0]["agent_id"] == "agent_001"
    assert world_cmds[0]["type"] == "echo"

    assert len(summary_cmds) == 1
    assert summary_cmds[0]["type"] == "echo"


@pytest.mark.asyncio
async def test_no_engine_commands_when_handler_omits_them():
    world = HostedWorld()
    world.push_observation("agent_001", {"location": "start"})
    sim = _make_sim(world, handler=_NullHandler())
    summary = await sim.run_tick()
    assert summary.engine_commands() == []
    assert world.collect_commands() == []


@pytest.mark.asyncio
async def test_metadata_available_to_brain_via_context():
    """Metadata pushed before a tick appears in BrainContext.metadata."""
    received_meta: dict = {}

    class _MetaCaptureBrain:
        async def decide(self, agent, observation, actions, context) -> Intent:
            received_meta.update(context.metadata)
            return Intent(action="act")

    world = HostedWorld()
    world.push_observation("agent_001", {})
    world.push_metadata({"season": "winter", "tick": 1})

    registry = ActionRegistry()
    registry.register(ActionSchema("act", "test"), _NullHandler())
    agent = Agent(id="agent_001", name="A", brain=_MetaCaptureBrain(), memory=SimpleMemory())
    sim = Simulation(agents=[agent], world=world, registry=registry)
    await sim.run_tick()

    assert received_meta.get("season") == "winter"


@pytest.mark.asyncio
async def test_nearby_agents_from_pushed_observation_reach_brain():
    """
    nearby_agents pre-loaded in pushed observation pass through to the brain
    without AgentRuntime calling VisibilityWorld.get_nearby_agents().
    """
    received_obs: dict = {}

    class _ObsCaptureBrain:
        async def decide(self, agent, observation, actions, context) -> Intent:
            received_obs.update(observation)
            return Intent(action="act")

    world = HostedWorld()
    nearby = [{"id": "agent_002", "name": "Bob", "inventory": {}, "ext": {}}]
    world.push_observation("agent_001", {
        "location": "market",
        "nearby_agents": nearby,
    })

    registry = ActionRegistry()
    registry.register(ActionSchema("act", "test"), _NullHandler())
    agent = Agent(id="agent_001", name="Alice", brain=_ObsCaptureBrain(), memory=SimpleMemory())
    sim = Simulation(agents=[agent], world=world, registry=registry)
    await sim.run_tick()

    assert received_obs["nearby_agents"] == nearby
    assert received_obs["location"] == "market"


@pytest.mark.asyncio
async def test_multiple_ticks_accumulate_correctly():
    world = HostedWorld()
    sim = _make_sim(world)

    for i in range(3):
        world.push_observation("agent_001", {"tick_label": i})
        summary = await sim.run_tick()
        assert summary.tick == i + 1
        cmds = world.collect_commands()
        assert len(cmds) == 1   # one command per tick, buffer drained each time

    assert world.current_tick == 3
