"""
src/contracts/action.py
────────────────────────────────
Contracts for the action system. These are the stable types that flow
between engine, world, brain, and handlers.

  ActionKind    — who executes the action: HOST | ENGINE | HYBRID
  Intent        — what an agent wants to do (output of Brain.decide)
  ActionResult  — what actually happened (output of ActionHandler.execute)
  ActionHandler — user-implemented: one class per action
  ActionSchema  — metadata about an action (shown to the LLM, validated against)
"""
from __future__ import annotations

import json
import re
from dataclasses import dataclass, field
from enum import Enum
from typing import Any, Protocol, runtime_checkable


# ── ActionKind ────────────────────────────────────────────────────────────────

class ActionKind(str, Enum):
    HOST   = "host"    # host (Unity/renderer) executes via engine_commands; Python only packages the command
    ENGINE = "engine"  # Python executes; may mutate world state, inventory, social graph
    HYBRID = "hybrid"  # both: Python processing AND host commands


# ── Intent ────────────────────────────────────────────────────────────────────

@dataclass
class Intent:
    action:     str            = "idle"
    target:     str | None     = None
    parameters: dict[str, Any] = field(default_factory=dict)
    reasoning:  str            = ""


def parse_intent(raw: str, valid_actions: set[str] | None = None) -> Intent:
    """
    Parse an Intent from raw LLM output.
    valid_actions is used only for the keyword fallback — JSON path is open.
    """
    raw = re.sub(r"```(?:json)?", "", raw).strip().rstrip("`").strip()

    match = re.search(r"\{.*\}", raw, re.DOTALL)
    if match:
        try:
            data      = json.loads(match.group())
            action    = str(data.get("action", "idle")).lower().strip().replace(" ", "_")
            target    = data.get("target") or None
            params    = data.get("parameters", {})
            if not isinstance(params, dict):
                params = {}
            reasoning = str(data.get("reasoning", ""))[:150]
            return Intent(action=action, target=target,
                          parameters=params, reasoning=reasoning)
        except (json.JSONDecodeError, ValueError):
            pass

    # Keyword fallback — only match known action names, never raw words
    if valid_actions:
        for action in sorted(valid_actions, key=len, reverse=True):
            if action.replace("_", " ") in raw.lower() or action in raw.lower():
                return Intent(action=action, reasoning="(keyword fallback)")

    return Intent(action="idle", reasoning="(parse failed)")


# ── ActionResult ──────────────────────────────────────────────────────────────

@dataclass
class ActionResult:
    success:          bool
    outcome_text:     str                           # human-readable log line
    state_mutations:  dict[str, Any] = field(default_factory=dict)
    side_effects:     list[dict]     = field(default_factory=list)
    engine_commands:  list[dict]     = field(default_factory=list)
    # side_effect shapes:
    #   {"type": "social",  "from": id, "to": id, "delta": float}
    #   {"type": "event",   ...}   — future extensibility
    #
    # engine_commands shapes (host-defined; opaque to core engine):
    #   {"type": "navigate",      "destination": {...}}
    #   {"type": "set_animation", "clip": "walk"}
    #   {"type": "play_sound",    "clip": "attack_hit"}
    # Consumed by ExternalWorld.collect_commands() or TickSummary.engine_commands().


# ── ActionHandler ─────────────────────────────────────────────────────────────

@runtime_checkable
class ActionHandler(Protocol):
    def execute(
        self,
        agent:   "AgentView",           # noqa: F821
        intent:  Intent,
        context: "WorldContext",         # noqa: F821
    ) -> ActionResult:
        ...


# ── ActionSchema ──────────────────────────────────────────────────────────────

@dataclass
class ActionSchema:
    name:              str
    description:       str
    parameters_schema: dict[str, Any]  = field(default_factory=dict)
    kind:              ActionKind       = ActionKind.HYBRID
    tags:              frozenset[str]   = field(default_factory=frozenset)
    examples:          list[dict]       = field(default_factory=list)

    def prompt_block(self) -> str:
        kind_label = f"  [{self.kind.value}]" if self.kind != ActionKind.HYBRID else ""
        lines = [f"  {self.name}: {self.description}{kind_label}"]
        if self.parameters_schema:
            params = json.dumps(self.parameters_schema, separators=(",", ":"))
            lines.append(f"    params: {params}")
        if self.examples:
            ex_json = json.dumps(self.examples[0], separators=(",", ":"))
            lines.append(f"    example: {ex_json}")
        return "\n".join(lines)