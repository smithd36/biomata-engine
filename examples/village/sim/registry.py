"""
examples/village/sim/registry.py
"""
from src.contracts.action import ActionKind, ActionSchema
from src.engine.registry import ActionRegistry
from examples.village.sim.handlers import (
    IdleHandler,
    InteractHandler,
    NavigateHandler,
    SpeakHandler,
)


def build_village_registry() -> ActionRegistry:
    r = ActionRegistry()
    r.register(
        ActionSchema(
            "navigate",
            "Move to a world-space XZ position.",
            {"target_x": "float", "target_z": "float", "target_y": "float (optional, default 0)"},
            kind=ActionKind.HOST,
            examples=[{"action": "navigate", "parameters": {"target_x": 0.0, "target_z": 0.0}}],
        ),
        NavigateHandler(),
    )
    r.register(ActionSchema("idle", "Stand still and wait.", kind=ActionKind.ENGINE), IdleHandler())
    r.register(
        ActionSchema(
            "interact",
            "Interact with a location or person.",
            {"location": "string — name of the place or person you are interacting with"},
            kind=ActionKind.HOST,
            examples=[{"action": "interact", "parameters": {"location": "Market"}}],
        ),
        InteractHandler(),
    )
    r.register(
        ActionSchema(
            "speak",
            "Say something aloud (short phrase, under 15 words).",
            {"message": "string — what you say"},
            kind=ActionKind.HOST,
            examples=[{"action": "speak", "parameters": {"message": "Fresh goods today!"}}],
        ),
        SpeakHandler(),
    )
    return r
