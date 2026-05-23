"""
tests/test_snapshot.py
──────────────────────
Comprehensive tests for deterministic snapshot/restore.

Covers:
  - Protocol structural compliance for all Snapshotable components
  - Per-component serialize/restore round-trips (unit)
  - Full Simulation.snapshot() / restore() round-trips (integration)
  - RNG determinism after restore
  - File save/load (save_snapshot / load_snapshot)
  - Version mismatch detection
  - Partial snapshots (non-Snapshotable world)
  - Multi-tick round-trips
  - ReplayBrain cursor restoration
  - Social graph restoration
  - HostedWorld + ExternalWorld snapshot

Run with: pytest tests/test_snapshot.py -v
"""
from __future__ import annotations

import asyncio
import pickle
import random
import tempfile
from collections import defaultdict
from pathlib import Path

import pytest

from src.contracts.action import ActionResult, ActionSchema, Intent
from src.contracts.snapshot import (
    AgentSnapshot,
    Snapshotable,
    SimulationSnapshot,
    SnapshotError,
    SNAPSHOT_VERSION,
    load_from_file,
    save_to_file,
)
from src.contracts.social import SocialSystem
from src.contracts.world import ExternalWorld
from src.engine.agent import Agent
from src.engine.event_bus import EventBus, SocialEffectSubscriber
from src.engine.registry import ActionRegistry
from src.engine.simulation import Simulation, SimulationConfig, TickSummary
from src.plugins.builtin.replay_brain.brain import ReplayBrain
from src.plugins.builtin.simple_memory.memory import SimpleMemory
from src.plugins.builtin.simple_social.social import WeightedGraphSocial
from src.plugins.external.world import HostedWorld


# ── Minimal stubs ─────────────────────────────────────────────────────────────

class _FixedBrain:
    """Stateless brain — not Snapshotable."""
    def __init__(self, action: str = "act"):
        self._action = action

    async def decide(self, agent, observation, actions, context) -> Intent:
        return Intent(action=self._action)


class _CountingBrain:
    """Snapshotable brain that counts its own decide() calls."""

    def __init__(self):
        self._count = 0

    async def decide(self, agent, observation, actions, context) -> Intent:
        self._count += 1
        return Intent(action="act", reasoning=f"call#{self._count}")

    def serialize(self) -> bytes:
        return pickle.dumps({"count": self._count})

    def restore(self, data: bytes) -> None:
        self._count = pickle.loads(data)["count"]


class _NullHandler:
    def execute(self, agent, intent, context) -> ActionResult:
        return ActionResult(success=True, outcome_text="ok")


class _SocialHandler:
    """Handler that emits a social side effect so WeightedGraphSocial gets data."""
    def execute(self, agent, intent, context) -> ActionResult:
        return ActionResult(
            success=True,
            outcome_text="social",
            side_effects=[{"type": "social", "from": agent.id, "to": "agent_002", "delta": 0.5}],
        )


class _StatelessWorld:
    """World that does not implement Snapshotable — for partial-snapshot tests."""
    rng   = random.Random()
    _tick = 0

    def observe(self, agent_id):     return {}
    def apply(self, agent_id, r):    pass
    def tick(self):                  self._tick += 1
    @property
    def current_tick(self):          return self._tick
    @property
    def metadata(self):              return {"tick": self._tick}
    def get_agent(self, agent_id):   return None
    def get_world_data(self):        return {}


def _make_registry(handler=None) -> ActionRegistry:
    r = ActionRegistry()
    r.register(ActionSchema("act", "Test action."), handler or _NullHandler())
    return r


def _make_agent(agent_id: str = "agent_001", brain=None) -> Agent:
    from examples.medieval.sim.vitals import MedievalVitals
    return Agent(
        id        = agent_id,
        name      = agent_id.replace("_", " ").title(),
        brain     = brain or _FixedBrain(),
        memory    = SimpleMemory(capacity=10),
        inventory = {"food": 5, "wood": 2},
        state_ext = MedievalVitals(health=80, hunger=20, energy=60),
    )


def _make_sim(
    world=None,
    agents=None,
    registry=None,
    social=None,
    config=None,
) -> Simulation:
    if world is None:
        world = HostedWorld()
    if agents is None:
        agents = [_make_agent()]
    if registry is None:
        registry = _make_registry()
    bus = EventBus()
    if social is not None:
        bus.subscribe("action_completed", SocialEffectSubscriber(social))
    if world:
        for a in agents:
            if isinstance(world, HostedWorld):
                world.push_observation(a.id, {"location": "start"})
    return Simulation(
        agents   = agents,
        world    = world,
        registry = registry,
        bus      = bus,
        config   = config or SimulationConfig(ticks=5, seed=99),
        social   = social,
    )


# ─────────────────────────────────────────────────────────────────────────────
# 1. Protocol compliance
# ─────────────────────────────────────────────────────────────────────────────

def test_simple_memory_is_snapshotable():
    assert isinstance(SimpleMemory(), Snapshotable)


def test_medieval_vitals_is_snapshotable():
    from examples.medieval.sim.vitals import MedievalVitals
    assert isinstance(MedievalVitals(), Snapshotable)


def test_employee_vitals_is_snapshotable():
    from examples.corporate.sim.state import EmployeeVitals
    assert isinstance(EmployeeVitals(), Snapshotable)


def test_weighted_graph_social_is_snapshotable():
    assert isinstance(WeightedGraphSocial(), Snapshotable)


def test_weighted_graph_social_satisfies_social_system():
    assert isinstance(WeightedGraphSocial(), SocialSystem)


def test_replay_brain_is_snapshotable():
    import json, tempfile
    with tempfile.NamedTemporaryFile(suffix=".jsonl", mode="w", delete=False) as f:
        f.write(json.dumps({"tick":1,"agent_id":"a1","action":"act",
                            "target":None,"parameters":{},"reasoning":""}) + "\n")
        path = f.name
    brain = ReplayBrain(mode="replay", path=path)
    assert isinstance(brain, Snapshotable)


def test_hosted_world_is_snapshotable():
    assert isinstance(HostedWorld(), Snapshotable)


def test_medieval_world_is_snapshotable():
    from examples.medieval.sim.world import MedievalWorld
    assert isinstance(MedievalWorld(), Snapshotable)


def test_corporate_world_is_snapshotable():
    from examples.corporate.sim.world import CorporateWorld
    assert isinstance(CorporateWorld(), Snapshotable)


def test_stateless_world_is_not_snapshotable():
    assert not isinstance(_StatelessWorld(), Snapshotable)


def test_fixed_brain_is_not_snapshotable():
    assert not isinstance(_FixedBrain(), Snapshotable)


def test_counting_brain_is_snapshotable():
    assert isinstance(_CountingBrain(), Snapshotable)


# ─────────────────────────────────────────────────────────────────────────────
# 2. Per-component unit round-trips
# ─────────────────────────────────────────────────────────────────────────────

def test_simple_memory_round_trip():
    m = SimpleMemory(capacity=5)
    m.store(1, "forest", Intent(action="move"), "moved north")
    m.store(2, "river",  Intent(action="rest"), "rested")

    data = m.serialize()
    m2   = SimpleMemory(capacity=5)
    m2.restore(data)

    assert m2.recall(10) == m.recall(10)


def test_medieval_vitals_round_trip():
    from examples.medieval.sim.vitals import MedievalVitals
    v = MedievalVitals(health=60, hunger=45, energy=30)
    data = v.serialize()

    v2 = MedievalVitals()
    v2.restore(data)
    assert v2.health == 60
    assert v2.hunger == 45
    assert v2.energy == 30


def test_employee_vitals_round_trip():
    from examples.corporate.sim.state import EmployeeVitals
    v = EmployeeVitals(role="manager", stress=70, reputation=30)
    v.influence = 42
    data = v.serialize()

    v2 = EmployeeVitals(role="manager")
    v2.restore(data)
    assert v2.stress     == 70
    assert v2.reputation == 30
    assert v2.influence  == 42


def test_weighted_graph_social_round_trip():
    s = WeightedGraphSocial()
    s.add_agent("a1", "Alice")
    s.add_agent("a2", "Bob")
    s.update("a1", "a2", 0.6)
    s.update("a2", "a1", -0.3)

    data = s.serialize()
    s2   = WeightedGraphSocial()
    s2.restore(data)

    assert s2.relationship("a1", "a2") == pytest.approx(0.6)
    assert s2.relationship("a2", "a1") == pytest.approx(-0.3)
    assert s2.g.nodes["a1"]["name"] == "Alice"
    assert s2.g.nodes["a2"]["name"] == "Bob"


def test_hosted_world_round_trip():
    w = HostedWorld()
    w.push_metadata({"season": "winter", "tick": 3})
    w.push_observation("a1", {"location": "forest", "nearby_agents": []})
    w.tick(); w.tick(); w.tick()

    data = w.serialize()
    w2   = HostedWorld()
    w2.restore(data)

    assert w2.current_tick == 3
    assert w2.metadata["season"] == "winter"
    assert w2.observe("a1")["location"] == "forest"


def test_medieval_world_round_trip():
    from examples.medieval.sim.world import MedievalWorld
    w = MedievalWorld(width=5, height=5, seed=42)
    w.tick(); w.tick()
    w.place_agent("a1", x=2, y=2)
    cell = w.grid.get(2, 2)
    cell.local_food = 7  # consume some food

    data = w.serialize()
    w2   = MedievalWorld(width=5, height=5, seed=42)
    w2.restore(data)

    assert w2.current_tick == 2
    assert w2.grid.get(2, 2).local_food == 7
    assert "a1" in w2.grid.cell_for("a1").occupants


def test_corporate_world_round_trip():
    from examples.corporate.sim.world import CorporateWorld
    w = CorporateWorld()
    w.place_agent("e1", department="Eng", role="manager")
    w.place_agent("e2", department="Eng", role="employee", manager="e1")
    w.tick(); w.tick()

    data = w.serialize()
    w2   = CorporateWorld()
    w2.restore(data)

    assert w2.current_tick == 2
    assert w2.departments["e1"] == "Eng"
    assert w2.roles["e2"] == "employee"
    assert w2.graph.has_edge("e1", "e2")


def test_replay_brain_cursor_round_trip():
    import json, tempfile
    records = [
        {"tick": 1, "agent_id": "a1", "action": "act",
         "target": None, "parameters": {}, "reasoning": "r1"},
        {"tick": 2, "agent_id": "a1", "action": "act",
         "target": None, "parameters": {}, "reasoning": "r2"},
        {"tick": 3, "agent_id": "a1", "action": "act",
         "target": None, "parameters": {}, "reasoning": "r3"},
    ]
    with tempfile.NamedTemporaryFile(suffix=".jsonl", mode="w", delete=False) as f:
        for r in records:
            f.write(json.dumps(r) + "\n")
        path = f.name

    brain = ReplayBrain(mode="replay", path=path)
    # Advance cursor by consuming the first intent
    brain._next("a1", 1)
    assert brain._cursors["a1"] == 1

    data  = brain.serialize()
    brain2 = ReplayBrain(mode="replay", path=path)
    brain2.restore(data)

    assert brain2._cursors["a1"] == 1
    # Next intent from cursor 1 should be record[1]
    intent = brain2._next("a1", 2)
    assert intent.reasoning == "r2"


def test_counting_brain_round_trip():
    b = _CountingBrain()
    b._count = 7
    data = b.serialize()
    b2   = _CountingBrain()
    b2.restore(data)
    assert b2._count == 7


# ─────────────────────────────────────────────────────────────────────────────
# 3. SimulationSnapshot structure
# ─────────────────────────────────────────────────────────────────────────────

def test_snapshot_has_correct_version():
    sim  = _make_sim()
    snap = sim.snapshot()
    assert snap.version == SNAPSHOT_VERSION


def test_snapshot_tick_matches_world():
    world = HostedWorld()
    sim   = _make_sim(world=world)
    world.push_observation("agent_001", {})
    asyncio.run(sim.run_tick())
    asyncio.run(sim.run_tick())
    snap = sim.snapshot()
    assert snap.tick == 2


def test_snapshot_rng_state_is_captured():
    sim  = _make_sim()
    snap = sim.snapshot()
    assert snap.rng_state is not None
    # Verify it's a valid getstate() tuple by restoring it
    rng = random.Random()
    rng.setstate(snap.rng_state)


def test_snapshot_captures_agent_inventory():
    agent = _make_agent()
    agent.inventory["gold"] = 42
    sim   = _make_sim(agents=[agent])
    snap  = sim.snapshot()
    assert snap.agents[0].inventory["gold"] == 42


def test_snapshot_agent_memory_is_bytes():
    agent = _make_agent()
    agent.memory.store(1, "loc", Intent(action="act"), "ok")
    sim  = _make_sim(agents=[agent])
    snap = sim.snapshot()
    assert isinstance(snap.agents[0].memory, bytes)
    assert len(snap.agents[0].memory) > 0


def test_snapshot_agent_state_ext_is_bytes():
    sim  = _make_sim()
    snap = sim.snapshot()
    assert isinstance(snap.agents[0].state_ext, bytes)


def test_snapshot_brain_none_for_non_snapshotable():
    sim  = _make_sim()  # uses _FixedBrain which is not Snapshotable
    snap = sim.snapshot()
    assert snap.agents[0].brain is None


def test_snapshot_brain_bytes_for_snapshotable():
    agent = _make_agent(brain=_CountingBrain())
    sim   = _make_sim(agents=[agent])
    snap  = sim.snapshot()
    assert isinstance(snap.agents[0].brain, bytes)


def test_snapshot_world_bytes_for_snapshotable_world():
    sim  = _make_sim(world=HostedWorld())
    snap = sim.snapshot()
    assert isinstance(snap.world, bytes)


def test_snapshot_world_none_for_non_snapshotable():
    sim  = _make_sim(world=_StatelessWorld())
    snap = sim.snapshot()
    assert snap.world is None


def test_snapshot_social_bytes_when_present():
    social = WeightedGraphSocial()
    social.add_agent("agent_001", "A001")
    sim  = _make_sim(social=social)
    snap = sim.snapshot()
    assert isinstance(snap.social, bytes)


def test_snapshot_social_none_when_absent():
    sim  = _make_sim(social=None)
    snap = sim.snapshot()
    assert snap.social is None


def test_is_complete_true_when_world_and_social_present():
    social = WeightedGraphSocial()
    social.add_agent("agent_001", "A001")
    sim  = _make_sim(world=HostedWorld(), social=social)
    snap = sim.snapshot()
    assert snap.is_complete()


def test_is_complete_false_without_world():
    sim  = _make_sim(world=_StatelessWorld())
    snap = sim.snapshot()
    assert not snap.is_complete()


def test_missing_components_lists_world_when_absent():
    sim  = _make_sim(world=_StatelessWorld())
    snap = sim.snapshot()
    assert "world" in snap.missing_components()


def test_snapshot_is_picklable():
    sim  = _make_sim()
    snap = sim.snapshot()
    data = pickle.dumps(snap)
    snap2 = pickle.loads(data)
    assert snap2.tick == snap.tick
    assert snap2.version == snap.version


# ─────────────────────────────────────────────────────────────────────────────
# 4. Simulation.restore() — integration round-trips
# ─────────────────────────────────────────────────────────────────────────────

@pytest.mark.asyncio
async def test_restore_inventory():
    agent = _make_agent()
    world = HostedWorld()
    world.push_observation(agent.id, {})
    sim   = _make_sim(world=world, agents=[agent])

    agent.inventory["gold"] = 99
    snap = sim.snapshot()
    agent.inventory["gold"] = 0   # mutate after snapshot

    sim.restore(snap)
    assert agent.inventory["gold"] == 99


@pytest.mark.asyncio
async def test_restore_memory():
    agent = _make_agent()
    agent.memory.store(1, "loc", Intent(action="rest"), "rested well")
    world = HostedWorld()
    world.push_observation(agent.id, {})
    sim   = _make_sim(world=world, agents=[agent])

    snap = sim.snapshot()
    memory_before = agent.memory.recall(10)

    # Run another tick to dirty the memory
    world.push_observation(agent.id, {})
    await sim.run_tick()

    sim.restore(snap)
    assert agent.memory.recall(10) == memory_before


@pytest.mark.asyncio
async def test_restore_state_ext():
    from examples.medieval.sim.vitals import MedievalVitals
    agent      = _make_agent()
    agent.state_ext = MedievalVitals(health=75, hunger=30, energy=50)
    world = HostedWorld()
    world.push_observation(agent.id, {})
    sim = _make_sim(world=world, agents=[agent])

    snap = sim.snapshot()
    agent.state_ext.health = 1    # damage after snapshot

    sim.restore(snap)
    assert agent.state_ext.health == 75
    assert agent.state_ext.hunger == 30


@pytest.mark.asyncio
async def test_restore_snapshotable_brain():
    brain = _CountingBrain()
    agent = _make_agent(brain=brain)
    world = HostedWorld()
    world.push_observation(agent.id, {})
    sim = _make_sim(world=world, agents=[agent])

    brain._count = 5
    snap = sim.snapshot()
    brain._count = 999   # corrupt after snapshot

    sim.restore(snap)
    assert brain._count == 5


@pytest.mark.asyncio
async def test_restore_world_tick():
    world = HostedWorld()
    world.push_observation("agent_001", {})
    sim   = _make_sim(world=world)
    await sim.run_tick()
    await sim.run_tick()
    assert world.current_tick == 2

    snap = sim.snapshot()
    world.push_observation("agent_001", {})
    await sim.run_tick()
    assert world.current_tick == 3

    sim.restore(snap)
    assert world.current_tick == 2


@pytest.mark.asyncio
async def test_restore_hosted_world_observations():
    world = HostedWorld()
    sim   = _make_sim(world=world)
    # Push "castle" after _make_sim so it's the observation captured by snapshot
    world.push_observation("agent_001", {"location": "castle"})
    snap  = sim.snapshot()

    world.push_observation("agent_001", {"location": "dungeon"})
    sim.restore(snap)
    assert world.observe("agent_001")["location"] == "castle"


@pytest.mark.asyncio
async def test_restore_social_graph():
    social = WeightedGraphSocial()
    social.add_agent("agent_001", "Alice")
    social.add_agent("agent_002", "Bob")
    social.update("agent_001", "agent_002", 0.7)

    world = HostedWorld()
    world.push_observation("agent_001", {})
    sim   = _make_sim(world=world, social=social)
    snap  = sim.snapshot()

    # Corrupt social after snapshot
    social.update("agent_001", "agent_002", -1.7)  # drives to -1.0
    assert social.relationship("agent_001", "agent_002") < 0

    sim.restore(snap)
    assert social.relationship("agent_001", "agent_002") == pytest.approx(0.7)


@pytest.mark.asyncio
async def test_restore_medieval_world_grid_positions():
    from examples.medieval.sim.world import MedievalWorld
    from examples.medieval.sim.registry import build_medieval_registry

    world    = MedievalWorld(width=5, height=5, seed=42)
    registry = build_medieval_registry()
    agent    = Agent(
        id="agent_001", name="Hero",
        brain=_FixedBrain(action="idle"), memory=SimpleMemory(),
        inventory={},
    )
    world.place_agent("agent_001", x=2, y=2)
    world.register_agents([agent])
    sim = Simulation(agents=[agent], world=world, registry=registry,
                     config=SimulationConfig(seed=1))

    # Move the agent
    world.grid.move_agent("agent_001", "north")
    cell_after_move = world.grid.cell_for("agent_001")
    assert cell_after_move.y == 1

    snap = sim.snapshot()

    # Move again
    world.grid.move_agent("agent_001", "north")

    sim.restore(snap)
    cell_restored = world.grid.cell_for("agent_001")
    assert cell_restored.y == 1, "Agent position should match snapshot"


# ─────────────────────────────────────────────────────────────────────────────
# 5. RNG determinism
# ─────────────────────────────────────────────────────────────────────────────

def test_rng_state_restored_produces_same_sequence():
    sim  = _make_sim()
    snap = sim.snapshot()

    # Generate a reference sequence from the canonical RNG
    reference = [sim.rng.random() for _ in range(5)]

    # Restore and generate the same sequence again
    sim.restore(snap)
    replayed  = [sim.rng.random() for _ in range(5)]

    assert reference == replayed


@pytest.mark.asyncio
async def test_rng_determinism_after_tick_restore():
    """
    Run to tick 2, snapshot, run to tick 4, restore back to 2,
    run to tick 4 again — the RNG sequence from tick 2→4 must be identical.
    """
    world = HostedWorld()

    def push_all():
        world.push_observation("agent_001", {})

    sim = _make_sim(world=world, config=SimulationConfig(seed=77))

    push_all(); await sim.run_tick()
    push_all(); await sim.run_tick()
    snap = sim.snapshot()

    # Collect RNG output for ticks 3 and 4 (first run)
    r1_values = []
    push_all(); await sim.run_tick()
    r1_values.append(sim.rng.random())
    push_all(); await sim.run_tick()
    r1_values.append(sim.rng.random())

    # Restore and repeat
    sim.restore(snap)
    push_all()
    r2_values = []
    push_all(); await sim.run_tick()
    r2_values.append(sim.rng.random())
    push_all(); await sim.run_tick()
    r2_values.append(sim.rng.random())

    assert r1_values == r2_values


# ─────────────────────────────────────────────────────────────────────────────
# 6. File persistence
# ─────────────────────────────────────────────────────────────────────────────

def test_save_and_load_from_file():
    agent = _make_agent()
    agent.inventory["gems"] = 17
    sim   = _make_sim(agents=[agent])
    snap  = sim.snapshot()

    with tempfile.TemporaryDirectory() as tmpdir:
        path = Path(tmpdir) / "snap.pkl"
        save_to_file(snap, path)
        loaded = load_from_file(path)

    assert loaded.agents[0].inventory["gems"] == 17
    assert loaded.tick == snap.tick


def test_save_and_load_via_simulation_methods():
    agent = _make_agent()
    agent.inventory["potions"] = 3
    world = HostedWorld()
    world.push_observation(agent.id, {})
    sim   = _make_sim(world=world, agents=[agent])

    with tempfile.TemporaryDirectory() as tmpdir:
        path = Path(tmpdir) / "checkpoint.pkl"
        sim.save_snapshot(path)

        agent.inventory["potions"] = 0
        sim.load_snapshot(path)

    assert agent.inventory["potions"] == 3


def test_load_from_nonexistent_file_raises():
    with pytest.raises(SnapshotError, match="not found"):
        load_from_file("/nonexistent/path/snap.pkl")


def test_load_wrong_type_raises():
    with tempfile.TemporaryDirectory() as tmpdir:
        path = Path(tmpdir) / "bad.pkl"
        path.write_bytes(pickle.dumps({"not": "a snapshot"}))
        with pytest.raises(SnapshotError, match="SimulationSnapshot"):
            load_from_file(path)


def test_version_mismatch_raises_on_load():
    snap = SimulationSnapshot(version="999", tick=0)
    with tempfile.TemporaryDirectory() as tmpdir:
        path = Path(tmpdir) / "old.pkl"
        path.write_bytes(pickle.dumps(snap))
        with pytest.raises(SnapshotError, match="version mismatch"):
            load_from_file(path)


def test_version_mismatch_raises_on_restore():
    sim  = _make_sim()
    snap = sim.snapshot()
    snap.version = "999"
    with pytest.raises(SnapshotError, match="incompatible"):
        sim.restore(snap)


# ─────────────────────────────────────────────────────────────────────────────
# 7. Partial snapshots (non-Snapshotable world)
# ─────────────────────────────────────────────────────────────────────────────

def test_partial_snapshot_world_is_none():
    sim  = _make_sim(world=_StatelessWorld())
    snap = sim.snapshot()
    assert snap.world is None
    assert not snap.is_complete()


@pytest.mark.asyncio
async def test_partial_snapshot_restore_still_restores_agents():
    """Even with a non-Snapshotable world, agent state is still restored."""
    agent = _make_agent()
    sim   = _make_sim(world=_StatelessWorld(), agents=[agent])

    agent.inventory["food"] = 10
    snap = sim.snapshot()
    agent.inventory["food"] = 0

    sim.restore(snap)
    assert agent.inventory["food"] == 10


# ─────────────────────────────────────────────────────────────────────────────
# 8. Multi-tick round-trips
# ─────────────────────────────────────────────────────────────────────────────

@pytest.mark.asyncio
async def test_multi_tick_restore_then_continue():
    """Snapshot at tick 3, run to tick 5, restore, run again — tick restarts from 3."""
    world = HostedWorld()
    sim   = _make_sim(world=world, config=SimulationConfig(ticks=10, seed=42))

    def push():
        world.push_observation("agent_001", {})

    push(); await sim.run_tick()
    push(); await sim.run_tick()
    push(); await sim.run_tick()
    assert world.current_tick == 3

    snap = sim.snapshot()

    push(); await sim.run_tick()
    push(); await sim.run_tick()
    assert world.current_tick == 5

    sim.restore(snap)
    assert world.current_tick == 3

    push(); await sim.run_tick()
    assert world.current_tick == 4


@pytest.mark.asyncio
async def test_agent_object_identity_preserved_after_restore():
    """
    Restore must mutate existing Agent objects, not replace them.
    Verifies that the object reference in sim.agents is unchanged.
    """
    agent = _make_agent()
    world = HostedWorld()
    world.push_observation(agent.id, {})
    sim = _make_sim(world=world, agents=[agent])

    original_id = id(agent)
    snap = sim.snapshot()
    sim.restore(snap)

    assert id(sim.agents[0]) == original_id


@pytest.mark.asyncio
async def test_tick_summary_still_valid_after_restore():
    """run_tick() after restore returns correct tick number."""
    world = HostedWorld()
    world.push_observation("agent_001", {})
    sim   = _make_sim(world=world)
    await sim.run_tick()

    snap = sim.snapshot()
    world.push_observation("agent_001", {})
    await sim.run_tick()
    assert world.current_tick == 2

    sim.restore(snap)
    world.push_observation("agent_001", {})
    summary = await sim.run_tick()
    assert summary.tick == 2  # re-runs tick 2 after restoring to tick 1


# ─────────────────────────────────────────────────────────────────────────────
# 9. ReplayBrain end-to-end snapshot
# ─────────────────────────────────────────────────────────────────────────────

@pytest.mark.asyncio
async def test_replay_brain_snapshot_restores_cursor():
    import json, tempfile
    records = [
        {"tick": i+1, "agent_id": "agent_001", "action": "act",
         "target": None, "parameters": {}, "reasoning": f"r{i}"}
        for i in range(5)
    ]
    with tempfile.NamedTemporaryFile(suffix=".jsonl", mode="w", delete=False) as f:
        for r in records:
            f.write(json.dumps(r) + "\n")
        path = f.name

    brain = ReplayBrain(mode="replay", path=path)
    agent = _make_agent(brain=brain)
    world = HostedWorld()
    registry = _make_registry()
    sim = Simulation(agents=[agent], world=world, registry=registry,
                     config=SimulationConfig(seed=1))

    world.push_observation(agent.id, {})
    await sim.run_tick()
    world.push_observation(agent.id, {})
    await sim.run_tick()
    # Cursor should be at 2 (consumed records[0] and records[1])
    assert brain._cursors["agent_001"] == 2

    snap = sim.snapshot()

    # Consume two more records
    world.push_observation(agent.id, {})
    await sim.run_tick()
    world.push_observation(agent.id, {})
    await sim.run_tick()
    assert brain._cursors["agent_001"] == 4

    sim.restore(snap)
    # Cursor should be back at 2
    assert brain._cursors["agent_001"] == 2
    # Next replay should produce records[2]
    world.push_observation(agent.id, {})
    summary = await sim.run_tick()
    assert summary.agent_results[0].intent.reasoning == "r2"


# ─────────────────────────────────────────────────────────────────────────────
# 10. Missing agent in snapshot (graceful skip)
# ─────────────────────────────────────────────────────────────────────────────

@pytest.mark.asyncio
async def test_restore_skips_unknown_agent_id():
    """
    If a snapshot contains an agent_id not present in the current sim,
    restore() should skip it without raising.
    """
    agent = _make_agent("agent_001")
    world = HostedWorld()
    world.push_observation("agent_001", {})
    sim   = _make_sim(world=world, agents=[agent])

    snap = sim.snapshot()
    # Inject a phantom agent into the snapshot
    snap.agents.append(AgentSnapshot(
        id="phantom_999", name="Ghost",
        inventory={"gold": 100},
        memory=agent.memory.serialize(),
        state_ext=None, brain=None,
    ))

    # Should not raise
    sim.restore(snap)
    assert agent.inventory.get("gold", 0) == 0  # real agent unaffected
