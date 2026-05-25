"""
src/contracts/brain.py
───────────────────────────────
Brain is the cognition contract. Decouples *how an agent decides* from
agent identity and runtime state.

Implementations can be:
  LLMBrain          — calls an LLM
  RuleBasedBrain    — deterministic scripted logic
  UtilityBrain      — scores actions by utility function
  RLBrain           — wraps a trained policy
  HybridBrain       — composes multiple brains

Personality, backstory, prompt templates — all live in the Brain implementation,
not in Agent. Agent is identity + state only.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Callable, Protocol, runtime_checkable

from .action import Intent, ActionSchema
from .observation import ObservationSchema
from .world import AgentView

# Stable type alias — world observation is a flat dict; shape is world-defined.
Observation = dict[str, Any]


@dataclass
class BrainContext:
    """Simulation-level context passed to every Brain.decide() call."""
    tick:                int
    memory:              str                    = ""
    metadata:            dict[str, Any]         = field(default_factory=dict)
    observation_schemas: list[ObservationSchema] = field(default_factory=list)
    # Optional event emitter — injected by engine so brains can fire BRAIN_DECIDED events.
    # Callable[[Event], None]; typed as Any to avoid a circular import with event_bus.
    emit:                Any                    = None


@runtime_checkable
class Closeable(Protocol):
    """
    Optional lifecycle protocol for Brain implementations that hold external
    resources (open file handles, network connections, worker threads, etc.).

    The engine calls close() on every agent's brain at simulation teardown if
    the brain implements this protocol.  Implementing it is not mandatory —
    brains without open resources need not implement it.

    Implementations must be idempotent: calling close() more than once must
    not raise.
    """
    def close(self) -> None: ...


@runtime_checkable
class Brain(Protocol):
    async def decide(
        self,
        agent:       AgentView,
        observation: Observation,
        actions:     list[ActionSchema],
        context:     BrainContext,
    ) -> Intent:
        """
        Decide the agent's next action.

        Parameters
        ----------
        agent
            Read-only snapshot of the deciding agent (id, name, inventory, ext).
        observation
            World perception assembled by the engine — structure is world-defined.
        actions
            All registered ActionSchema objects. Brain MUST return an Intent
            whose action matches one of these names (or "idle" as fallback).
        context
            Tick number, agent memory, world metadata, and observation schemas.
            Social data arrives via the observation dict, not this context.
        """
        ...
