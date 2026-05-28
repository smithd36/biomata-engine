"""
src/engine/registry.py
────────────────────────────────
ActionRegistry maps action names → (schema, handler).

Users register their own handlers; the engine never hard-codes action semantics.
"""
from __future__ import annotations

from src.contracts.action import (
    Intent,
    ActionResult,
    ActionHandler,
    ActionSchema,
    ActionValidationError,
)


class ActionRegistry:
    """
    Central registry that maps action names → (schema, handler).

    Usage
    -----
    registry = ActionRegistry()
    registry.register(
        schema=ActionSchema("gather_food", "Collect food from your location."),
        handler=GatherFoodHandler(),
    )
    result = registry.dispatch(intent, agent_view, world_context)
    """

    def __init__(self):
        self._entries: dict[str, tuple[ActionSchema, ActionHandler]] = {}

    def register(self, schema: ActionSchema, handler: ActionHandler) -> None:
        if schema.name in self._entries:
            raise ValueError(f"Action '{schema.name}' is already registered.")
        self._entries[schema.name] = (schema, handler)

    def action_names(self) -> list[str]:
        return list(self._entries.keys())

    def schemas(self) -> list[ActionSchema]:
        """Return all registered ActionSchema objects."""
        return [schema for schema, _ in self._entries.values()]

    def schemas_for(self, capabilities: "frozenset[str]") -> list[ActionSchema]:
        """
        Return the ActionSchemas an agent with the given capabilities may use.

        Visibility rules:
          - Untagged schema (tags == frozenset())  → universal; always visible.
          - Tagged schema                          → visible only if the agent's
            capabilities intersect the schema's tags (at least one tag matches).

        Pass an empty frozenset for agents with no special capabilities —
        they will see all universal (untagged) actions.
        """
        result = []
        for schema, _ in self._entries.values():
            if not schema.required_capabilities or schema.required_capabilities & capabilities:
                result.append(schema)
        return result

    def validate_intent(
        self,
        intent:       Intent,
        capabilities: "frozenset[str]",
    ) -> list[ActionValidationError]:
        """
        Validate an Intent before dispatch.

        Checks (in order):
          1. Action name is registered.
          2. Agent capabilities satisfy the schema's tag requirements.
          3. Intent parameters satisfy the schema's parameter specs.

        Returns an empty list when the intent is valid.
        Never raises — all failures are returned as structured errors.
        """
        entry = self._entries.get(intent.action)
        if entry is None:
            return [ActionValidationError(
                code    = "unknown_action",
                message = f"action '{intent.action}' is not registered",
            )]

        schema, _ = entry

        if schema.required_capabilities and not (schema.required_capabilities & capabilities):
            allowed = sorted(schema.required_capabilities)
            have    = sorted(capabilities) or ["none"]
            return [ActionValidationError(
                code    = "capability_denied",
                message = (
                    f"action '{intent.action}' requires capability "
                    f"{allowed} — agent has {have}"
                ),
            )]

        return schema.validate_parameters(intent.parameters or {})

    def dispatch(
        self,
        intent:  Intent,
        agent:   "AgentView",       # noqa: F821
        context: "WorldContext",    # noqa: F821
    ) -> ActionResult:
        entry = self._entries.get(intent.action)
        if entry is None:
            return ActionResult(
                success=False,
                outcome_text=f"unknown action '{intent.action}' — idled",
            )
        _, handler = entry
        return handler.execute(agent, intent, context)
