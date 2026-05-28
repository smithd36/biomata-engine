"""
src/plugins/builtin/ollama/registry.py
──────────────────────────────────────
Default ActionRegistry for OllamaLLMBrain-driven simulations hosted in Unity.

Action names are chosen to match the built-in Unity ActionHandlerBase subclasses
exactly, so the brain's constrained vocabulary maps 1-to-1 to Unity components
without any custom C# handlers:

  idle     → no-op wait (IdleActionHandler or ignored gracefully)
  move     → MoveActionHandler  (translate + rotate toward target position)
  speak    → SpeakActionHandler (dialogue, subtitle, audio trigger)
  interact → InteractActionHandler (pick-up, use, examine, give)

Usage in sim.yaml:

  registry:
    class: src.plugins.builtin.ollama.registry.build_hosted_registry

The factory accepts no required arguments. The social parameter is injected
automatically by the loader when a social block is configured.
"""
from __future__ import annotations

from src.contracts.action import ActionKind, ActionResult, ActionSchema
from src.engine.registry import ActionRegistry


# ── Handlers ──────────────────────────────────────────────────────────────────

class _IdleHandler:
    def execute(self, agent, intent, context) -> ActionResult:
        return ActionResult(success=True, outcome_text=f"{agent.name} waits.")


class _MoveHandler:
    def execute(self, agent, intent, context) -> ActionResult:
        p = intent.parameters
        cmd: dict = {"type": "navigate"}
        if "target_x" in p:
            cmd["x"] = p["target_x"]
        if "target_y" in p:
            cmd["y"] = p["target_y"]
        if "target_z" in p:
            cmd["z"] = p["target_z"]
        if "destination" in p:
            cmd["destination"] = p["destination"]
        return ActionResult(
            success=True,
            outcome_text=f"{agent.name} moves.",
            engine_commands=[cmd],
        )


class _SpeakHandler:
    def execute(self, agent, intent, context) -> ActionResult:
        text = (
            intent.parameters.get("text")
            or intent.parameters.get("message")
            or "(says nothing)"
        )
        return ActionResult(
            success=True,
            outcome_text=f"{agent.name} says: {text}",
            engine_commands=[{"type": "speak", "text": text, "target": intent.target}],
        )


class _InteractHandler:
    def execute(self, agent, intent, context) -> ActionResult:
        target = intent.target or intent.parameters.get("target_id", "unknown")
        action = intent.parameters.get("interaction", "interact")
        return ActionResult(
            success=True,
            outcome_text=f"{agent.name} {action}s with {target}.",
            engine_commands=[{"type": "interact", "target": target, "interaction": action}],
        )


# ── Factory ───────────────────────────────────────────────────────────────────

def build_hosted_registry(social=None) -> ActionRegistry:
    """
    Build and return an ActionRegistry suitable for OllamaLLMBrain + HostedWorld.

    The four actions registered here correspond 1-to-1 with the built-in Unity
    ActionHandlerBase components (IdleActionHandler, MoveActionHandler,
    SpeakActionHandler, InteractActionHandler). Add those components to your
    agent's GameObject in Unity to handle all four without custom C#.
    """
    registry = ActionRegistry()

    registry.register(
        schema=ActionSchema(
            name="idle",
            description="Wait or do nothing for this tick. Use when there is no clear goal.",
            kind=ActionKind.ENGINE,
        ),
        handler=_IdleHandler(),
    )

    registry.register(
        schema=ActionSchema(
            name="move",
            description="Move toward a target position or destination.",
            kind=ActionKind.HOST,
            parameters_schema={
                "target_x":    "float? — world-space X coordinate",
                "target_y":    "float? — world-space Y coordinate (omit for ground-level)",
                "target_z":    "float? — world-space Z coordinate",
                "destination": "str?   — named location (e.g. 'market', 'gate')",
            },
            examples=[
                {"action": "move", "target": None,
                 "parameters": {"target_x": 5.0, "target_z": -3.0},
                 "reasoning": "Heading toward the market stall."},
            ],
        ),
        handler=_MoveHandler(),
    )

    registry.register(
        schema=ActionSchema(
            name="speak",
            description="Say something aloud to a nearby agent or to no one in particular.",
            kind=ActionKind.HOST,
            parameters_schema={
                "text": "str — the words to say",
            },
            examples=[
                {"action": "speak", "target": "agent_002",
                 "parameters": {"text": "Good day, friend!"},
                 "reasoning": "Greeting a nearby villager."},
            ],
        ),
        handler=_SpeakHandler(),
    )

    registry.register(
        schema=ActionSchema(
            name="interact",
            description="Interact with an agent or object (examine, pick up, use, give).",
            kind=ActionKind.HOST,
            parameters_schema={
                "interaction": "str? — what to do: examine | pickup | use | give",
                "target_id":   "str? — id of the object or agent to interact with",
            },
            examples=[
                {"action": "interact", "target": "chest_01",
                 "parameters": {"interaction": "open"},
                 "reasoning": "Opening the chest near the entrance."},
            ],
        ),
        handler=_InteractHandler(),
    )

    return registry
