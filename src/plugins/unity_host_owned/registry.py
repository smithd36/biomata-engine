from src.contracts.action import ActionHint, ActionResult, ActionSchema
from src.engine.registry import ActionRegistry
from src.plugins.builtin.ollama.registry import build_hosted_registry

class _EatHandler:
    def execute(self, agent, intent, context) -> ActionResult:
        target = intent.parameters.get("target", "")
        return ActionResult(
            success=True,
            outcome_text=f"{agent.name} eats {target}.",
            engine_commands=[{"type": "play_animation", "clip": "eat"}],
        )

def build_registry() -> ActionRegistry:
    registry = build_hosted_registry()   # gets idle, move, speak, interact
    registry.register(
        schema=ActionSchema(
            name="eat",
            description="Eat a nearby food source to reduce hunger.",
            execution_hint=ActionHint.HOST,
            parameters_schema={"target": "str? — name of the food source"},
            example={"action": "eat", "parameters": {"target": "apple"},
                     "reasoning": "Hunger is high and food is nearby."},
        ),
        handler=_EatHandler(),
    )
    return registry