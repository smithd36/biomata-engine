"""
examples/corporate/sim/handlers.py
────────────────────────────────────
ActionHandlers for the corporate simulation.

PURE handler pattern: handlers never mutate the world directly.
All effects are expressed as ActionResult.state_mutations and side_effects.
CorporateWorld.apply() processes the cross-agent mutations.

Mutation keys used:
  state_mutations:
    influence_delta       → applied to acting agent's EmployeeVitals
    stress_delta          → applied to acting agent's EmployeeVitals
    reputation_delta      → applied to acting agent's EmployeeVitals
    inventory             → {"budget": delta}  applied by engine
    target_id             → which agent to affect
    target_inventory      → {"budget": delta}  applied by world.apply()
    target_state_mutations → {stress_delta, reputation_delta, ...} applied by world.apply()
    event                 → logged to CorporateWorld._events

  side_effects:
    {"type": "social", "from": ..., "to": ..., "delta": float}
    → consumed by SocialEffectSubscriber → social.update()
"""
from __future__ import annotations

from typing import Any

from src.contracts.action import Intent, ActionResult
from src.contracts.world import AgentView, WorldContext as OrgContext, SpatialWorld


def _social(from_id: str, to_id: str, delta: float) -> dict:
    return {"type": "social", "from": from_id, "to": to_id, "delta": delta}


def _get_agent(context: OrgContext, target_id: str | None) -> AgentView | None:
    if not target_id:
        return None
    return context.get_agent(target_id)


def _are_adjacent(context: OrgContext, id1: str, id2: str) -> bool:
    """Check adjacency if the world supports it; defaults to True when absent."""
    if isinstance(context, SpatialWorld):
        return context.are_adjacent(id1, id2)
    return True


# ── Email ─────────────────────────────────────────────────────────────────────

class EmailHandler:
    """Send a professional email to anyone in the org. No adjacency required."""

    def execute(self, agent: AgentView, intent: Intent, context: OrgContext) -> ActionResult:
        target = _get_agent(context, intent.target)
        msg    = str(intent.parameters.get("message", ""))[:120] or "(no message)"

        if not target:
            return ActionResult(
                success=True,
                outcome_text=f"sent email to org: {msg}",
                state_mutations={"influence_delta": +1},
            )

        tone  = intent.parameters.get("tone", "professional").lower()
        delta = +0.03 if "friendly" in tone or "thank" in msg.lower() else +0.01

        return ActionResult(
            success=True,
            outcome_text=f"emailed {target.name}: '{msg[:60]}'",
            state_mutations={
                "influence_delta": +2,
                "event": {"type": "email", "actor": agent.name, "to": target.name},
            },
            side_effects=[_social(agent.id, target.id, delta)],
        )


# ── Schedule meeting ──────────────────────────────────────────────────────────

class ScheduleMeetingHandler:
    """Organise a meeting with adjacent colleagues. Builds relationships."""

    def execute(self, agent: AgentView, intent: Intent, context: OrgContext) -> ActionResult:
        target = _get_agent(context, intent.target)

        if not target:
            return ActionResult(False, "schedule_meeting failed: target not found")
        if not _are_adjacent(context, agent.id, target.id):
            return ActionResult(False, f"schedule_meeting: {target.name} is in a different part of the org")

        topic = str(intent.parameters.get("topic", "general sync"))[:80]

        return ActionResult(
            success=True,
            outcome_text=f"held meeting with {target.name} — topic: {topic}",
            state_mutations={
                "influence_delta": +3,
                "stress_delta":    -5,   # meetings reduce isolation stress
                "event": {
                    "type":      "meeting",
                    "organizer": agent.name,
                    "target":    target.name,
                    "topic":     topic,
                },
            },
            side_effects=[
                _social(agent.id, target.id, +0.05),
                _social(target.id, agent.id, +0.03),
            ],
        )


# ── Request budget ────────────────────────────────────────────────────────────

class RequestBudgetHandler:
    """Request budget allocation from your manager. Transfers budget if manager has it."""

    def execute(self, agent: AgentView, intent: Intent, context: OrgContext) -> ActionResult:
        world_data = context.get_world_data()
        manager_id = world_data["_manager_of"].get(agent.id)

        if not manager_id:
            return ActionResult(False, "request_budget failed: you have no manager to ask")

        manager = _get_agent(context, manager_id)
        if not manager:
            return ActionResult(False, "request_budget failed: manager not found")

        manager_budget = manager.inventory.get("budget", 0)
        requested      = int(intent.parameters.get("amount", 50))
        granted        = min(requested, manager_budget)

        if granted <= 0:
            return ActionResult(
                False,
                f"request_budget: {manager.name} has no available budget ($k{manager_budget})",
                state_mutations={"stress_delta": +8},
            )

        purpose = str(intent.parameters.get("purpose", "project"))[:60]

        return ActionResult(
            success=True,
            outcome_text=f"secured ${granted}k from {manager.name} for {purpose}",
            state_mutations={
                "inventory":       {"budget": granted},    # engine adds to agent
                "influence_delta": +5,
                "target_id":       manager_id,
                "target_inventory": {"budget": -granted},  # world deducts from manager
                "event": {
                    "type":    "budget_approved",
                    "actor":   agent.name,
                    "manager": manager.name,
                    "amount":  granted,
                },
            },
            side_effects=[_social(agent.id, manager_id, +0.04)],
        )


# ── Gossip ────────────────────────────────────────────────────────────────────

class GossipHandler:
    """Spread rumours about a colleague. Damages their reputation and your relationship."""

    def execute(self, agent: AgentView, intent: Intent, context: OrgContext) -> ActionResult:
        target = _get_agent(context, intent.target)

        if not target:
            return ActionResult(False, "gossip failed: target not found")

        # Gossip only works in-person (adjacent)
        if not _are_adjacent(context, agent.id, target.id):
            return ActionResult(False, f"gossip: {target.name} is not nearby")

        rumour = str(intent.parameters.get("message", "spread rumours"))[:80]
        # More influential gossips hit harder
        influence = agent.ext.get("influence", 20)
        rep_dmg   = min(20, 8 + influence // 10)

        return ActionResult(
            success=True,
            outcome_text=f"gossiped about {target.name}: '{rumour[:50]}'",
            state_mutations={
                "influence_delta": +2,
                "target_id":       target.id,
                "target_state_mutations": {
                    "reputation_delta": -rep_dmg,
                    "stress_delta":     +6,
                },
                "event": {
                    "type":  "gossip",
                    "actor": agent.name,
                    "about": target.name,
                },
            },
            side_effects=[
                _social(agent.id, target.id, -0.08),
                _social(target.id, agent.id, -0.06),
            ],
        )


# ── Form alliance ─────────────────────────────────────────────────────────────

class FormAllianceHandler:
    """Propose a formal political alliance. Strong mutual positive relationship."""

    def execute(self, agent: AgentView, intent: Intent, context: OrgContext) -> ActionResult:
        target = _get_agent(context, intent.target)

        if not target:
            return ActionResult(False, "form_alliance failed: target not found")

        terms = str(intent.parameters.get("terms", "mutual support"))[:80]

        return ActionResult(
            success=True,
            outcome_text=f"formed alliance with {target.name} — terms: {terms}",
            state_mutations={
                "influence_delta":  +8,
                "reputation_delta": +4,
                "stress_delta":     -6,
                "event": {
                    "type":   "alliance",
                    "actor":  agent.name,
                    "target": target.name,
                    "terms":  terms,
                },
            },
            side_effects=[
                _social(agent.id, target.id, +0.20),
                _social(target.id, agent.id, +0.15),
            ],
        )


# ── Sabotage ──────────────────────────────────────────────────────────────────

class SabotageHandler:
    """Undermine a colleague. Damages their reputation and stress. High relationship cost."""

    def execute(self, agent: AgentView, intent: Intent, context: OrgContext) -> ActionResult:
        target = _get_agent(context, intent.target)

        if not target:
            return ActionResult(False, "sabotage failed: target not found")

        method = str(intent.parameters.get("method", "subtle undermining"))[:60]
        # Higher own reputation = more effective sabotage
        own_rep = agent.ext.get("reputation", 50)
        rep_dmg = min(25, 12 + own_rep // 10)

        return ActionResult(
            success=True,
            outcome_text=f"sabotaged {target.name} via {method}",
            state_mutations={
                "reputation_delta": +3,   # acting agent gains a little
                "stress_delta":     +10,  # but takes on stress/guilt
                "target_id":        target.id,
                "target_state_mutations": {
                    "reputation_delta": -rep_dmg,
                    "stress_delta":     +15,
                },
                "event": {
                    "type":   "sabotage",
                    "actor":  agent.name,
                    "target": target.name,
                },
            },
            side_effects=[
                _social(agent.id, target.id, -0.20),
                _social(target.id, agent.id, -0.15),
            ],
        )


# ── Delegate ──────────────────────────────────────────────────────────────────

class DelegateHandler:
    """Delegate a task to a direct report. Reduces your stress; increases theirs."""

    def execute(self, agent: AgentView, intent: Intent, context: OrgContext) -> ActionResult:
        world_data = context.get_world_data()
        reports    = world_data["_reports_of"].get(agent.id, [])

        target = _get_agent(context, intent.target)
        if not target:
            return ActionResult(False, "delegate failed: target not found")
        if target.id not in reports:
            return ActionResult(
                False,
                f"delegate failed: {target.name} does not report to you",
            )

        task = str(intent.parameters.get("task", "general task"))[:80]

        return ActionResult(
            success=True,
            outcome_text=f"delegated '{task}' to {target.name}",
            state_mutations={
                "stress_delta":    -12,
                "influence_delta": +4,
                "target_id":       target.id,
                "target_state_mutations": {
                    "stress_delta":     +10,
                    "influence_delta":  +2,   # being trusted builds influence
                },
                "event": {
                    "type":  "delegation",
                    "from":  agent.name,
                    "to":    target.name,
                    "task":  task,
                },
            },
            side_effects=[
                _social(agent.id, target.id, +0.03),
            ],
        )


# ── Pitch idea ────────────────────────────────────────────────────────────────

class PitchIdeaHandler:
    """Pitch a project idea to a manager or executive. Influence gain if well-received."""

    def execute(self, agent: AgentView, intent: Intent, context: OrgContext) -> ActionResult:
        world_data = context.get_world_data()
        target     = _get_agent(context, intent.target)

        if not target:
            return ActionResult(False, "pitch_idea failed: target not found")

        target_role = world_data["_roles"].get(target.id, "employee")
        if target_role not in ("manager", "executive"):
            return ActionResult(
                False,
                f"pitch_idea: {target.name} is a {target_role}, not a decision-maker",
            )

        idea = str(intent.parameters.get("idea", "new initiative"))[:80]
        # Receptiveness based on target's current stress (stressed execs say no)
        target_stress = target.ext.get("stress", 50)
        receptive     = target_stress < 70

        if receptive:
            return ActionResult(
                success=True,
                outcome_text=f"pitched '{idea}' to {target.name} — well received",
                state_mutations={
                    "influence_delta":  +10,
                    "reputation_delta": +6,
                    "stress_delta":     -3,
                    "event": {
                        "type":     "pitch",
                        "actor":    agent.name,
                        "audience": target.name,
                        "idea":     idea,
                        "outcome":  "approved",
                    },
                },
                side_effects=[
                    _social(agent.id, target.id, +0.07),
                    _social(target.id, agent.id, +0.04),
                ],
            )
        else:
            return ActionResult(
                success=False,
                outcome_text=f"pitched '{idea}' to {target.name} — rejected (too stressed)",
                state_mutations={
                    "stress_delta":     +5,
                    "influence_delta":  -2,
                    "event": {
                        "type":     "pitch",
                        "actor":    agent.name,
                        "audience": target.name,
                        "idea":     idea,
                        "outcome":  "rejected",
                    },
                },
                side_effects=[_social(agent.id, target.id, -0.02)],
            )


# ── Idle ──────────────────────────────────────────────────────────────────────

class IdleHandler:
    """Do nothing. Reduces stress slightly."""

    def execute(self, agent: AgentView, intent: Intent, context: OrgContext) -> ActionResult:
        return ActionResult(
            success=True,
            outcome_text="took a breather",
            state_mutations={"stress_delta": -8},
        )
