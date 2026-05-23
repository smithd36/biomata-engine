"""
examples/patrol/sim/handlers.py
Action handlers for the patrol demo.

NavigateHandler: extracts target_x / target_z from Intent.parameters and
returns an engine_command that Unity's MoveActionHandler understands:
    {"type": "navigate", "x": float, "y": float, "z": float}

IdleHandler: no-op fallback.
"""
from __future__ import annotations

from src.contracts.action import ActionResult, Intent
from src.contracts.world import AgentView, WorldContext


class NavigateHandler:
    def execute(self, agent: AgentView, intent: Intent, context: WorldContext) -> ActionResult:
        p = intent.parameters or {}
        tx = float(p.get("target_x", 0.0))
        tz = float(p.get("target_z", 0.0))
        ty = float(p.get("target_y", 0.0))
        return ActionResult(
            success=True,
            outcome_text=f"navigate to ({tx:.2f}, {ty:.2f}, {tz:.2f})",
            engine_commands=[{"type": "navigate", "x": tx, "y": ty, "z": tz}],
        )


class IdleHandler:
    def execute(self, agent: AgentView, intent: Intent, context: WorldContext) -> ActionResult:
        return ActionResult(success=True, outcome_text="idle")
