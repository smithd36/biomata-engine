"""
examples/village/sim/handlers.py
Action handlers for the village demo.

navigate  -> engine_command {"type":"navigate","x":f,"y":f,"z":f}
idle      -> no engine_command
interact  -> engine_command {"type":"interact","location":str}
speak     -> engine_command {"type":"speak","message":str}
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
            outcome_text=f"navigate to ({tx:.1f}, {ty:.1f}, {tz:.1f})",
            engine_commands=[{"type": "navigate", "x": tx, "y": ty, "z": tz}],
        )


class IdleHandler:
    def execute(self, agent: AgentView, intent: Intent, context: WorldContext) -> ActionResult:
        return ActionResult(success=True, outcome_text="idle")


class InteractHandler:
    def execute(self, agent: AgentView, intent: Intent, context: WorldContext) -> ActionResult:
        p = intent.parameters or {}
        location = str(p.get("location", p.get("target", "")))
        return ActionResult(
            success=True,
            outcome_text=f"interact at {location}",
            engine_commands=[{"type": "interact", "location": location}],
        )


class SpeakHandler:
    def execute(self, agent: AgentView, intent: Intent, context: WorldContext) -> ActionResult:
        p = intent.parameters or {}
        message = str(p.get("message", p.get("text", intent.reasoning or "")))
        return ActionResult(
            success=True,
            outcome_text=message,
            engine_commands=[{"type": "speak", "message": message}],
        )
