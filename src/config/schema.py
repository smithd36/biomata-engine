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


class BrainRoleConfig(BaseModel):
    """
    Brain config within a role definition.
    Accepts either a provider shorthand or an explicit class path.
    Extra fields are forwarded as kwargs to the brain constructor.
    """
    model_config = ConfigDict(extra="allow", populate_by_name=True)
    provider: str | None = None
    class_:   str | None = Field(default=None, alias="class")

    def kwargs(self) -> dict[str, Any]:
        return self.model_extra or {}


class RoleConfig(BaseModel):
    """
    A flat role bundle: capabilities + default brain.

    Example
    -------
      Guard:
        capabilities: [guard, patrol, authority]
        brain:
          provider: ollama
          model: llama3
    """
    capabilities: list[str]              = Field(default_factory=list)
    brain:        BrainRoleConfig | None = None


class AgentConfig(BaseModel):
    id:           str
    name:         str
    role:         str | None          = None   # optional: reference to a declared role
    brain:        ComponentConfig | None = None  # optional when role supplies a brain
    state_ext:    ComponentConfig | None = None
    memory:       ComponentConfig | None = None
    position:     dict[str, Any]  | None = None
    inventory:    dict[str, Any]         = Field(default_factory=dict)
    capabilities: list[str]              = Field(default_factory=list)
    metadata:     dict[str, Any]         = Field(default_factory=dict)


class SimConfig(BaseModel):
    engine:       EngineConfig              = Field(default_factory=EngineConfig)
    world:        ComponentConfig
    registry:     ComponentConfig | None    = None
    observations: ComponentConfig | None    = None
    social:       ComponentConfig | None    = None
    llm:          dict[str, Any]            = Field(default_factory=dict)
    roles:        dict[str, RoleConfig]     = Field(default_factory=dict)
    agents:       list[AgentConfig]         = Field(default_factory=list)
