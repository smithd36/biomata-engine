"""
examples/village/sim/handlers.py
──────────────────────────────────
Action handlers for the village demo.

HOST actions (produce engine_commands relayed to Unity):
  navigate  → {"type":"navigate","x":f,"y":f,"z":f}
  speak     → {"type":"speak","message":str}
  interact  → {"type":"interact","location":str}

ENGINE actions (Python-side only, no host command):
  idle      → no-op (agent stays still)

HYBRID actions:
  socialize → delivers message to ConversationInbox AND emits host command
              AND emits a social side_effect routed through the event bus:
                {"type":"social","from":agent_id,"to":target_id,"delta":0.02}
              SocialEffectSubscriber (wired by loader) applies this to
              VillageRelationships via the canonical SocialSystem.update() path.
              Handlers do NOT directly mutate relationship state.
"""
from __future__ import annotations

from src.contracts.action import ActionResult, Intent
from src.contracts.world import AgentView, WorldContext
from src.engine.conversation import ConversationInbox


# ── HOST handlers ─────────────────────────────────────────────────────────────

class NavigateHandler:
    def execute(self, agent: AgentView, intent: Intent, context: WorldContext) -> ActionResult:
        p  = intent.parameters or {}
        tx = float(p.get("target_x", 0.0))
        tz = float(p.get("target_z", 0.0))
        ty = float(p.get("target_y", 0.0))
        return ActionResult(
            success      = True,
            outcome_text = f"navigate to ({tx:.1f}, {ty:.1f}, {tz:.1f})",
            engine_commands = [{"type": "navigate", "x": tx, "y": ty, "z": tz}],
        )


class InteractHandler:
    def execute(self, agent: AgentView, intent: Intent, context: WorldContext) -> ActionResult:
        p        = intent.parameters or {}
        location = str(p.get("location", p.get("target", "")))
        return ActionResult(
            success      = True,
            outcome_text = f"interact at {location}",
            engine_commands = [{"type": "interact", "location": location}],
        )


class SpeakHandler:
    def __init__(self, inbox: ConversationInbox | None = None) -> None:
        self._inbox = inbox

    def execute(self, agent: AgentView, intent: Intent, context: WorldContext) -> ActionResult:
        p         = intent.parameters or {}
        message   = str(p.get("message", p.get("text", intent.reasoning or "")))
        target_id = str(p.get("target_id", "")).strip()

        if target_id and self._inbox is not None:
            self._inbox.deliver(target_id, agent.id, message)

        return ActionResult(
            success         = True,
            outcome_text    = message,
            engine_commands = [{"type": "speak", "message": message}],
        )


# ── ENGINE handlers ───────────────────────────────────────────────────────────

class IdleHandler:
    def execute(self, agent: AgentView, intent: Intent, context: WorldContext) -> ActionResult:
        return ActionResult(success=True, outcome_text="idle")


# ── HYBRID handlers ───────────────────────────────────────────────────────────

class SocializeHandler:
    """
    Python side: delivers message to ConversationInbox so the target receives
                 it next tick, and emits a social side_effect.
    Host side:   emits {"type":"socialize","target_id":str,"message":str} so
                 Unity can animate the interaction.

    Social state is updated through the canonical platform path:
      side_effects → SocialEffectSubscriber → SocialSystem.update()
    This handler does not import or mutate VillageRelationships directly.
    """

    def __init__(self, inbox: ConversationInbox | None = None) -> None:
        self._inbox = inbox

    def execute(self, agent: AgentView, intent: Intent, context: WorldContext) -> ActionResult:
        p         = intent.parameters or {}
        target_id = str(p.get("target_id", ""))
        message   = str(p.get("message",   ""))

        side_effects: list[dict] = []
        if target_id:
            if self._inbox is not None:
                self._inbox.deliver(target_id, agent.id, message)
            side_effects = [{
                "type":  "social",
                "from":  agent.id,
                "to":    target_id,
                "delta": 0.02,
            }]

        return ActionResult(
            success         = True,
            outcome_text    = f'→ {target_id}: "{message}"',
            side_effects    = side_effects,
            engine_commands = [{
                "type":      "socialize",
                "target_id": target_id,
                "message":   message,
            }],
        )
