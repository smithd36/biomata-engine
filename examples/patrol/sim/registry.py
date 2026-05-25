"""
examples/patrol/sim/registry.py
"""
from src.contracts.action import ActionKind, ActionSchema
from src.engine.registry import ActionRegistry
from examples.patrol.sim.handlers import IdleHandler, NavigateHandler


def build_patrol_registry() -> ActionRegistry:
    r = ActionRegistry()
    r.register(
        ActionSchema(
            "navigate",
            "Move to a world-space XZ position.",
            {"target_x": float, "target_z": float, "target_y": "float?"},
            kind=ActionKind.HOST,
        ),
        NavigateHandler(),
    )
    r.register(
        ActionSchema("idle", "Stand still.", kind=ActionKind.ENGINE),
        IdleHandler(),
    )
    return r
