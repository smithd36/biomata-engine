"""
src/contracts/observation.py
────────────────────────────────
Contracts for the observation system.

  ObservationSchema    — documents one named observation slice
  ObservationProvider  — produces observation data for an agent each tick
"""
from __future__ import annotations

import json
from dataclasses import dataclass, field
from typing import Any, Protocol, runtime_checkable


# ── ObservationSchema ─────────────────────────────────────────────────────────

def _render_field_spec(spec: Any) -> str:
    if isinstance(spec, type):
        return spec.__name__
    return str(spec)


@dataclass
class ObservationSchema:
    """
    Documents one named observation slice that providers contribute to agents.

    Fields
    ------
    name
        Unique identifier. Used as the registry key and in prompt rendering.
    description
        One-line description rendered into the cognition prompt so LLMs
        understand what this observation slot means.
    payload_schema
        Maps field names to type annotations (Python types or descriptive strings).
        Rendered into the prompt for LLM context. Not enforced at runtime —
        observations come from trusted provider code, not LLMs.
    required_capabilities
        Capability gate. Empty frozenset = universal (every agent sees this).
        Non-empty = only agents whose capabilities intersect these tags see it.
        Same semantics as ActionSchema.required_capabilities.
    examples
        Concrete example dicts shown in the prompt. examples[0] is used.
    """
    name:                  str
    description:           str
    payload_schema:        dict[str, Any]        = field(default_factory=dict)
    required_capabilities: frozenset[str]         = field(default_factory=frozenset)
    examples:              list[dict]             = field(default_factory=list)
    # Deprecated: use required_capabilities=
    tags:                  frozenset[str] | None  = field(default=None, repr=False)

    def __post_init__(self) -> None:
        if self.tags is not None and not self.required_capabilities:
            self.required_capabilities = self.tags
        self.tags = self.required_capabilities

    def prompt_block(self) -> str:
        lines = [f"  {self.name}: {self.description}"]
        if self.payload_schema:
            rendered = {k: _render_field_spec(v) for k, v in self.payload_schema.items()}
            lines.append(f"    fields: {json.dumps(rendered, separators=(',', ':'))}")
        if self.examples:
            ex_json = json.dumps(self.examples[0], separators=(",", ":"))
            lines.append(f"    example: {ex_json}")
        return "\n".join(lines)


# ── ObservationProvider ───────────────────────────────────────────────────────

@runtime_checkable
class ObservationProvider(Protocol):
    """
    Produces a dict slice that is merged into an agent's observation each tick.

    Implementations
    ---------------
    - Return a flat dict — keys are merged directly into the observation.
    - Raise no exceptions — any error should be caught and returned as
      partial data or an empty dict. The registry silently skips failures.
    - The method signature intentionally does not include all engine internals.
      Providers that need the social system or other state should capture it
      in their constructor.

    Merge precedence (highest wins)
    --------------------------------
    engine identity > world.observe() > ObservationRegistry providers

    Reserved keys (engine always overwrites — providers must not emit these):
        agent_id, agent_name, inventory, state_ext, state_advice,
        state_str, nearby_agents
    """
    def collect(
        self,
        agent_id:     str,
        capabilities: frozenset[str],
        world:        "World",          # noqa: F821
    ) -> dict[str, Any]:
        ...
