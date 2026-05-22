"""
src/engine/agent.py
────────────────────────────
Agent is identity + runtime state only.

Owns: id, name, brain, memory, inventory, state_ext.
Does NOT own: prompt templates, LLM calls, personality, action dispatch, world interaction.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any

from src.contracts.state import AgentStateExtension
from src.contracts.memory import Memory
from src.contracts.brain import Brain


@dataclass
class Agent:
    id:        str
    name:      str
    brain:     Brain
    memory:    Memory
    inventory: dict[str, Any]              = field(default_factory=dict)
    state_ext: AgentStateExtension | None  = None

    def view(self) -> "AgentView":                          # noqa: F821
        from src.contracts.world import AgentView
        return AgentView.from_agent(self)
