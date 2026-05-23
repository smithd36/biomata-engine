"""
tests/test_websocket_transport.py
─────────────────────────────────
Integration tests for src/transport/websocket/.

Spins up a real WebSocketServer on a random port and drives it through the
websockets client API. Validates that every method on the wire protocol round-
trips correctly and that the event stream delivers engine events.
"""
from __future__ import annotations

import asyncio
import json
import uuid
from contextlib import asynccontextmanager

import pytest
import websockets

from src.contracts.action import ActionResult, ActionSchema, Intent
from src.engine.agent import Agent
from src.engine.registry import ActionRegistry
from src.engine.simulation import Simulation, SimulationConfig
from src.plugins.builtin.simple_memory.memory import SimpleMemory
from src.plugins.external.world import HostedWorld
from src.service import SimulationSession
from src.transport.websocket.protocol import Method
from src.transport.websocket.server import WebSocketServer


# ── Test fixtures ────────────────────────────────────────────────────────────

class _FixedBrain:
    async def decide(self, agent, observation, actions, context):
        return Intent(action="act", parameters={"p": 1})


class _EchoHandler:
    def execute(self, agent, intent, context):
        return ActionResult(
            success         = True,
            outcome_text    = "done",
            engine_commands = [{"type": "move", "dir": "north"}],
        )


def _make_sim(ticks: int = 5) -> Simulation:
    world = HostedWorld()
    agent = Agent(
        id     = "agent_001",
        name   = "Alice",
        brain  = _FixedBrain(),
        memory = SimpleMemory(),
    )
    reg = ActionRegistry()
    reg.register(ActionSchema("act", "Test action."), _EchoHandler())
    return Simulation(
        agents   = [agent],
        world    = world,
        registry = reg,
        config   = SimulationConfig(ticks=ticks),
    )


@asynccontextmanager
async def _running_server(session: SimulationSession):
    server = WebSocketServer(session, host="127.0.0.1", port=0)
    port = await server.start()
    try:
        yield server, port
    finally:
        await server.stop()


# ── Wire helpers ─────────────────────────────────────────────────────────────
#
# The server interleaves event frames with response frames on the same socket.
# We share a small per-connection event buffer so _request can dequeue events
# it sees while waiting for its correlated response, and _collect_events can
# read them back later. This mirrors what a real client would do — keep a
# single receive loop and demultiplex by frame type.

class _Client:
    def __init__(self, ws):
        self.ws = ws
        self.pending_events: list[dict] = []

    async def request(self, method: str, params: dict | None = None, timeout: float = 5.0) -> dict:
        req_id = uuid.uuid4().hex
        await self.ws.send(json.dumps({
            "type":   "req",
            "id":     req_id,
            "method": method,
            "params": params or {},
        }))
        deadline = asyncio.get_event_loop().time() + timeout
        while True:
            remaining = deadline - asyncio.get_event_loop().time()
            if remaining <= 0:
                raise TimeoutError(f"no response for {method}")
            raw = await asyncio.wait_for(self.ws.recv(), timeout=remaining)
            msg = json.loads(raw)
            if msg.get("type") == "evt":
                # Stash for later — don't lose events that arrive between
                # our request and its response.
                self.pending_events.append(msg)
                continue
            if msg.get("type") == "res" and msg.get("id") == req_id:
                return msg

    async def collect_events(self, until: int, timeout: float = 2.0) -> list[dict]:
        events = list(self.pending_events)
        self.pending_events.clear()
        deadline = asyncio.get_event_loop().time() + timeout
        while len(events) < until:
            remaining = deadline - asyncio.get_event_loop().time()
            if remaining <= 0:
                break
            try:
                raw = await asyncio.wait_for(self.ws.recv(), timeout=remaining)
            except asyncio.TimeoutError:
                break
            msg = json.loads(raw)
            if msg.get("type") == "evt":
                events.append(msg)
        return events


# Compatibility shims so existing tests don't need rewriting wholesale.
async def _request(ws, method: str, params: dict | None = None, timeout: float = 5.0) -> dict:
    # Used by tests that don't care about events — events get silently dropped.
    return await _Client(ws).request(method, params, timeout)


# ── Tests ────────────────────────────────────────────────────────────────────

class TestWebSocketTransportLifecycle:
    async def test_server_starts_and_stops(self):
        sim     = _make_sim()
        session = SimulationSession(sim)
        async with _running_server(session) as (server, port):
            assert port > 0
            assert server.port == port

    async def test_client_connects(self):
        sim     = _make_sim()
        session = SimulationSession(sim)
        async with _running_server(session) as (_, port):
            async with websockets.connect(f"ws://127.0.0.1:{port}") as ws:
                resp = await _request(ws, Method.HEALTH_CHECK)
                assert resp["ok"] is True
                assert resp["result"]["status"] == "ok"


class TestWebSocketHealthCheck:
    async def test_health_returns_status(self):
        sim     = _make_sim()
        session = SimulationSession(sim)
        async with _running_server(session) as (_, port):
            async with websockets.connect(f"ws://127.0.0.1:{port}") as ws:
                resp = await _request(ws, Method.HEALTH_CHECK)
                result = resp["result"]
                assert result["status"]        == "ok"
                assert result["agent_count"]   == 1
                assert result["session_state"] == "created"
                assert result["tick"]          == 0


class TestWebSocketTick:
    async def test_tick_returns_decisions(self):
        sim     = _make_sim()
        session = SimulationSession(sim)
        async with _running_server(session) as (_, port):
            async with websockets.connect(f"ws://127.0.0.1:{port}") as ws:
                resp = await _request(ws, Method.TICK, {
                    "agent_observations": [
                        {"agent_id": "agent_001", "observation": {"location": "town"}},
                    ],
                    "world_metadata": {"weather": "clear"},
                })
                result = resp["result"]
                assert result["tick"] == 1
                assert len(result["decisions"]) == 1
                d = result["decisions"][0]
                assert d["agent_id"]        == "agent_001"
                assert d["action"]          == "act"
                assert d["parameters"]      == {"p": 1}
                assert d["engine_commands"] == [{"type": "move", "dir": "north"}]

    async def test_multiple_ticks_advance_counter(self):
        sim     = _make_sim()
        session = SimulationSession(sim)
        async with _running_server(session) as (_, port):
            async with websockets.connect(f"ws://127.0.0.1:{port}") as ws:
                for expected in (1, 2, 3):
                    r = await _request(ws, Method.TICK, {})
                    assert r["result"]["tick"] == expected


class TestWebSocketAgentLifecycle:
    async def test_register_then_remove(self):
        sim     = _make_sim()
        session = SimulationSession(sim)
        async with _running_server(session) as (_, port):
            async with websockets.connect(f"ws://127.0.0.1:{port}") as ws:
                # Register a second agent backed by ReplayBrain.
                resp = await _request(ws, Method.REGISTER_AGENT, {
                    "agent_id":     "agent_002",
                    "agent_name":   "Bob",
                    "brain_class":  "src.plugins.builtin.idle_brain.brain.IdleBrain",
                })
                assert resp["result"]["success"] is True

                health = await _request(ws, Method.HEALTH_CHECK)
                assert health["result"]["agent_count"] == 2

                # Remove it.
                rm = await _request(ws, Method.REMOVE_AGENT, {"agent_id": "agent_002"})
                assert rm["result"]["success"] is True

                health = await _request(ws, Method.HEALTH_CHECK)
                assert health["result"]["agent_count"] == 1

    async def test_register_duplicate_id_fails_cleanly(self):
        sim     = _make_sim()
        session = SimulationSession(sim)
        async with _running_server(session) as (_, port):
            async with websockets.connect(f"ws://127.0.0.1:{port}") as ws:
                resp = await _request(ws, Method.REGISTER_AGENT, {
                    "agent_id":   "agent_001",   # already present in the sim
                    "agent_name": "DuplicateAlice",
                    "brain_class": "src.plugins.builtin.replay_brain.brain.ReplayBrain",
                })
                assert resp["result"]["success"] is False
                assert "already registered" in resp["result"]["error"]


class TestWebSocketLifecycleControl:
    async def test_pause_resume_round_trip(self):
        sim     = _make_sim()
        session = SimulationSession(sim)
        # pause/resume only apply once RUNNING; force-step first.
        async with _running_server(session) as (_, port):
            async with websockets.connect(f"ws://127.0.0.1:{port}") as ws:
                await _request(ws, Method.TICK, {})
                p = await _request(ws, Method.PAUSE)
                assert p["result"]["state"] == "paused"
                r = await _request(ws, Method.RESUME)
                assert r["result"]["state"] == "running"


class TestWebSocketEvents:
    async def test_subscribe_then_tick_delivers_events(self):
        sim     = _make_sim()
        session = SimulationSession(sim)
        async with _running_server(session) as (_, port):
            async with websockets.connect(f"ws://127.0.0.1:{port}") as ws:
                c = _Client(ws)
                sub = await c.request(Method.SUBSCRIBE_EVENTS, {})
                assert sub["result"]["subscribed"] is True

                # Trigger a tick — should emit tick_start, action_completed, tick_end.
                await c.request(Method.TICK, {})

                events = await c.collect_events(until=3, timeout=2.0)
                etypes = {e["event_type"] for e in events}
                assert "tick_start"       in etypes
                assert "tick_end"         in etypes
                assert "action_completed" in etypes

    async def test_unsubscribe_stops_event_flow(self):
        sim     = _make_sim()
        session = SimulationSession(sim)
        async with _running_server(session) as (_, port):
            async with websockets.connect(f"ws://127.0.0.1:{port}") as ws:
                c = _Client(ws)
                await c.request(Method.SUBSCRIBE_EVENTS, {})
                await c.request(Method.TICK, {})
                _ = await c.collect_events(until=3, timeout=1.0)

                await c.request(Method.UNSUBSCRIBE_EVENTS)
                # Drain any in-flight events from the last tick.
                _ = await c.collect_events(until=10, timeout=0.5)

                # Next tick — no new events should arrive.
                await c.request(Method.TICK, {})
                new_events = await c.collect_events(until=1, timeout=0.5)
                assert new_events == []

    async def test_event_filter_only_delivers_matching(self):
        sim     = _make_sim()
        session = SimulationSession(sim)
        async with _running_server(session) as (_, port):
            async with websockets.connect(f"ws://127.0.0.1:{port}") as ws:
                c = _Client(ws)
                await c.request(Method.SUBSCRIBE_EVENTS, {"event_types": ["tick_end"]})
                await c.request(Method.TICK, {})
                events = await c.collect_events(until=1, timeout=1.0)
                assert all(e["event_type"] == "tick_end" for e in events)
                assert len(events) >= 1


class TestWebSocketSnapshot:
    async def test_snapshot_round_trip(self):
        sim     = _make_sim(ticks=10)
        session = SimulationSession(sim)
        async with _running_server(session) as (_, port):
            async with websockets.connect(f"ws://127.0.0.1:{port}") as ws:
                await _request(ws, Method.TICK, {})
                snap = await _request(ws, Method.SNAPSHOT)
                assert snap["result"]["tick"] == 1
                b64 = snap["result"]["data_b64"]
                assert isinstance(b64, str) and len(b64) > 0

                # Advance, then restore back to tick 1.
                await _request(ws, Method.TICK, {})
                await _request(ws, Method.TICK, {})
                health = await _request(ws, Method.HEALTH_CHECK)
                assert health["result"]["tick"] == 3

                restore = await _request(ws, Method.RESTORE, {"data_b64": b64})
                assert restore["result"]["success"] is True
                assert restore["result"]["tick"]    == 1


class TestWebSocketErrorPaths:
    async def test_unknown_method_returns_error(self):
        sim     = _make_sim()
        session = SimulationSession(sim)
        async with _running_server(session) as (_, port):
            async with websockets.connect(f"ws://127.0.0.1:{port}") as ws:
                resp = await _request(ws, "no_such_method")
                assert resp["ok"] is False
                assert "unknown method" in resp["error"]

    async def test_malformed_request_does_not_close_connection(self):
        sim     = _make_sim()
        session = SimulationSession(sim)
        async with _running_server(session) as (_, port):
            async with websockets.connect(f"ws://127.0.0.1:{port}") as ws:
                await ws.send("not-valid-json")
                # Server should send an error frame back with id=None.
                raw = await asyncio.wait_for(ws.recv(), timeout=1.0)
                msg = json.loads(raw)
                assert msg["type"] == "res"
                assert msg["ok"]   is False
                # Connection still alive.
                resp = await _request(ws, Method.HEALTH_CHECK)
                assert resp["ok"] is True
