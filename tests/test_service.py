"""
tests/test_service.py
─────────────────────────────────────────────────
Tests for the service layer: DTOs, EventStreamAdapter, SimulationSession.

Run with:  pytest tests/test_service.py
"""
from __future__ import annotations

import asyncio

import pytest

from src.contracts.action import ActionResult, ActionSchema, Intent
from src.engine.agent import Agent
from src.engine.event_bus import EventBus, Event, TICK_END, TICK_START, ACTION_COMPLETED
from src.engine.registry import ActionRegistry
from src.engine.simulation import Simulation
from src.plugins.builtin.simple_memory.memory import SimpleMemory
from src.plugins.external.world import HostedWorld
from src.service import (
    AgentDecisionDTO,
    AgentObservationDTO,
    EventStreamAdapter,
    ServiceEvent,
    SessionError,
    SessionState,
    SimulationController,
    SimulationSession,
    SimulationStatus,
    StepRequest,
    StepResponse,
    create_session,
)


# ── Shared stubs ──────────────────────────────────────────────────────────────

class _FixedBrain:
    async def decide(self, agent, observation, actions, context) -> Intent:
        return Intent(action="act", parameters={"p": 1})


class _EchoHandler:
    def execute(self, agent, intent, context) -> ActionResult:
        return ActionResult(
            success=True,
            outcome_text="done",
            engine_commands=[{"type": "move", "dir": "north"}],
        )


class _NullHandler:
    def execute(self, agent, intent, context) -> ActionResult:
        return ActionResult(success=True, outcome_text="nothing")


def _make_registry(handler=None) -> ActionRegistry:
    reg = ActionRegistry()
    reg.register(ActionSchema("act", "Test action."), handler or _EchoHandler())
    return reg


def _make_sim(world=None, ticks: int = 5, handler=None) -> Simulation:
    world = world or HostedWorld()
    agent = Agent(
        id     = "agent_001",
        name   = "Alice",
        brain  = _FixedBrain(),
        memory = SimpleMemory(),
    )
    return Simulation(
        agents   = [agent],
        world    = world,
        registry = _make_registry(handler),
        config   = __import__("src.engine.simulation", fromlist=["SimulationConfig"]).SimulationConfig(ticks=ticks),
    )


# ── DTO unit tests ─────────────────────────────────────────────────────────────

class TestDTOs:
    def test_step_request_defaults(self):
        req = StepRequest()
        assert req.agent_observations == []
        assert req.world_metadata == {}

    def test_step_request_with_observations(self):
        obs = AgentObservationDTO(agent_id="a1", observation={"loc": "forest"})
        req = StepRequest(agent_observations=[obs], world_metadata={"time": "noon"})
        assert req.agent_observations[0].agent_id == "a1"
        assert req.world_metadata["time"] == "noon"

    def test_step_response_engine_commands_aggregation(self):
        d1 = AgentDecisionDTO("a1", "Alice", "move", engine_commands=[{"type": "nav"}])
        d2 = AgentDecisionDTO("a2", "Bob",   "idle", engine_commands=[{"type": "anim"}, {"type": "fx"}])
        resp = StepResponse(tick=3, decisions=[d1, d2])
        cmds = resp.engine_commands()
        assert len(cmds) == 3
        assert cmds[0]["type"] == "nav"
        assert cmds[2]["type"] == "fx"

    def test_step_response_empty_engine_commands(self):
        resp = StepResponse(tick=1, decisions=[])
        assert resp.engine_commands() == []

    def test_service_event_fields(self):
        ev = ServiceEvent(
            session_id="sess-1", event_type="tick_end",
            tick=2, agent_id="engine", data={"x": 1},
        )
        assert ev.session_id == "sess-1"
        assert ev.event_type == "tick_end"
        assert ev.data["x"] == 1

    def test_simulation_status_fields(self):
        st = SimulationStatus(
            session_id="s", state="running", tick=3,
            config_ticks=10, agent_count=2, has_world_snap=True,
        )
        assert st.tick == 3
        assert st.has_world_snap is True


# ── EventStreamAdapter ────────────────────────────────────────────────────────

class TestEventStreamAdapter:
    def _make_bus_adapter(self, session_id="sess-x"):
        bus     = EventBus()
        adapter = EventStreamAdapter(bus, session_id=session_id)
        return bus, adapter

    def test_adapter_forwards_engine_event(self):
        bus, adapter = self._make_bus_adapter()
        received: list[ServiceEvent] = []
        adapter.subscribe("tick_end", received.append)

        bus.emit(Event(type=TICK_END, tick=1, agent_id="engine", data={}))

        assert len(received) == 1
        assert received[0].event_type == "tick_end"
        assert received[0].tick == 1

    def test_adapter_filters_by_event_type(self):
        bus, adapter = self._make_bus_adapter()
        tick_events:   list[ServiceEvent] = []
        action_events: list[ServiceEvent] = []

        adapter.subscribe(TICK_START,        tick_events.append)
        adapter.subscribe(ACTION_COMPLETED,  action_events.append)

        bus.emit(Event(type=TICK_START,       tick=1, agent_id="engine"))
        bus.emit(Event(type=ACTION_COMPLETED, tick=1, agent_id="agent_001"))

        assert len(tick_events)   == 1
        assert len(action_events) == 1
        assert tick_events[0].event_type   == TICK_START
        assert action_events[0].event_type == ACTION_COMPLETED

    def test_adapter_none_filter_receives_all(self):
        bus, adapter = self._make_bus_adapter()
        all_events: list[ServiceEvent] = []
        adapter.subscribe(None, all_events.append)

        bus.emit(Event(type=TICK_START, tick=1, agent_id="engine"))
        bus.emit(Event(type=TICK_END,   tick=1, agent_id="engine"))

        assert len(all_events) == 2

    def test_unsubscribe_stops_delivery(self):
        bus, adapter = self._make_bus_adapter()
        received: list[ServiceEvent] = []
        sub_id = adapter.subscribe(None, received.append)

        bus.emit(Event(type=TICK_START, tick=1, agent_id="engine"))
        adapter.unsubscribe(sub_id)
        bus.emit(Event(type=TICK_END, tick=1, agent_id="engine"))

        assert len(received) == 1

    def test_unsubscribe_unknown_id_is_noop(self):
        _, adapter = self._make_bus_adapter()
        adapter.unsubscribe("nonexistent-id")   # must not raise

    def test_adapter_session_id_propagated(self):
        bus, adapter = self._make_bus_adapter(session_id="my-session")
        received: list[ServiceEvent] = []
        adapter.subscribe(None, received.append)

        bus.emit(Event(type=TICK_START, tick=1, agent_id="engine"))

        assert received[0].session_id == "my-session"

    def test_adapter_data_dict_is_copy(self):
        bus, adapter = self._make_bus_adapter()
        received: list[ServiceEvent] = []
        adapter.subscribe(None, received.append)

        original_data = {"key": "value"}
        bus.emit(Event(type=TICK_START, tick=1, agent_id="engine", data=original_data))
        received[0].data["key"] = "mutated"

        assert original_data["key"] == "value"   # engine Event unaffected

    def test_close_stops_all_deliveries(self):
        bus, adapter = self._make_bus_adapter()
        received: list[ServiceEvent] = []
        adapter.subscribe(None, received.append)

        bus.emit(Event(type=TICK_START, tick=1, agent_id="engine"))
        adapter.close()
        bus.emit(Event(type=TICK_END, tick=1, agent_id="engine"))

        assert len(received) == 1

    def test_multiple_subscribers_all_notified(self):
        bus, adapter = self._make_bus_adapter()
        a: list[ServiceEvent] = []
        b: list[ServiceEvent] = []
        adapter.subscribe(None, a.append)
        adapter.subscribe(None, b.append)

        bus.emit(Event(type=TICK_START, tick=1, agent_id="engine"))

        assert len(a) == 1
        assert len(b) == 1


# ── SimulationSession lifecycle ───────────────────────────────────────────────

class TestSimulationSessionLifecycle:
    def test_initial_state_is_created(self):
        sim     = _make_sim()
        session = SimulationSession(sim)
        assert session.state == SessionState.CREATED

    def test_session_id_is_set(self):
        sim     = _make_sim()
        session = SimulationSession(sim, session_id="fixed-id")
        assert session.session_id == "fixed-id"

    def test_session_id_auto_generated(self):
        sim     = _make_sim()
        session = SimulationSession(sim)
        assert len(session.session_id) > 0

    async def test_step_transitions_to_running_then_back(self):
        sim     = _make_sim()
        session = SimulationSession(sim)
        resp    = await session.step()
        # After a single step the session is still RUNNING (not STOPPED)
        assert session.state == SessionState.RUNNING
        assert isinstance(resp, StepResponse)

    async def test_step_returns_correct_tick(self):
        sim     = _make_sim()
        session = SimulationSession(sim)
        resp    = await session.step()
        assert resp.tick == 1

    async def test_run_advances_all_ticks(self):
        sim     = _make_sim(ticks=3)
        session = SimulationSession(sim)
        await session.run()
        assert session.tick == 3
        assert session.state == SessionState.STOPPED

    def test_shutdown_sets_stopped(self):
        sim     = _make_sim()
        session = SimulationSession(sim)
        session.shutdown()
        assert session.state == SessionState.STOPPED

    async def test_step_after_shutdown_raises(self):
        sim     = _make_sim()
        session = SimulationSession(sim)
        session.shutdown()
        with pytest.raises(SessionError):
            await session.step()

    async def test_run_after_shutdown_raises(self):
        sim     = _make_sim()
        session = SimulationSession(sim)
        session.shutdown()
        with pytest.raises(SessionError):
            await session.run()

    def test_pause_from_running_sets_paused(self):
        sim     = _make_sim()
        session = SimulationSession(sim)
        # Manually force RUNNING so pause() takes effect
        session._state = SessionState.RUNNING
        session.pause()
        assert session.state == SessionState.PAUSED

    def test_resume_from_paused_sets_running(self):
        sim     = _make_sim()
        session = SimulationSession(sim)
        session._state = SessionState.RUNNING
        session.pause()
        session.resume()
        assert session.state == SessionState.RUNNING

    def test_pause_from_non_running_is_noop(self):
        sim     = _make_sim()
        session = SimulationSession(sim)
        session.pause()   # state is CREATED — should be a no-op
        assert session.state == SessionState.CREATED

    def test_resume_from_non_paused_is_noop(self):
        sim     = _make_sim()
        session = SimulationSession(sim)
        session.resume()   # state is CREATED — no-op
        assert session.state == SessionState.CREATED


# ── StepResponse content ──────────────────────────────────────────────────────

class TestStepResponseContent:
    async def test_step_returns_agent_decision(self):
        world   = HostedWorld()
        sim     = _make_sim(world=world)
        session = SimulationSession(sim)

        world.push_observation("agent_001", {"location": "town"})
        resp = await session.step()

        assert len(resp.decisions) == 1
        d = resp.decisions[0]
        assert d.agent_id   == "agent_001"
        assert d.agent_name == "Alice"
        assert d.action     == "act"
        assert d.parameters == {"p": 1}
        assert d.outcome_text == "done"
        assert d.error is None

    async def test_step_engine_commands_present(self):
        world   = HostedWorld()
        sim     = _make_sim(world=world)
        session = SimulationSession(sim)
        resp    = await session.step()

        cmds = resp.engine_commands()
        assert len(cmds) == 1
        assert cmds[0]["type"] == "move"

    async def test_step_no_engine_commands_when_handler_returns_none(self):
        world   = HostedWorld()
        sim     = _make_sim(world=world, handler=_NullHandler())
        session = SimulationSession(sim)
        resp    = await session.step()
        assert resp.engine_commands() == []

    async def test_step_errors_list_empty_on_success(self):
        world   = HostedWorld()
        sim     = _make_sim(world=world)
        session = SimulationSession(sim)
        resp    = await session.step()
        assert resp.errors == []


# ── StepRequest observation injection ─────────────────────────────────────────

class TestStepRequestInjection:
    async def test_observation_injected_via_step_request(self):
        world   = HostedWorld()
        sim     = _make_sim(world=world)
        session = SimulationSession(sim)

        req = StepRequest(
            agent_observations=[
                AgentObservationDTO("agent_001", {"location": "castle"})
            ],
            world_metadata={"time_of_day": "dusk"},
        )
        await session.step(req)

        # After the tick the observation was consumed; confirm metadata was pushed
        assert world._metadata.get("time_of_day") == "dusk"

    async def test_empty_step_request_does_not_crash(self):
        world   = HostedWorld()
        sim     = _make_sim(world=world)
        session = SimulationSession(sim)
        resp    = await session.step(StepRequest())
        assert resp.tick == 1


# ── Session event subscriptions ───────────────────────────────────────────────

class TestSessionEventSubscriptions:
    async def test_subscribe_receives_tick_events(self):
        sim     = _make_sim()
        session = SimulationSession(sim)
        received: list[ServiceEvent] = []
        session.subscribe(TICK_END, received.append)

        await session.step()

        assert len(received) == 1
        assert received[0].event_type == TICK_END

    async def test_subscribe_none_receives_all(self):
        sim     = _make_sim()
        session = SimulationSession(sim)
        received: list[ServiceEvent] = []
        session.subscribe(None, received.append)

        await session.step()

        assert len(received) >= 2   # at minimum TICK_START + TICK_END

    async def test_unsubscribe_stops_delivery(self):
        sim     = _make_sim(ticks=3)
        session = SimulationSession(sim)
        received: list[ServiceEvent] = []
        sub_id = session.subscribe(TICK_END, received.append)

        await session.step()
        session.unsubscribe(sub_id)
        await session.step()

        assert len(received) == 1   # only the first tick

    async def test_subscription_session_id_matches(self):
        sim     = _make_sim()
        session = SimulationSession(sim, session_id="my-sess")
        received: list[ServiceEvent] = []
        session.subscribe(None, received.append)

        await session.step()

        assert all(e.session_id == "my-sess" for e in received)


# ── Status ────────────────────────────────────────────────────────────────────

class TestSimulationStatus:
    def test_status_initial(self):
        sim     = _make_sim()
        session = SimulationSession(sim)
        st      = session.status()
        assert st.state       == "created"
        assert st.tick        == 0
        assert st.agent_count == 1

    async def test_status_after_step(self):
        sim     = _make_sim(ticks=5)
        session = SimulationSession(sim)
        await session.step()
        st = session.status()
        assert st.tick  == 1
        assert st.state == "running"

    def test_status_after_shutdown(self):
        sim     = _make_sim()
        session = SimulationSession(sim)
        session.shutdown()
        st = session.status()
        assert st.state == "stopped"

    def test_status_config_ticks(self):
        sim     = _make_sim(ticks=42)
        session = SimulationSession(sim)
        assert session.status().config_ticks == 42

    def test_status_has_world_snap_hosted_world(self):
        world   = HostedWorld()
        sim     = _make_sim(world=world)
        session = SimulationSession(sim)
        assert session.status().has_world_snap is True


# ── create_session factory ────────────────────────────────────────────────────

class TestCreateSession:
    def test_create_session_returns_simulation_session(self):
        sim     = _make_sim()
        session = create_session(sim)
        assert isinstance(session, SimulationSession)

    def test_create_session_with_explicit_id(self):
        sim     = _make_sim()
        session = create_session(sim, session_id="explicit-id")
        assert session.session_id == "explicit-id"

    async def test_create_session_step_works(self):
        sim     = _make_sim()
        session = create_session(sim)
        resp    = await session.step()
        assert resp.tick == 1


# ── Protocol structural check ─────────────────────────────────────────────────

class TestProtocolCompliance:
    def test_simulation_session_satisfies_controller_protocol(self):
        sim     = _make_sim()
        session = SimulationSession(sim)
        assert isinstance(session, SimulationController)


# ── Snapshot through session ──────────────────────────────────────────────────

class TestSessionSnapshot:
    async def test_snapshot_restore_via_session(self):
        world   = HostedWorld()
        sim     = _make_sim(world=world, ticks=10)
        session = SimulationSession(sim)

        await session.step()
        snap = session.snapshot()
        assert snap.tick == 1

        # Run two more ticks then restore
        await session.step()
        await session.step()
        assert session.tick == 3

        session.restore(snap)
        assert session.tick == 1

    async def test_snapshot_tick_matches_session_tick(self):
        sim     = _make_sim(ticks=10)
        session = SimulationSession(sim)

        await session.step()
        await session.step()
        snap = session.snapshot()

        assert snap.tick == session.tick
