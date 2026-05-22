"""
examples/medieval/sim/registry.py
───────────────────────────────────
Builds the ActionRegistry for the medieval simulation.
Called by Simulation.from_config() or directly in main.py.
"""
from src.engine.registry import ActionRegistry
from src.contracts.action import ActionSchema
from examples.medieval.sim.handlers import (
    MoveHandler, GatherFoodHandler, GatherWoodHandler, RestHandler,
    ExploreHandler, SpeakHandler, TradeHandler, GiveHandler,
    AttackHandler, IdleHandler,
)


def build_medieval_registry() -> ActionRegistry:
    registry = ActionRegistry()
    registry.register(ActionSchema("move",        "Move in a direction.",
                                   {"direction": "north|south|east|west"}), MoveHandler())
    registry.register(ActionSchema("gather_food", "Collect food from your current location."), GatherFoodHandler())
    registry.register(ActionSchema("gather_wood", "Collect wood from your current location."), GatherWoodHandler())
    registry.register(ActionSchema("rest",        "Rest to recover energy."), RestHandler())
    registry.register(ActionSchema("explore",     "Explore your surroundings for hidden resources."), ExploreHandler())
    registry.register(ActionSchema("speak",       "Speak to a nearby agent.", {"message": "string"}), SpeakHandler())
    registry.register(ActionSchema("trade",       "Trade items with a nearby agent.",
                                   {"offer": {"item": "qty"}, "request": {"item": "qty"}}), TradeHandler())
    registry.register(ActionSchema("give",        "Give items to a nearby agent.",
                                   {"item": "string", "qty": "int"}), GiveHandler())
    registry.register(ActionSchema("attack",      "Attack a nearby agent.", {"weapon": "string"}), AttackHandler())
    registry.register(ActionSchema("idle",        "Do nothing this tick."), IdleHandler())
    return registry