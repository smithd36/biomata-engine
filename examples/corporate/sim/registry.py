"""
examples/corporate/sim/registry.py
────────────────────────────────────
Builds the ActionRegistry for the corporate simulation.
"""
from src.engine.registry import ActionRegistry
from src.contracts.action import ActionSchema
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
        {"message": "string", "tone": "professional|friendly|urgent"}),
        EmailHandler())
    r.register(ActionSchema("schedule_meeting",
        "Hold a meeting with an adjacent colleague.",
        {"topic": "string"}),
        ScheduleMeetingHandler())
    r.register(ActionSchema("request_budget",
        "Ask your manager for budget allocation.",
        {"amount": "int ($k)", "purpose": "string"}),
        RequestBudgetHandler())
    r.register(ActionSchema("gossip",
        "Spread rumours about a nearby colleague — damages their reputation.",
        {"message": "string"}),
        GossipHandler())
    r.register(ActionSchema("form_alliance",
        "Propose a formal political alliance with a colleague.",
        {"terms": "string"}),
        FormAllianceHandler())
    r.register(ActionSchema("sabotage",
        "Undermine a colleague's standing. High relationship cost.",
        {"method": "string"}),
        SabotageHandler())
    r.register(ActionSchema("delegate",
        "Delegate a task to a direct report (manager/executive only).",
        {"task": "string"}),
        DelegateHandler())
    r.register(ActionSchema("pitch_idea",
        "Pitch a project idea to a manager or executive.",
        {"idea": "string"}),
        PitchIdeaHandler())
    r.register(ActionSchema("idle",
        "Take a breather. Reduces stress slightly."),
        IdleHandler())
    return r
