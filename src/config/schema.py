"""
src/config/schema.py
─────────────────────────────
Pydantic models for sim.yaml validation.

Every `class:` field accepts a dotted Python path. Extra keys on a component
config are passed as kwargs to the constructor via ComponentConfig.kwargs().

Example — agent with starting inventory and sequential ordering:

  engine:
    ticks: 100
    scheduler: sequential
    scheduler_order: [hero_001, guard_001]   # step order for sequential mode

  agents:
    - id: hero_001
      name: Hero
      inventory:
        sword: 1
        gold: 50
      brain:
        class: src.plugins.builtin.ollama.brain.OllamaLLMBrain
        personality:
          traits: [brave]
          goals: [protect the village]
          backstory: A seasoned warrior.
"""
from __future__ import annotations

from typing import Any
from pydantic import BaseModel, Field, ConfigDict


class EngineConfig(BaseModel):
    ticks:           int       = 20
    seed:            int       = 42
    scheduler:       str       = "simultaneous"   # "simultaneous" | "sequential"
    scheduler_order: list[str] = Field(default_factory=list)
    log_level:       str       = "normal"         # "normal" | "verbose" | "quiet"


class ComponentConfig(BaseModel):
    """
    Any plugin component declared as { class: dotted.path, **kwargs }.
    Extra fields are forwarded to the constructor via kwargs().
    """
    model_config = ConfigDict(extra="allow", populate_by_name=True)

    class_: str = Field(..., alias="class")

    def kwargs(self) -> dict[str, Any]:
        """Return extra fields that should be forwarded to the constructor."""
        return self.model_extra or {}


class AgentConfig(BaseModel):
    id:           str
    name:         str
    brain:        ComponentConfig
    state_ext:    ComponentConfig | None = None
    memory:       ComponentConfig | None = None
    position:     dict[str, Any]  | None = None
    inventory:    dict[str, Any]         = Field(default_factory=dict)
    capabilities: list[str]              = Field(default_factory=list)


class SimConfig(BaseModel):
    engine:       EngineConfig             = Field(default_factory=EngineConfig)
    world:        ComponentConfig
    registry:     ComponentConfig | None   = None
    observations: ComponentConfig | None   = None
    social:       ComponentConfig | None   = None
    llm:          dict[str, Any]           = Field(default_factory=dict)
    agents:       list[AgentConfig]        = Field(default_factory=list)
