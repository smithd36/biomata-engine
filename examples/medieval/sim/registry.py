"""
examples/medieval/sim/registry.py
───────────────────────────────────
Builds the ActionRegistry for the medieval simulation.
Called by Simulation.from_config() or directly in main.py.
"""
from src.engine.registry import ActionRegistry
from src.contracts.action import ActionKind, ActionSchema
from examples.medieval.sim.handlers import (
    MoveHandler, GatherFoodHandler, GatherWoodHandler, RestHandler,
    ExploreHandler, SpeakHandler, TradeHandler, GiveHandler,
    AttackHandler, IdleHandler,
)


def build_medieval_registry() -> ActionRegistry:
    registry = ActionRegistry()
    registry.register(ActionSchema("move",        "Move in a direction.",
                                   {"direction": "north|south|east|west"},
                                   kind=ActionKind.ENGINE), MoveHandler())
    registry.register(ActionSchema("gather_food", "Collect food from your current location.",
                                   kind=ActionKind.ENGINE), GatherFoodHandler())
    registry.register(ActionSchema("gather_wood", "Collect wood from your current location.",
                                   kind=ActionKind.ENGINE), GatherWoodHandler())
    registry.register(ActionSchema("rest",        "Rest to recover energy.",
                                   kind=ActionKind.ENGINE), RestHandler())
    registry.register(ActionSchema("explore",     "Explore your surroundings for hidden resources.",
                                   kind=ActionKind.ENGINE), ExploreHandler())
    registry.register(ActionSchema("speak",       "Speak to a nearby agent.",
                                   {"message": str},
                                   kind=ActionKind.ENGINE), SpeakHandler())
    registry.register(ActionSchema("trade",       "Trade items with a nearby agent.",
                                   {"offer": {"item": "qty"}, "request": {"item": "qty"}},
                                   kind=ActionKind.ENGINE), TradeHandler())
    registry.register(ActionSchema("give",        "Give items to a nearby agent.",
                                   {"item": str, "qty": int},
                                   kind=ActionKind.ENGINE), GiveHandler())
    registry.register(ActionSchema("attack",      "Attack a nearby agent.",
                                   {"weapon": str},
                                   kind=ActionKind.ENGINE), AttackHandler())
    registry.register(ActionSchema("idle",        "Do nothing this tick.",
                                   kind=ActionKind.ENGINE), IdleHandler())
    return registry