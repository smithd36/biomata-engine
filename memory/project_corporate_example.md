---
name: project-corporate-example
description: Corporate simulation added to prove framework generality — org graph world, no spatial grid.
metadata:
  type: project
---

Corporate example lives at `examples/corporate/`. Proves the framework is domain-agnostic:
- **World**: `CorporateWorld` — org graph (networkx DiGraph), departments, roles, budgets. No x/y coords. `are_adjacent` = same department OR 1-hop in hierarchy.
- **State**: `EmployeeVitals` — stress/influence/reputation. Tick: stress+4, influence-1.
- **Handlers**: Pure — no direct world mutation. 9 actions: email, schedule_meeting, request_budget, gossip, form_alliance, sabotage, delegate, pitch_idea, idle.
- **Budget** stored in `agent.inventory["budget"]`, set in `register_agents()` by role.
- Cross-agent state mutations use `target_state_mutations` key in `state_mutations` dict, handled by `CorporateWorld.apply()`.

**Why:** Also demonstrates the CORRECT pure-handler pattern (unlike medieval handlers which still mutate world directly — that's a known open violation).
