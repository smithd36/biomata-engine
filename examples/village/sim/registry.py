"""
examples/village/sim/registry.py
──────────────────────────────────
ActionRegistry for the village simulation.

Actions by kind and capability:
  HOST (universal)    — navigate, speak, interact
  ENGINE (universal)  — idle
  HYBRID (social)     — socialize  (agents with 'social' capability only)

Social flow:
  SocializeHandler emits a side_effect instead of directly mutating state.
  The engine routes this via SocialEffectSubscriber → VillageRelationships.update().
"""
from __future__ import annotations

from src.contracts.action import ActionKind, ActionSchema
from src.engine.registry import ActionRegistry
from examples.village.sim.handlers import (
    IdleHandler,
    InteractHandler,
    NavigateHandler,
    SpeakHandler,
    SocializeHandler,
)
from examples.village.sim.social import get_inbox


def build_village_registry(**kwargs) -> ActionRegistry:
    r     = ActionRegistry()
    inbox = get_inbox()

    # ── HOST: universal ────────────────────────────────────────────────────────

    r.register(
        ActionSchema(
            "navigate",
            "Move to a world-space XZ position.",
            {"target_x": float, "target_z": float, "target_y": "float?"},
            kind    = ActionKind.HOST,
            examples = [{"action": "navigate", "parameters": {"target_x": 0.0, "target_z": 0.0}}],
        ),
        NavigateHandler(),
    )

    r.register(
        ActionSchema(
            "interact",
            "Interact with a location or object.",
            {"location": str},
            kind    = ActionKind.HOST,
            examples = [{"action": "interact", "parameters": {"location": "Market"}}],
        ),
        InteractHandler(),
    )

    r.register(
        ActionSchema(
            "speak",
            "Say something aloud (short phrase, under 12 words). "
            "Optionally direct speech at a nearby agent using their id.",
            {"message": str, "target_id": "str? (optional: id of agent you are speaking to)"},
            kind     = ActionKind.HOST,
            examples = [
                {"action": "speak", "parameters": {"message": "Fresh goods today!"}},
                {"action": "speak", "parameters": {"message": "Good morning!", "target_id": "villager_001"}},
            ],
        ),
        SpeakHandler(inbox=inbox),
    )

    # ── ENGINE: universal ──────────────────────────────────────────────────────

    r.register(
        ActionSchema(
            "idle",
            "Stand still and wait one tick.",
            kind = ActionKind.ENGINE,
        ),
        IdleHandler(),
    )

    # ── HYBRID: social agents only ─────────────────────────────────────────────

    r.register(
        ActionSchema(
            "socialize",
            "Engage in a brief social exchange with a nearby agent. "
            "Use their id from nearby_agents in target_id.",
            {"target_id": str, "message": str},
            kind    = ActionKind.HYBRID,
            tags    = frozenset({"social"}),
            examples = [{
                "action":     "socialize",
                "parameters": {"target_id": "merchant_001", "message": "Fine day for trade!"},
            }],
        ),
        SocializeHandler(inbox=inbox),
    )

    return r
