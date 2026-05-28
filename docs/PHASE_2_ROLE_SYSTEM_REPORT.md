# Phase 2 — Role System Report

> **Refactor goal**: Make "role" a first-class simulation concept — a named bundle of capabilities, brain defaults, and advisory observation hints — instead of an opaque observation-dict string.

---

## What was delivered

### New files

| File | Purpose |
|---|---|
| `src/config/roles.py` | Role expansion helpers: `resolve_brain_class`, `expand_capabilities`, `expand_brain_config`, `export_roles_json` |
| `unity_sdk/Runtime/Resources/BiomataRoles.json` | Generated roles sidecar — Unity reads this |
| `unity_sdk/Runtime/Integration/Simulation/RoleManifestLoader.cs` | Unity runtime loader for `BiomataRoles.json` |
| `unity_sdk/Editor/RoleManifestValidator.cs` | **Biomata > Validate Roles** menu item |
| `docs/architecture/roles.md` | Full operational documentation |
| `PHASE_2_ROLE_SYSTEM_REPORT.md` | This file |

### Modified files

| File | Change |
|---|---|
| `src/config/schema.py` | Added `BrainRoleConfig`, `RoleConfig`; made `AgentConfig.brain` optional; added `AgentConfig.role`, `AgentConfig.metadata`; added `SimConfig.roles` |
| `src/config/loader.py` | Role expansion in agent construction: capability union, brain fallback, role name in metadata |
| `unity_sdk/Runtime/Models/AgentRegistration.cs` | Added `Capabilities` field |
| `unity_sdk/Runtime/Transport/WebSocketTransport.cs` | Serializes `Capabilities` in `RegisterAgentAsync` |
| `unity_sdk/Runtime/Integration/Agents/UnityAgentBridge.cs` | Added `_capabilities` field, `capabilities` param to `Configure()`, capabilities in `RegisterCoroutine()` |
| `unity_sdk/Runtime/Integration/Agents/BiomataAgent.cs` | Role expansion in `Awake()` + `OnValidate()` + `RoleForValidation` accessor |
| `examples/engine_owned/sim.yaml` | Updated to demonstrate role usage with all agents declared via `role:` |

---

## What was NOT changed

- `ActionRegistry`, `AgentRuntime`, `Simulation`, `EventBus` — no tick-path changes
- `Agent` Python dataclass — no new fields; role name stored in existing `Agent.metadata`
- WebSocket protocol wire format — `capabilities` was already in the `register_agent` spec; now Unity actually sends it
- The `role` observation injection in `BiomataAgent.Awake()` — still works exactly as before
- `BindToExisting` mode — unchanged; role expansion only applies to `CreateAtRuntime`

---

## Bug fixed: capabilities not sent in CreateAtRuntime

**Before Phase 2**: `BiomataAgent.capabilities` was injected into the observation dict but never included in the `AgentRegistration` payload sent to the backend. Unity-registered agents always had `capabilities=frozenset()`, making all capability-gated actions inaccessible.

**After Phase 2**: `AgentRegistration.Capabilities` is populated (from role expansion or the Inspector field) and serialized as `"capabilities": [...]` in the `register_agent` RPC.

This was a silent correctness bug. The phase 2 role system is what surfaced it.

---

## How to use in a new project

### 1. Declare roles in sim.yaml

```yaml
roles:
  Guard:
    capabilities: [guard, patrol, authority]
    brain:
      provider: ollama
      model: llama3
    observations:
      - position
      - nearby_agents

  Villager:
    capabilities: [social]
    brain:
      provider: ollama
      model: llama3

agents:
  - id: guard_001
    name: Aldric
    role: Guard          # inherits capabilities + brain from Guard

  - id: villager_01
    name: Mira
    role: Villager
```

### 2. Override per-agent when needed

```yaml
agents:
  - id: captain_001
    name: Hector
    role: Guard
    capabilities: [commander]     # union: {guard, patrol, authority, commander}

  - id: special_001
    name: Elena
    role: Merchant
    brain:                        # explicit brain overrides role brain
      class: myproject.brain.EliteBrain
      aggression: 0.9
```

### 3. Export roles JSON for Unity

```sh
python -c "
from src.config.schema import SimConfig
from src.config.roles import export_roles_json
import yaml
cfg = SimConfig.model_validate(yaml.safe_load(open('sim.yaml')))
export_roles_json(cfg.roles, 'Assets/Resources/BiomataRoles.json')
"
```

### 4. Configure Unity BiomataAgent

Set `role = "Guard"` in the Inspector. In `CreateAtRuntime` mode, capabilities and brain class are auto-populated from the manifest. Run **Biomata > Validate Roles** to confirm all role names are recognised.

---

## Migration guide for existing projects

### Python sim.yaml

No migration required. Agents that already have explicit `brain:` and `capabilities:` continue to work. The `roles:` block is optional.

To adopt roles incrementally: add a `roles:` block, then replace explicit `capabilities:` and `brain:` on individual agents with `role:` references.

### Unity BiomataAgent

No migration required. The `role` field continues to inject into observations as before.

To benefit from auto-population of capabilities: ensure `BiomataRoles.json` is in a Resources folder and the `role` field matches a declared role name. Nothing breaks if `BiomataRoles.json` is absent — `RoleManifestLoader.Load()` returns null and role expansion is skipped gracefully.

### `AgentRegistration` manual construction

If your project manually constructs `AgentRegistration` without using `BiomataAgent`, add:
```csharp
Capabilities = new[] { "guard", "patrol" },
```
This previously compiled without the field; it will now work if you want capabilities to reach the backend.

---

## Design decisions

**Why `roles:` in sim.yaml, not a separate roles.yaml?**  
Roles are simulation-specific configuration, not a shared library. They belong alongside the agents that reference them. A separate file would require a second config path with no benefit for the single-sim case.

**Why union of capabilities instead of role-wins or agent-wins?**  
An agent may need specialisation beyond its role (e.g. `role: Guard` plus `capabilities: [commander]`). Union is the only merge strategy that lets both express meaningful facts. Role-wins would make per-agent specialisation impossible; agent-wins would make roles meaningless.

**Why not role inheritance (`extends: BaseRole`)?**  
Three roles with 80% overlap is three explicit role declarations. That's readable, debuggable, and has no hidden dependency chain. A role tree adds: a traversal algorithm, a merge order question, and a new failure mode (circular extends). Ruled out per the "no overengineering" constraint.

**Why store role name in `agent.metadata` rather than a new field on `Agent`?**  
`Agent.metadata` is an existing `dict[str, Any]` that already flows through snapshots, events, and the brain context. Adding a dedicated `role: str` field to `Agent` would be additive API surface for what is fundamentally an optional label. `metadata["role"]` is sufficient and requires no protocol changes.

**Why does `brain_provider` not resolve to a C# class in Unity?**  
`provider: ollama` is a Python shorthand. Unity doesn't have a Python import system. The `brain_class` field carries the actual dotted class path that Unity passes to the backend during registration. If a role only declares a provider without a class, the export includes `brain_class: null` and Unity cannot auto-populate it — which is the correct, honest behaviour.

---

## Known limitations and follow-on work

- **No role hot-swap**: Roles are expanded at construction. To change an agent's role, unregister and re-register it with the new role's configuration.
- **`observations:` advisory only**: The list documents expected observation providers but is not enforced by Python. Future work could use it to auto-register matching ObservationProviders or validate coverage.
- **`brain_provider` gap in Unity**: If a role specifies only `provider:` without `class:`, Unity cannot auto-populate `brainClass`. Workaround: always include both, or use `class:` exclusively.
- **No roles export CLI**: The export is currently a Python one-liner. A `biomata roles export` command would be the natural follow-on.
