"""
src/service/dto.py
──────────────────────────────────────────────────────────────
Data transfer objects for the simulation service layer.

These are the boundary types between the core engine and any external
transport (WebSocket, gRPC, HTTP, Unity, Unreal, etc.). The core engine
never imports these; they exist only in the service layer.

All DTOs are plain dataclasses — no serialization logic, no transport
concerns. The transport adapter is responsible for encoding/decoding.

StepRequest / StepResponse
──────────────────────────
Primary tick protocol. The host pushes state in via StepRequest (per-agent
observations + world metadata), calls step(), and receives a StepResponse
containing every agent's decision and any engine_commands to relay.

ServiceEvent
────────────
Carries engine EventBus events across the service boundary. The transport
receives these via subscription rather than polling.

SimulationStatus
────────────────
Snapshot of the controller's current operational state.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any


# ── Inbound DTOs (host → engine) ──────────────────────────────────────────────

@dataclass
class AgentObservationDTO:
    """
    One agent's current world-state as reported by the host.

    Passed inside StepRequest; the service layer fans them out to
    world.push_observation() before each tick.
    """
    agent_id:    str
    observation: dict[str, Any] = field(default_factory=dict)


@dataclass
class StepRequest:
    """
    Everything the engine needs to execute one cognition tick.

    agent_observations — per-agent world-state; required when the simulation
                         uses a HostedWorld. Omit for self-contained worlds.
    world_metadata     — global world state (time, weather, etc.).
    """
    agent_observations: list[AgentObservationDTO] = field(default_factory=list)
    world_metadata:     dict[str, Any]             = field(default_factory=dict)


# ── Outbound DTOs (engine → host) ─────────────────────────────────────────────

@dataclass
class AgentDecisionDTO:
    """
    One agent's decision output for a tick.

    action          — the action name the agent chose.
    parameters      — action parameters from the agent's intent.
    outcome_text    — human-readable result description.
    engine_commands — structured commands for the host to execute
                      (move, animate, spawn, etc.). Shape is action-defined.
    error           — non-None if this agent's step raised an exception.
    """
    agent_id:        str
    agent_name:      str
    action:          str
    parameters:      dict[str, Any]       = field(default_factory=dict)
    outcome_text:    str                  = ""
    engine_commands: list[dict[str, Any]] = field(default_factory=list)
    error:           str | None           = None


@dataclass
class StepResponse:
    """
    Full output from one engine tick.

    decisions   — one entry per agent that completed a step (no entry for
                  agents that errored; check errors instead).
    errors      — (agent_id, message) pairs for agents whose steps failed.
    tick        — the engine tick that was just completed.
    """
    tick:      int
    decisions: list[AgentDecisionDTO]     = field(default_factory=list)
    errors:    list[tuple[str, str]]      = field(default_factory=list)

    def engine_commands(self) -> list[dict[str, Any]]:
        """All engine_commands from every agent, in decision order."""
        return [cmd for d in self.decisions for cmd in d.engine_commands]


# ── Event streaming ────────────────────────────────────────────────────────────

@dataclass
class ServiceEvent:
    """
    A simulation event crossing the service boundary.

    Maps 1-to-1 to engine EventBus events but adds a session_id so
    a single event stream can carry events from multiple sessions.

    event_type matches the engine constants (TICK_START, ACTION_COMPLETED, …).
    """
    session_id:  str
    event_type:  str
    tick:        int
    agent_id:    str
    data:        dict[str, Any] = field(default_factory=dict)


# ── Status ─────────────────────────────────────────────────────────────────────

@dataclass
class SimulationStatus:
    """Current operational state of a SimulationSession."""
    session_id:    str
    state:         str       # SessionState.value
    tick:          int
    config_ticks:  int       # total ticks configured
    agent_count:   int
    has_world_snap: bool     # True if world implements Snapshotable
