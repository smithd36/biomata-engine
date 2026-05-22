"""
sim/handlers.py
───────────────
Concrete ActionHandlers for the bundled medieval simulation.

Each handler:
  - reads from AgentView and SimulationContext (never mutates them directly)
  - returns an ActionResult with:
      outcome_text      — for logs and memory
      state_mutations   — applied by the engine to state_ext and inventory
      side_effects      — social graph updates, emitted to SocialGraph by Simulation

Social graph updates are expressed as side_effects dicts, NOT done inside the
handler. The Simulation loop reads them and calls social.update(). This keeps
handlers free of any SocialGraph dependency.
"""
from __future__ import annotations

from typing import Any

from src.contracts.action import Intent, ActionResult
from src.contracts.world import AgentView, WorldContext as SimulationContext, SpatialWorld


# ── Helpers ───────────────────────────────────────────────────────────────────

def _social(from_id: str, to_id: str, delta: float) -> dict:
    return {"type": "social", "from": from_id, "to": to_id, "delta": delta}

def _get_agent(context: SimulationContext, target_id: str | None) -> AgentView | None:
    if not target_id:
        return None
    return context.get_agent(target_id)

def _are_adjacent(context: SimulationContext, id1: str, id2: str) -> bool:
    """Check adjacency if the world supports it; defaults to True when absent."""
    if isinstance(context, SpatialWorld):
        return context.are_adjacent(id1, id2)
    return True


# ── Move ──────────────────────────────────────────────────────────────────────

class MoveHandler:
    def execute(self, agent: AgentView, intent: Intent, context: SimulationContext) -> ActionResult:
        direction = str(intent.parameters.get("direction", "")).lower()
        world_data = context.get_world_data()
        grid = world_data.get("_grid")          # raw grid; see WorldState.get_world_data()
        if grid is None:
            return ActionResult(False, "move failed: no grid available")
        ok, msg = grid.move_agent(agent.id, direction)
        return ActionResult(
            success=ok,
            outcome_text=msg,
            state_mutations={"inventory": {}},  # no inventory change
        )


# ── Gather food ───────────────────────────────────────────────────────────────

class GatherFoodHandler:
    def execute(self, agent: AgentView, intent: Intent, context: SimulationContext) -> ActionResult:
        world_data = context.get_world_data()
        grid = world_data.get("_grid")
        if grid is None:
            return ActionResult(False, "gather_food failed: no grid")
        cell = grid.cell_for(agent.id)
        if not cell:
            return ActionResult(False, "no cell to gather from")
        amt = min(12, cell.local_food)
        cell.local_food -= amt
        return ActionResult(
            success=True,
            outcome_text=f"gathered {amt} food from {cell.name} (cell stock: {cell.local_food})",
            state_mutations={"inventory": {"food": amt}},
        )


# ── Gather wood ───────────────────────────────────────────────────────────────

class GatherWoodHandler:
    def execute(self, agent: AgentView, intent: Intent, context: SimulationContext) -> ActionResult:
        world_data = context.get_world_data()
        grid = world_data.get("_grid")
        if grid is None:
            return ActionResult(False, "gather_wood failed: no grid")
        cell = grid.cell_for(agent.id)
        if not cell:
            return ActionResult(False, "no cell to gather from")
        amt = min(8, cell.local_wood)
        cell.local_wood -= amt
        return ActionResult(
            success=True,
            outcome_text=f"gathered {amt} wood from {cell.name}",
            state_mutations={"inventory": {"wood": amt}},
        )


# ── Rest ──────────────────────────────────────────────────────────────────────

class RestHandler:
    def execute(self, agent: AgentView, intent: Intent, context: SimulationContext) -> ActionResult:
        world_data = context.get_world_data()
        grid = world_data.get("_grid")
        cell = grid.cell_for(agent.id) if grid else None
        bonus = 40 if (cell and cell.location_type in ("village", "market")) else 25
        loc = cell.name if cell else "unknown"
        return ActionResult(
            success=True,
            outcome_text=f"rested at {loc} (+{bonus} energy)",
            state_mutations={"energy_delta": bonus},
        )


# ── Explore ───────────────────────────────────────────────────────────────────

class ExploreHandler:
    def execute(self, agent: AgentView, intent: Intent, context: SimulationContext) -> ActionResult:
        world_data = context.get_world_data()
        grid = world_data.get("_grid")
        cell = grid.cell_for(agent.id) if grid else None
        if cell:
            finds = []
            if cell.local_food > 5:
                bonus = context.rng.randint(2, 6)
                cell.local_food += bonus
                finds.append(f"+{bonus} food discovered")
            outcome = f"explored {cell.name}: " + (", ".join(finds) or "nothing notable found")
        else:
            outcome = "explored the area"
        return ActionResult(success=True, outcome_text=outcome)


# ── Speak ─────────────────────────────────────────────────────────────────────

class SpeakHandler:
    def execute(self, agent: AgentView, intent: Intent, context: SimulationContext) -> ActionResult:
        target = _get_agent(context, intent.target)
        msg = str(intent.parameters.get("message", ""))[:120]
        if target and _are_adjacent(context, agent.id, target.id):
            return ActionResult(
                success=True,
                outcome_text=f'said to {target.name}: "{msg}"',
                side_effects=[_social(agent.id, target.id, +0.02)],
            )
        elif target:
            return ActionResult(False, f"tried to speak to {target.name} but they are too far away")
        else:
            return ActionResult(True, f'said aloud: "{msg}"')


# ── Trade ─────────────────────────────────────────────────────────────────────

class TradeHandler:
    def execute(self, agent: AgentView, intent: Intent, context: SimulationContext) -> ActionResult:
        target = _get_agent(context, intent.target)
        if not target:
            return ActionResult(False, "trade failed: target not found")
        if not _are_adjacent(context, agent.id, target.id):
            return ActionResult(False, f"trade failed: {target.name} is too far away")

        offer   = intent.parameters.get("offer", {})
        request = intent.parameters.get("request", {})

        # Validate
        for item, qty in offer.items():
            if agent.inventory.get(item, 0) < qty:
                return ActionResult(False, f"trade failed: {agent.name} lacks {qty} {item}")
        for item, qty in request.items():
            if target.inventory.get(item, 0) < qty:
                return ActionResult(False, f"trade failed: {target.name} lacks {qty} {item}")

        # Build inventory mutations for both parties
        # (engine applies agent's; world.apply_result must handle target's)
        agent_inv = {item: -qty for item, qty in offer.items()}
        agent_inv.update({item: qty for item, qty in request.items()})

        offer_str   = ", ".join(f"{q} {i}" for i, q in offer.items())
        request_str = ", ".join(f"{q} {i}" for i, q in request.items())

        return ActionResult(
            success=True,
            outcome_text=f"traded {offer_str} to {target.name} for {request_str}",
            state_mutations={
                "inventory": agent_inv,
                "target_id": target.id,
                "target_inventory": {item: -qty for item, qty in request.items()} |
                                    {item: qty for item, qty in offer.items()},
            },
            side_effects=[
                _social(agent.id, target.id, +0.08),
                _social(target.id, agent.id, +0.04),
            ],
        )


# ── Give ──────────────────────────────────────────────────────────────────────

class GiveHandler:
    def execute(self, agent: AgentView, intent: Intent, context: SimulationContext) -> ActionResult:
        target = _get_agent(context, intent.target)
        item = str(intent.parameters.get("item", ""))
        qty  = int(intent.parameters.get("qty", 0))
        if not target:
            return ActionResult(False, "give failed: target not found")
        if not _are_adjacent(context, agent.id, target.id):
            return ActionResult(False, f"give failed: {target.name} is too far away")
        if agent.inventory.get(item, 0) < qty:
            return ActionResult(False, f"give failed: not enough {item}")
        return ActionResult(
            success=True,
            outcome_text=f"gave {qty} {item} to {target.name}",
            state_mutations={
                "inventory": {item: -qty},
                "target_id": target.id,
                "target_inventory": {item: qty},
            },
            side_effects=[
                _social(agent.id, target.id, +0.06),
                _social(target.id, agent.id, +0.03),
            ],
        )


# ── Attack ────────────────────────────────────────────────────────────────────

class AttackHandler:
    def execute(self, agent: AgentView, intent: Intent, context: SimulationContext) -> ActionResult:
        target = _get_agent(context, intent.target)
        if not target:
            return ActionResult(False, "attack failed: target not found")
        if not _are_adjacent(context, agent.id, target.id):
            return ActionResult(False, f"attack failed: {target.name} is too far away")
        dmg = context.rng.randint(5, 20)
        return ActionResult(
            success=True,
            outcome_text=f"attacked {target.name} for {dmg} damage",
            state_mutations={
                "target_id": target.id,
                "target_health_delta": -dmg,
            },
            side_effects=[
                _social(agent.id, target.id, -0.25),
                _social(target.id, agent.id, -0.15),
            ],
        )


# ── Idle ──────────────────────────────────────────────────────────────────────

class IdleHandler:
    def execute(self, agent: AgentView, intent: Intent, context: SimulationContext) -> ActionResult:
        return ActionResult(success=True, outcome_text="idled")