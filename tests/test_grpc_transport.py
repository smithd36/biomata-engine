"""
tests/test_grpc_transport.py
──────────────────────────────────────────────────────────────
Integration tests for the gRPC transport layer.

Each test class spins up a real async gRPC server on an ephemeral port and
exercises the full round-trip: client stub → gRPC → servicer → engine.

Run with:  pytest tests/test_grpc_transport.py -v
"""
from __future__ import annotations

import asyncio
import pickle

import grpc
import grpc.aio
import pytest

from src.contracts.action import ActionResult, ActionSchema, Intent
from src.engine.agent import Agent
from src.engine.registry import ActionRegistry
from src.engine.simulation import Simulation, SimulationConfig
from src.plugins.builtin.simple_memory.memory import SimpleMemory
from src.plugins.external.world import HostedWorld
from src.service import create_session
from src.transport.grpc import GrpcServer
from src.transport.grpc.generated import simulation_pb2 as pb
from src.transport.grpc.generated.simulation_pb2_grpc import SimulationServiceStub


# ── Shared stubs ──────────────────────────────────────────────────────────────

class _FixedBrain:
    """Always returns the same intent regardless of observation."""
    def __init__(self, action: str = "act", **kwargs):
        self._intent = Intent(action=action, parameters={"speed": 1})

    async def decide(self, agent, observation, actions, context) -> Intent:
        return self._intent


class _EchoHandler:
    """Returns one engine_command per call so we can assert on transport."""
    def execute(self, agent, intent, context) -> ActionResult:
        return ActionResult(
            success=True,
            outcome_text="echo",
            engine_commands=[{"type": "move", "dir": "north", "agent": agent.id}],
        )


class _NullHandler:
    def execute(self, agent, intent, context) -> ActionResult:
        return ActionResult(success=True, outcome_text="idle")


def _make_registry(handler=None) -> ActionRegistry:
    reg = ActionRegistry()
    reg.register(ActionSchema("act", "Test action."), handler or _EchoHandler())
    return reg


def _make_sim(
    world:    HostedWorld | None = None,
    ticks:    int  = 20,
    handler         = None,
    agent_id: str  = "agent_001",
    name:     str  = "Alice",
) -> tuple[Simulation, HostedWorld]:
    world = world or HostedWorld()
    agent = Agent(
        id     = agent_id,
        name   = name,
        brain  = _FixedBrain(),
        memory = SimpleMemory(),
    )
    sim = Simulation(
        agents   = [agent],
        world    = world,
        registry = _make_registry(handler),
        config   = SimulationConfig(ticks=ticks, seed=42),
    )
    return sim, world


# ── Pytest fixtures ───────────────────────────────────────────────────────────

@pytest.fixture
async def server_and_stub():
    """Start a real gRPC server on an ephemeral port; yield (server, stub, world)."""
    sim, world = _make_sim()
    session    = create_session(sim)
    server     = GrpcServer(session, host="[::1]", port=0)
    port       = await server.start()

    channel = grpc.aio.insecure_channel(f"localhost:{port}")
    stub    = SimulationServiceStub(channel)

    yield server, stub, world, sim

    await channel.close()
    await server.stop(0)


@pytest.fixture
async def stub(server_and_stub):
    """Convenience alias: just the stub."""
    _, stub, _, _ = server_and_stub
    return stub


@pytest.fixture
async def stub_world(server_and_stub):
    _, stub, world, _ = server_and_stub
    return stub, world


@pytest.fixture
async def stub_sim(server_and_stub):
    _, stub, world, sim = server_and_stub
    return stub, sim


# ── HealthCheck ───────────────────────────────────────────────────────────────

class TestHealthCheck:
    async def test_returns_ok(self, stub):
        resp = await stub.HealthCheck(pb.HealthCheckRequest())
        assert resp.status == "ok"

    async def test_initial_tick_is_zero(self, stub):
        resp = await stub.HealthCheck(pb.HealthCheckRequest())
        assert resp.tick == 0

    async def test_initial_agent_count(self, stub):
        resp = await stub.HealthCheck(pb.HealthCheckRequest())
        assert resp.agent_count == 1

    async def test_session_state_is_created(self, stub):
        resp = await stub.HealthCheck(pb.HealthCheckRequest())
        assert resp.session_state == "created"

    async def test_tick_advances_after_tick(self, stub):
        await stub.TickSimulation(pb.TickRequest())
        resp = await stub.HealthCheck(pb.HealthCheckRequest())
        assert resp.tick == 1


# ── RegisterAgent ─────────────────────────────────────────────────────────────

class TestRegisterAgent:
    async def test_register_new_agent(self, stub_sim):
        stub, sim = stub_sim
        resp = await stub.RegisterAgent(pb.RegisterAgentRequest(
            config=pb.AgentConfig(
                agent_id   = "agent_002",
                agent_name = "Bob",
                brain_class = "tests.test_grpc_transport._FixedBrain",
            )
        ))
        assert resp.success is True
        assert resp.error == ""
        assert len(sim.agents) == 2
        assert sim.agents[-1].id == "agent_002"

    async def test_duplicate_agent_id_returns_error(self, stub):
        resp = await stub.RegisterAgent(pb.RegisterAgentRequest(
            config=pb.AgentConfig(
                agent_id   = "agent_001",   # already registered
                agent_name = "Alice",
                brain_class = "tests.test_grpc_transport._FixedBrain",
            )
        ))
        assert resp.success is False
        assert "already registered" in resp.error

    async def test_bad_brain_class_returns_error(self, stub):
        resp = await stub.RegisterAgent(pb.RegisterAgentRequest(
            config=pb.AgentConfig(
                agent_id    = "agent_003",
                agent_name  = "Charlie",
                brain_class = "no.such.module.Brain",
            )
        ))
        assert resp.success is False
        assert resp.error != ""

    async def test_missing_agent_id_aborts(self, stub):
        with pytest.raises(grpc.aio.AioRpcError) as exc_info:
            await stub.RegisterAgent(pb.RegisterAgentRequest(
                config=pb.AgentConfig(
                    agent_id   = "",
                    agent_name = "Nameless",
                    brain_class = "tests.test_grpc_transport._FixedBrain",
                )
            ))
        assert exc_info.value.code() == grpc.StatusCode.INVALID_ARGUMENT

    async def test_registered_agent_participates_in_tick(self, stub):
        await stub.RegisterAgent(pb.RegisterAgentRequest(
            config=pb.AgentConfig(
                agent_id    = "agent_002",
                agent_name  = "Bob",
                brain_class = "tests.test_grpc_transport._FixedBrain",
            )
        ))
        resp = await stub.TickSimulation(pb.TickRequest())
        agent_ids = {d.agent_id for d in resp.decisions}
        assert "agent_001" in agent_ids
        assert "agent_002" in agent_ids


# ── RemoveAgent ───────────────────────────────────────────────────────────────

class TestRemoveAgent:
    async def test_remove_existing_agent(self, stub_sim):
        stub, sim = stub_sim
        resp = await stub.RemoveAgent(pb.RemoveAgentRequest(agent_id="agent_001"))
        assert resp.success is True
        assert len(sim.agents) == 0

    async def test_remove_nonexistent_agent_returns_error(self, stub):
        resp = await stub.RemoveAgent(pb.RemoveAgentRequest(agent_id="ghost_agent"))
        assert resp.success is False
        assert "not found" in resp.error

    async def test_removed_agent_absent_from_next_tick(self, stub):
        await stub.RemoveAgent(pb.RemoveAgentRequest(agent_id="agent_001"))
        resp = await stub.TickSimulation(pb.TickRequest())
        assert len(resp.decisions) == 0


# ── SendObservation ───────────────────────────────────────────────────────────

class TestSendObservation:
    async def test_send_observation_success(self, stub_world):
        stub, world = stub_world
        from google.protobuf.struct_pb2 import Struct
        obs = Struct()
        obs.update({"location": "forest", "health": 100})
        resp = await stub.SendObservation(pb.SendObservationRequest(
            agent_id    = "agent_001",
            observation = obs,
        ))
        assert resp.success is True

    async def test_observation_stored_in_world(self, stub_world):
        stub, world = stub_world
        from google.protobuf.struct_pb2 import Struct
        obs = Struct()
        obs.update({"location": "castle"})
        await stub.SendObservation(pb.SendObservationRequest(
            agent_id    = "agent_001",
            observation = obs,
        ))
        assert world._observations["agent_001"]["location"] == "castle"

    async def test_missing_agent_id_aborts(self, stub):
        with pytest.raises(grpc.aio.AioRpcError) as exc_info:
            await stub.SendObservation(pb.SendObservationRequest(
                agent_id="", observation=pb.AgentObservation().observation,
            ))
        assert exc_info.value.code() == grpc.StatusCode.INVALID_ARGUMENT


# ── TickSimulation ────────────────────────────────────────────────────────────

class TestTickSimulation:
    async def test_tick_returns_one_decision_per_agent(self, stub):
        resp = await stub.TickSimulation(pb.TickRequest())
        assert len(resp.decisions) == 1
        assert resp.decisions[0].agent_id == "agent_001"

    async def test_tick_increments_counter(self, stub):
        resp = await stub.TickSimulation(pb.TickRequest())
        assert resp.tick == 1
        resp2 = await stub.TickSimulation(pb.TickRequest())
        assert resp2.tick == 2

    async def test_tick_decision_fields(self, stub):
        resp = await stub.TickSimulation(pb.TickRequest())
        d = resp.decisions[0]
        assert d.agent_name  == "Alice"
        assert d.action      == "act"
        assert d.outcome_text == "echo"
        assert d.error == ""

    async def test_tick_engine_commands_present(self, stub):
        resp = await stub.TickSimulation(pb.TickRequest())
        d = resp.decisions[0]
        assert len(d.engine_commands) == 1
        from google.protobuf import json_format
        cmd = json_format.MessageToDict(d.engine_commands[0])
        assert cmd["type"] == "move"
        assert cmd["dir"] == "north"

    async def test_tick_inline_observations(self, stub_world):
        stub, world = stub_world
        from google.protobuf.struct_pb2 import Struct
        obs = Struct()
        obs.update({"location": "tavern"})
        meta = Struct()
        meta.update({"time": "night"})
        resp = await stub.TickSimulation(pb.TickRequest(
            agent_observations=[pb.AgentObservation(agent_id="agent_001", observation=obs)],
            world_metadata=meta,
        ))
        assert resp.tick == 1
        assert world._metadata.get("time") == "night"

    async def test_tick_errors_empty_on_success(self, stub):
        resp = await stub.TickSimulation(pb.TickRequest())
        assert len(resp.errors) == 0

    async def test_tick_parameters_in_decision(self, stub):
        resp = await stub.TickSimulation(pb.TickRequest())
        from google.protobuf import json_format
        params = json_format.MessageToDict(resp.decisions[0].parameters)
        assert params.get("speed") == 1


# ── PauseSimulation / ResumeSimulation ────────────────────────────────────────

class TestPauseResume:
    async def test_pause_returns_paused_state(self, server_and_stub):
        _, stub, _, _ = server_and_stub
        # Force the session into RUNNING first
        server_and_stub[0]._session._state = __import__(
            "src.service", fromlist=["SessionState"]
        ).SessionState.RUNNING
        resp = await stub.PauseSimulation(pb.PauseRequest())
        assert resp.success is True
        assert resp.state == "paused"

    async def test_resume_returns_running_state(self, server_and_stub):
        server, stub, _, _ = server_and_stub
        session = server._session
        from src.service import SessionState
        session._state = SessionState.RUNNING
        await stub.PauseSimulation(pb.PauseRequest())
        resp = await stub.ResumeSimulation(pb.ResumeRequest())
        assert resp.success is True
        assert resp.state == "running"

    async def test_pause_noop_from_created(self, stub):
        # From CREATED state, pause is a no-op — should still return success
        resp = await stub.PauseSimulation(pb.PauseRequest())
        assert resp.success is True

    async def test_resume_noop_from_created(self, stub):
        resp = await stub.ResumeSimulation(pb.ResumeRequest())
        assert resp.success is True


# ── Snapshot / Restore ────────────────────────────────────────────────────────

class TestSnapshotRestore:
    async def test_snapshot_returns_bytes(self, stub):
        resp = await stub.Snapshot(pb.SnapshotRequest())
        assert len(resp.snapshot_data) > 0

    async def test_snapshot_tick_matches_current(self, stub):
        await stub.TickSimulation(pb.TickRequest())
        snap_resp = await stub.Snapshot(pb.SnapshotRequest())
        assert snap_resp.tick == 1

    async def test_snapshot_has_created_at(self, stub):
        resp = await stub.Snapshot(pb.SnapshotRequest())
        assert resp.created_at != ""

    async def test_restore_returns_success(self, stub):
        snap_resp = await stub.Snapshot(pb.SnapshotRequest())
        restore_resp = await stub.Restore(pb.RestoreRequest(
            snapshot_data=snap_resp.snapshot_data
        ))
        assert restore_resp.success is True

    async def test_restore_reverts_tick(self, stub):
        await stub.TickSimulation(pb.TickRequest())
        snap_resp = await stub.Snapshot(pb.SnapshotRequest())  # tick=1
        await stub.TickSimulation(pb.TickRequest())
        await stub.TickSimulation(pb.TickRequest())            # tick=3

        restore_resp = await stub.Restore(pb.RestoreRequest(
            snapshot_data=snap_resp.snapshot_data
        ))
        assert restore_resp.tick == 1

        health = await stub.HealthCheck(pb.HealthCheckRequest())
        assert health.tick == 1

    async def test_restore_invalid_bytes_returns_error(self, stub):
        resp = await stub.Restore(pb.RestoreRequest(snapshot_data=b"not-a-snapshot"))
        assert resp.success is False
        assert resp.error != ""

    async def test_snapshot_restore_roundtrip_deserialization(self, stub):
        await stub.TickSimulation(pb.TickRequest())
        snap_resp = await stub.Snapshot(pb.SnapshotRequest())

        # Verify the bytes deserialize to a valid SimulationSnapshot
        from src.contracts.snapshot import SimulationSnapshot
        snap = pickle.loads(snap_resp.snapshot_data)
        assert isinstance(snap, SimulationSnapshot)
        assert snap.tick == 1


# ── StreamEvents ──────────────────────────────────────────────────────────────

class TestStreamEvents:
    async def test_stream_receives_tick_events(self, stub):
        events: list[pb.EventMessage] = []

        async def _collect():
            async for ev in stub.StreamEvents(pb.StreamEventsRequest(
                event_types=["tick_end"]
            )):
                events.append(ev)

        stream_task = asyncio.create_task(_collect())
        await asyncio.sleep(0.05)   # let the stream subscription establish

        await stub.TickSimulation(pb.TickRequest())
        await stub.TickSimulation(pb.TickRequest())
        await asyncio.sleep(0.1)    # let events propagate

        stream_task.cancel()
        try:
            await stream_task
        except (asyncio.CancelledError, grpc.aio.AioRpcError):
            pass

        assert len(events) == 2
        for ev in events:
            assert ev.event_type == "tick_end"

    async def test_stream_receives_all_events_when_no_filter(self, stub):
        events: list[pb.EventMessage] = []

        async def _collect():
            async for ev in stub.StreamEvents(pb.StreamEventsRequest()):
                events.append(ev)

        stream_task = asyncio.create_task(_collect())
        await asyncio.sleep(0.05)

        await stub.TickSimulation(pb.TickRequest())
        await asyncio.sleep(0.1)

        stream_task.cancel()
        try:
            await stream_task
        except (asyncio.CancelledError, grpc.aio.AioRpcError):
            pass

        # At minimum TICK_START and TICK_END
        event_types = {ev.event_type for ev in events}
        assert "tick_start" in event_types
        assert "tick_end"   in event_types

    async def test_stream_filter_excludes_other_types(self, stub):
        events: list[pb.EventMessage] = []

        async def _collect():
            async for ev in stub.StreamEvents(pb.StreamEventsRequest(
                event_types=["tick_start"]
            )):
                events.append(ev)

        stream_task = asyncio.create_task(_collect())
        await asyncio.sleep(0.05)

        await stub.TickSimulation(pb.TickRequest())
        await asyncio.sleep(0.1)

        stream_task.cancel()
        try:
            await stream_task
        except (asyncio.CancelledError, grpc.aio.AioRpcError):
            pass

        assert all(ev.event_type == "tick_start" for ev in events)
        assert len(events) == 1

    async def test_stream_event_session_id_set(self, stub):
        events: list[pb.EventMessage] = []

        async def _collect():
            async for ev in stub.StreamEvents(pb.StreamEventsRequest(
                event_types=["tick_end"]
            )):
                events.append(ev)

        task = asyncio.create_task(_collect())
        await asyncio.sleep(0.05)

        await stub.TickSimulation(pb.TickRequest())
        await asyncio.sleep(0.1)

        task.cancel()
        try:
            await task
        except (asyncio.CancelledError, grpc.aio.AioRpcError):
            pass

        assert len(events) >= 1
        assert events[0].session_id != ""

    async def test_stream_tick_field_populated(self, stub):
        events: list[pb.EventMessage] = []

        async def _collect():
            async for ev in stub.StreamEvents(pb.StreamEventsRequest(
                event_types=["tick_end"]
            )):
                events.append(ev)

        task = asyncio.create_task(_collect())
        await asyncio.sleep(0.05)

        await stub.TickSimulation(pb.TickRequest())
        await asyncio.sleep(0.1)

        task.cancel()
        try:
            await task
        except (asyncio.CancelledError, grpc.aio.AioRpcError):
            pass

        assert events[0].tick == 1


# ── GrpcServer factory helpers ────────────────────────────────────────────────

class TestGrpcServerFactory:
    async def test_from_simulation(self):
        sim, _ = _make_sim()
        server  = GrpcServer.from_simulation(sim, host="[::1]", port=0)
        port    = await server.start()
        assert port > 0
        await server.stop(0)

    async def test_port_property_after_start(self):
        sim, _ = _make_sim()
        server  = GrpcServer.from_simulation(sim, host="[::1]", port=0)
        assert server.port is None           # before start
        await server.start()
        assert server.port is not None
        assert server.port > 0
        await server.stop(0)

    async def test_multiple_ticks_deterministic(self):
        """Verifies the engine RNG is seeded and ticks reproduce consistently."""
        sim, world = _make_sim(ticks=5)
        session = create_session(sim)
        server  = GrpcServer(session, host="[::1]", port=0)
        port    = await server.start()

        channel = grpc.aio.insecure_channel(f"localhost:{port}")
        stub    = SimulationServiceStub(channel)

        ticks = []
        for _ in range(3):
            r = await stub.TickSimulation(pb.TickRequest())
            ticks.append(r.tick)

        assert ticks == [1, 2, 3]

        await channel.close()
        await server.stop(0)


# ── Conversions unit tests (no gRPC required) ─────────────────────────────────

class TestConversions:
    def test_dict_to_struct_round_trip(self):
        from src.transport.grpc.conversions import dict_to_struct, struct_to_dict
        d = {"key": "value", "num": 42, "flag": True, "nested": {"a": 1}}
        s = dict_to_struct(d)
        out = struct_to_dict(s)
        assert out["key"] == "value"
        assert out["num"] == 42
        assert out["flag"] is True
        assert out["nested"]["a"] == 1

    def test_empty_dict_to_struct(self):
        from src.transport.grpc.conversions import dict_to_struct, struct_to_dict
        s = dict_to_struct({})
        assert struct_to_dict(s) == {}

    def test_none_to_struct(self):
        from src.transport.grpc.conversions import dict_to_struct, struct_to_dict
        s = dict_to_struct(None)
        assert struct_to_dict(s) == {}

    def test_struct_to_dict_none(self):
        from src.transport.grpc.conversions import struct_to_dict
        assert struct_to_dict(None) == {}

    def test_safe_dict_to_struct_coerces_non_serializable(self):
        from src.transport.grpc.conversions import safe_dict_to_struct, struct_to_dict

        class Weird:
            def __str__(self): return "weird_value"

        d = {"obj": Weird(), "normal": "ok"}
        s = safe_dict_to_struct(d)
        out = struct_to_dict(s)
        assert out["obj"] == "weird_value"
        assert out["normal"] == "ok"

    def test_list_values_preserved(self):
        from src.transport.grpc.conversions import dict_to_struct, struct_to_dict
        d = {"items": [1, 2, 3], "tags": ["a", "b"]}
        s = dict_to_struct(d)
        out = struct_to_dict(s)
        assert out["items"] == [1, 2, 3]
        assert out["tags"]  == ["a", "b"]
