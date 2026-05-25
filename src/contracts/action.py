"""
src/contracts/action.py
────────────────────────────────
Contracts for the action system. These are the stable types that flow
between engine, world, brain, and handlers.

  ActionKind           — who executes the action: HOST | ENGINE | HYBRID
  ActionValidationError — structured error from intent/parameter validation
  Intent               — what an agent wants to do (output of Brain.decide)
  ActionResult         — what actually happened (output of ActionHandler.execute)
  ActionHandler        — user-implemented: one class per action
  ActionSchema         — metadata about an action (shown to the LLM, validated against)
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


# ── ActionValidationError ─────────────────────────────────────────────────────

@dataclass
class ActionValidationError:
    """
    Structured error produced by intent or parameter validation.

    Codes:
      unknown_action   — action name not in registry
      capability_denied — agent lacks a required capability tag
      missing_param    — required parameter absent from intent.parameters
      type_mismatch    — parameter present but wrong Python type
    """
    code:    str
    message: str
    field:   str | None = None   # parameter name, if applicable


# ── Parameter-spec helpers ────────────────────────────────────────────────────

# Maps canonical string tokens → Python types
_PARAM_TYPE_MAP: dict[str, type] = {
    "str":     str,  "string":  str,
    "int":     int,  "integer": int,
    "float":   float,
    "bool":    bool, "boolean": bool,
}


def _parse_param_spec(spec: Any) -> tuple[type | None, bool]:
    """
    Parse a parameter spec value into (expected_type, is_required).

    Accepts:
      - Python type literal (str, int, float, bool) → required, type-validated
      - "float"  / "str" / "int" / "bool"           → required, type-validated
      - "float?" / "str?"                            → optional, type-validated
      - "float (optional ...)"  — any string with "optional" → optional
      - Descriptive strings ("string — what you say") → required str if first token matches
      - dict or unrecognised value                   → (None, True) — skip validation
    """
    if isinstance(spec, type) and spec in (str, int, float, bool):
        return spec, True

    if not isinstance(spec, str):
        return None, True   # dict / nested schema — skip

    s = spec.strip()
    optional = "optional" in s.lower()

    # "float?" shorthand
    if s.endswith("?"):
        s = s[:-1].strip()
        optional = True

    # Take the first whitespace/punctuation-separated token as the type name
    token = re.split(r"[\s,;:(|]", s)[0].lower()
    expected = _PARAM_TYPE_MAP.get(token)
    return expected, not optional


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

def _render_param_spec(spec: Any) -> str:
    """Render a parameter spec value as a human/LLM-readable string."""
    if isinstance(spec, type):
        return spec.__name__
    return str(spec)


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
            rendered = {k: _render_param_spec(v) for k, v in self.parameters_schema.items()}
            lines.append(f"    params: {json.dumps(rendered, separators=(',', ':'))}")
        if self.examples:
            ex_json = json.dumps(self.examples[0], separators=(",", ":"))
            lines.append(f"    example: {ex_json}")
        return "\n".join(lines)

    def validate_parameters(
        self, params: dict[str, Any]
    ) -> list[ActionValidationError]:
        """
        Validate intent parameters against this schema.

        Only validates parameters whose spec can be parsed to a known type.
        Unknown or descriptive specs (e.g. "north|south|east|west") are skipped
        for backward compatibility.

        int values are accepted where float is expected (silent coercion).
        """
        errors: list[ActionValidationError] = []
        for param_name, spec in self.parameters_schema.items():
            expected_type, required = _parse_param_spec(spec)
            value = params.get(param_name)

            if value is None:
                if required and expected_type is not None:
                    errors.append(ActionValidationError(
                        code    = "missing_param",
                        message = f"required parameter '{param_name}' is missing",
                        field   = param_name,
                    ))
                continue

            if expected_type is None:
                continue  # spec not parseable — skip type check

            # Allow silent int→float coercion (LLMs often omit the decimal point)
            if expected_type is float and isinstance(value, int):
                continue

            if not isinstance(value, expected_type):
                errors.append(ActionValidationError(
                    code    = "type_mismatch",
                    message = (
                        f"parameter '{param_name}' must be {expected_type.__name__}, "
                        f"got {type(value).__name__}"
                    ),
                    field   = param_name,
                ))
        return errors