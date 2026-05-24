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
            if not schema.tags or schema.tags & capabilities:
                result.append(schema)
        return result

    def actions_prompt_section(self) -> str:
        lines = ["AVAILABLE ACTIONS (use exactly these names):"]
        for schema, _ in self._entries.values():
            lines.append(schema.prompt_block())
        return "\n".join(lines)

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
