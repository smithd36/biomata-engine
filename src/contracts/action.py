"""
src/contracts/action.py
────────────────────────────────
Contracts for the action system. These are the stable types that flow
between engine, world, brain, and handlers.

  ActionHint           — advisory LLM prompt label: HOST | ENGINE | HYBRID
                         (no effect on dispatch — see docstring)
  StateMutations       — typed container for the two mutation channels
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


# ── ActionHint ────────────────────────────────────────────────────────────────
#
# Advisory-only label. Appends "[host]" or "[engine]" to the action description
# in the LLM system prompt so the model understands where effects land.
#
# IMPORTANT: ActionHint has NO effect on dispatch, validation, or mutation
# logic. The engine never reads execution_hint during a tick. A handler with
# execution_hint=ENGINE can still return engine_commands; a handler with
# execution_hint=HOST can still mutate state_mutations. The label is
# informational for the LLM, not a behavioral constraint.

class ActionHint(str, Enum):
    HOST   = "host"    # effects delivered via engine_commands to the host (Unity)
    ENGINE = "engine"  # effects applied by Python (mutations, social graph, etc.)
    HYBRID = "hybrid"  # both channels; default — no label added to prompt

# Backwards-compatible alias — existing code using ActionKind continues to work.
ActionKind = ActionHint


# ── ActionValidationError ─────────────────────────────────────────────────────

@dataclass
class ActionValidationError:
    """
    Structured error produced by intent or parameter validation.

    Codes:
      unknown_action    — action name not in registry
      capability_denied — agent lacks a required capability tag
      missing_param     — required parameter absent from intent.parameters
      type_mismatch     — parameter present but wrong Python type
    """
    code:    str
    message: str
    field:   str | None = None   # parameter name, if applicable


# ── Parameter-spec helpers ────────────────────────────────────────────────────

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

    if s.endswith("?"):
        s = s[:-1].strip()
        optional = True

    token    = re.split(r"[\s,;:(|]", s)[0].lower()
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

    if valid_actions:
        for action in sorted(valid_actions, key=len, reverse=True):
            if action.replace("_", " ") in raw.lower() or action in raw.lower():
                return Intent(action=action, reasoning="(keyword fallback)")

    return Intent(action="idle", reasoning="(parse failed)")


# ── StateMutations ────────────────────────────────────────────────────────────

@dataclass
class StateMutations:
    """
    Typed container for the two Python-side mutation channels an ActionHandler
    can request. Replaces the stringly-typed state_mutations dict.

    inventory
        Item count deltas applied by the engine after handler returns.
        Positive = add items, negative = remove items.
        Each item is clamped at zero (cannot go below 0).
        Example: {"gold": 5, "torch": -1}

    ext
        Key-value pairs forwarded verbatim to StateExtension.apply_mutations().
        Semantics are defined by the StateExtension implementation; the engine
        does not inspect these keys. Keys not consumed by StateExtension are
        silently ignored.
        Example: {"stress": -10, "hunger": 2}
    """
    inventory: dict[str, int] = field(default_factory=dict)
    ext:       dict[str, Any] = field(default_factory=dict)


# ── ActionResult ──────────────────────────────────────────────────────────────

@dataclass
class ActionResult:
    success:          bool
    outcome_text:     str                   # human-readable log line; stored in memory
    mutations:        StateMutations = field(default_factory=StateMutations)
    side_effects:     list[dict]     = field(default_factory=list)
    engine_commands:  list[dict]     = field(default_factory=list)
    # side_effects shape: {"type": "social", "from": id, "to": id, "delta": float}
    #
    # engine_commands shapes (host-defined; opaque to core engine):
    #   {"type": "navigate",      "destination": {...}}
    #   {"type": "set_animation", "clip": "walk"}
    #   {"type": "play_sound",    "clip": "attack_hit"}
    # Consumed by ExternalWorld.collect_commands() → StepResponse → Unity.

    # Deprecated: use mutations=StateMutations(...) instead.
    # Passing state_mutations= still works — it is migrated to mutations in __post_init__.
    state_mutations:  dict[str, Any] | None = field(default=None, repr=False)

    def __post_init__(self) -> None:
        if self.state_mutations is not None:
            inv = self.state_mutations.get("inventory") or {}
            ext = {k: v for k, v in self.state_mutations.items() if k != "inventory"}
            self.mutations = StateMutations(inventory=dict(inv), ext=ext)


# ── MoveAction ───────────────────────────────────────────────────────────────

def _to_float(v: Any) -> float | None:
    if v is None:
        return None
    try:
        return float(v)
    except (TypeError, ValueError):
        return None


@dataclass
class MoveAction:
    """
    Typed parameter bag for move / navigate / walk / travel / go actions.

    v1 (position-based, existing):
        Set ``x`` / ``z`` or ``destination``. Unchanged from before.

    v2 (POI-semantic, Phase 3+):
        Set ``poi_id`` (the POI's id string from the observation) and
        optionally ``anchor`` (defaults to ``"approach"``).  Python emits a
        symbolic intent; Unity (``MoveActionHandler``) is the sole authority
        for resolving POI id → world coordinates via the live scene Transform.

    Construct from an ``Intent``::

        move = MoveAction.from_intent(intent)
        cmd  = move.to_navigate_command()

    Old brains that output only ``destination`` / ``x`` / ``z`` are unaffected:
    ``poi_id`` defaults to ``None``, ``anchor`` defaults to ``"approach"``.
    """
    destination: str | None   = None
    x:           float | None = None
    y:           float | None = None
    z:           float | None = None
    poi_id:      str | None   = None
    anchor:      str          = "approach"

    @classmethod
    def from_intent(cls, intent: "Intent") -> "MoveAction":
        """
        Extract move parameters from ``intent.parameters`` and ``intent.target``.

        Checks both the v1 keys (``x`` / ``z``, ``target_x`` / ``target_z``,
        ``destination``) and the v2 keys (``poi_id``, ``anchor``).
        All keys are optional — unset keys keep their dataclass defaults.
        """
        p = intent.parameters
        return cls(
            destination = intent.target or p.get("destination") or None,
            x           = _to_float(p.get("x") or p.get("target_x")),
            y           = _to_float(p.get("y") or p.get("target_y")),
            z           = _to_float(p.get("z") or p.get("target_z")),
            poi_id      = p.get("poi_id") or None,
            anchor      = str(p.get("anchor") or "approach"),
        )

    def to_navigate_command(self) -> "dict[str, Any]":
        """
        Build a ``{"type": "navigate", ...}`` engine_command dict.

        Preference order:

        1. Explicit ``x`` / ``z`` coords — emits ``{"type":"navigate","x":…,"y":…,"z":…}``
        2. ``poi_id`` — emits ``{"type":"navigate","destination":…}`` plus optional
           ``"anchor"`` key.  Unity (``MoveActionHandler``) is the sole authority for
           resolving POI id → world coordinates via the live scene Transform.
        3. ``destination`` string — emits ``{"type":"navigate","destination":…}``
        """
        # Path 1: explicit coordinates
        if self.x is not None and self.z is not None:
            return {"type": "navigate",
                    "x": self.x, "y": self.y or 0.0, "z": self.z}

        # Path 2: POI id — delegate spatial resolution to Unity entirely.
        # Python emits a symbolic intent; Unity resolves the anchor from the live Transform.
        if self.poi_id is not None:
            cmd: dict[str, Any] = {"type": "navigate", "destination": self.poi_id}
            if self.anchor != "approach":
                cmd["anchor"] = self.anchor
            return cmd

        # Path 3: plain destination string
        if self.destination:
            return {"type": "navigate", "destination": self.destination}

        return {"type": "navigate"}


# ── ActionHandler ─────────────────────────────────────────────────────────────

@runtime_checkable
class ActionHandler(Protocol):
    def execute(
        self,
        agent:   "AgentView",       # noqa: F821
        intent:  Intent,
        context: "WorldContext",    # noqa: F821
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
    name:                  str
    description:           str
    parameters_schema:     dict[str, Any]        = field(default_factory=dict)
    execution_hint:        ActionHint             = ActionHint.HYBRID
    required_capabilities: frozenset[str]         = field(default_factory=frozenset)
    example:               dict | None            = None
    # ── Deprecated aliases ────────────────────────────────────────────────────
    # tags=          → use required_capabilities=
    # kind=          → use execution_hint=
    # examples=[...] → use example={...}  (single dict, not a list)
    tags:     frozenset[str] | None  = field(default=None, repr=False)
    kind:     ActionHint | None      = field(default=None, repr=False)
    examples: list[dict] | None      = field(default=None, repr=False)

    def __post_init__(self) -> None:
        # tags → required_capabilities
        if self.tags is not None and not self.required_capabilities:
            self.required_capabilities = self.tags
        self.tags = self.required_capabilities

        # kind → execution_hint
        if self.kind is not None and self.execution_hint is ActionHint.HYBRID:
            self.execution_hint = self.kind
        self.kind = self.execution_hint

        # examples → example (take first item from old list form)
        if self.example is None and self.examples:
            self.example = self.examples[0]
        self.examples = [self.example] if self.example is not None else []

    def prompt_block(self) -> str:
        hint_label = f"  [{self.execution_hint.value}]" if self.execution_hint is not ActionHint.HYBRID else ""
        lines      = [f"  {self.name}: {self.description}{hint_label}"]
        if self.parameters_schema:
            rendered = {k: _render_param_spec(v) for k, v in self.parameters_schema.items()}
            lines.append(f"    params: {json.dumps(rendered, separators=(',', ':'))}")
        if self.example:
            ex_json = json.dumps(self.example, separators=(",", ":"))
            lines.append(f"    example: {ex_json}")
        return "\n".join(lines)

    def validate_parameters(
        self, params: dict[str, Any]
    ) -> list[ActionValidationError]:
        """
        Validate intent parameters against this schema.

        Only validates parameters whose spec resolves to a known type token.
        Descriptive specs (e.g. "north|south|east|west") are skipped.
        int is silently accepted where float is expected.
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
                continue

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
