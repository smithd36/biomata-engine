"""
src/config/schema.py
─────────────────────────────
Pydantic models for sim.yaml validation.

Every `class:` field accepts a dotted Python path. Extra keys on a component
config are passed as kwargs to the constructor.
"""
from __future__ import annotations

from typing import Any
from pydantic import BaseModel, Field, ConfigDict


class EngineConfig(BaseModel):
    ticks:     int = 20
    seed:      int = 42
    scheduler: str = "simultaneous"   # "simultaneous" | "sequential"
    log_level: str = "normal"         # "normal" | "verbose" | "quiet"


class ComponentConfig(BaseModel):
    """
    Any plugin component declared as { class: dotted.path, **kwargs }.
    Extra fields are forwarded to the constructor.
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
    capabilities: list[str]              = Field(default_factory=list)


class SimConfig(BaseModel):
    engine:   EngineConfig             = Field(default_factory=EngineConfig)
    world:    ComponentConfig
    registry: ComponentConfig | None   = None
    social:   ComponentConfig | None   = None
    llm:      dict[str, Any]           = Field(default_factory=dict)
    agents:   list[AgentConfig]        = Field(default_factory=list)
