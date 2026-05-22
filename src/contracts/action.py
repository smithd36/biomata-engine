"""
src/contracts/action.py
────────────────────────────────
Contracts for the action system. These are the stable types that flow
between engine, world, brain, and handlers.

  Intent        — what an agent wants to do (output of Brain.decide)
  ActionResult  — what actually happened (output of ActionHandler.execute)
  ActionHandler — user-implemented: one class per action
  ActionSchema  — metadata about an action (shown to the LLM, validated against)
"""
from __future__ import annotations

import json
import re
from dataclasses import dataclass, field
from typing import Any, Protocol, runtime_checkable


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
    # side_effect shapes:
    #   {"type": "social",  "from": id, "to": id, "delta": float}
    #   {"type": "event",   ...}   — future extensibility


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
    parameters_schema: dict[str, Any] = field(default_factory=dict)

    def prompt_block(self) -> str:
        if self.parameters_schema:
            params = json.dumps(self.parameters_schema, separators=(",", ":"))
            return f"  {self.name}: {self.description}  params={params}"
        return f"  {self.name}: {self.description}"