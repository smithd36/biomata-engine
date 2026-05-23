"""
src/plugins/builtin/idle_brain/brain.py
─────────────────────────────────────────
IdleBrain — the minimal Brain implementation.

Always returns the same Intent (default: action="idle"). Useful for:
- SDK / transport-level integration tests that need a registered agent
  but don't want to depend on Ollama, OpenAI, or a recorded replay file.
- New-project smoke tests where the user just wants to confirm the engine
  is wired correctly before plugging in a real brain.
- Placeholder while authoring a new action handler — register one NPC with
  IdleBrain so the registry/dispatch path is exercised end-to-end.

Construction is parameterless by default; YAML or RegisterAgent payloads can
override `action` / `reasoning` to make idle-like NPCs that report a custom
status string.
"""
from __future__ import annotations

from typing import Any

from src.contracts.action import Intent


class IdleBrain:
    """Brain that always returns the same Intent. No LLM, no I/O, no state."""

    def __init__(
        self,
        action:    str = "idle",
        target:    str | None = None,
        reasoning: str = "(idle brain)",
        parameters: dict[str, Any] | None = None,
    ) -> None:
        self._intent = Intent(
            action     = action,
            target     = target,
            reasoning  = reasoning,
            parameters = parameters or {},
        )

    async def decide(self, agent, observation, actions, context):  # noqa: D401
        """Return the configured Intent unchanged on every tick."""
        return self._intent
