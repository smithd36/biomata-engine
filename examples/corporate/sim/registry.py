"""
examples/corporate/sim/registry.py
────────────────────────────────────
Builds the ActionRegistry for the corporate simulation.
"""
from src.engine.registry import ActionRegistry
from src.contracts.action import ActionKind, ActionSchema
from examples.corporate.sim.handlers import (
    EmailHandler,
    ScheduleMeetingHandler,
    RequestBudgetHandler,
    GossipHandler,
    FormAllianceHandler,
    SabotageHandler,
    DelegateHandler,
    PitchIdeaHandler,
    IdleHandler,
)


def build_corporate_registry() -> ActionRegistry:
    r = ActionRegistry()
    r.register(ActionSchema("email",
        "Send a professional email to a colleague (no adjacency needed).",
        {"message": str, "tone": "professional|friendly|urgent"},
        kind=ActionKind.ENGINE),
        EmailHandler())
    r.register(ActionSchema("schedule_meeting",
        "Hold a meeting with an adjacent colleague.",
        {"topic": str},
        kind=ActionKind.ENGINE),
        ScheduleMeetingHandler())
    r.register(ActionSchema("request_budget",
        "Ask your manager for budget allocation.",
        {"amount": int, "purpose": str},
        kind=ActionKind.ENGINE),
        RequestBudgetHandler())
    r.register(ActionSchema("gossip",
        "Spread rumours about a nearby colleague — damages their reputation.",
        {"message": str},
        kind=ActionKind.ENGINE),
        GossipHandler())
    r.register(ActionSchema("form_alliance",
        "Propose a formal political alliance with a colleague.",
        {"terms": str},
        kind=ActionKind.ENGINE),
        FormAllianceHandler())
    r.register(ActionSchema("sabotage",
        "Undermine a colleague's standing. High relationship cost.",
        {"method": str},
        kind=ActionKind.ENGINE),
        SabotageHandler())
    r.register(ActionSchema("delegate",
        "Delegate a task to a direct report (manager/executive only).",
        {"task": str},
        kind=ActionKind.ENGINE),
        DelegateHandler())
    r.register(ActionSchema("pitch_idea",
        "Pitch a project idea to a manager or executive.",
        {"idea": str},
        kind=ActionKind.ENGINE),
        PitchIdeaHandler())
    r.register(ActionSchema("idle",
        "Take a breather. Reduces stress slightly.",
        kind=ActionKind.ENGINE),
        IdleHandler())
    return r
