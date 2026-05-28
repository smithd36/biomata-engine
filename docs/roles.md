# Role System

> **Phase 2 of the Biomata architecture consolidation.**  
> Builds on the action manifest introduced in Phase 1.

---

## What a role is

A role is a named, flat bundle of defaults:

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
      - patrol_waypoints
```

An agent that declares `role: Guard` inherits those defaults. Nothing more. A role is not a class, not a type hierarchy, not a polymorphic entity. It is a name-indexed dictionary of defaults that the loader expands at construction time.

## What a role is NOT

- Not a base class or mixin. There is no role inheritance.
- Not a security boundary. Capabilities still gate action visibility; the role just populates those capabilities.
- Not authoritative over observation data. The `observations:` list is advisory — it documents what an agent in this role is expected to perceive, for human readers and Unity validation only.
- Not a replacement for explicit agent configuration. Any field set directly on the agent always overrides the role.

---

## Where roles live

Roles are declared in `sim.yaml` under the top-level `roles:` key, alongside `agents:`, `world:`, and `registry:`.

```yaml
roles:
  Guard:
    capabilities: [guard, patrol, authority]
    brain:
      provider: idle

agents:
  - id: guard_001
    name: Aldric
    role: Guard
```

There is no separate `roles.yaml` file. Roles are part of the simulation configuration, not a separate manifest.

---

## Field reference

```yaml
roles:
  RoleName:
    capabilities: [str, ...]          # Optional. Capability tags inherited by agents.
    brain:                            # Optional. Used when agent has no explicit brain:.
      provider: ollama | idle | replay  # Shorthand → Python class path
      # OR
      class: dotted.path.BrainClass    # Explicit class path
      any_kwarg: value                 # Extra kwargs forwarded to brain constructor
    observations: [str, ...]          # Optional, advisory. Documents expected providers.
```

### Brain config in a role

Two ways to specify the brain:

**Provider shorthand** (for built-in brains):
```yaml
brain:
  provider: ollama
  model: llama3          # forwarded as kwarg to OllamaLLMBrain
  temperature: 0.7
```

| Provider | Python class |
|---|---|
| `ollama` | `src.plugins.builtin.ollama.brain.OllamaLLMBrain` |
| `idle` | `src.plugins.builtin.idle_brain.brain.IdleBrain` |
| `replay` | `src.plugins.builtin.replay_brain.brain.ReplayBrain` |

**Explicit class path** (for custom brains):
```yaml
brain:
  class: myproject.brains.PatrolBrain
  patrol_speed: 1.5
```

Extra fields on the `brain:` block (beyond `provider` / `class`) are forwarded to the brain constructor as keyword arguments.

---

## Expansion rules

When the loader instantiates an agent with a role:

1. **Capabilities** — `agent.capabilities = frozenset(agent.capabilities) ∪ frozenset(role.capabilities)`. Both contribute; neither overrides the other.

2. **Brain** — Agent's explicit `brain:` block takes precedence. If the agent has no `brain:`, the role's brain is used. If neither has a brain, the loader raises a `ValueError` at startup.

3. **Observations** — Advisory only. Not processed by the Python loader. Stored in the role definition for Unity export.

4. **Role name in metadata** — `agent.metadata["role"] = role_name`. Downstream systems (LLM prompts, event logs, analytics) can inspect this without parsing the observation dict.

These rules are applied **at construction time**, not at tick time. There is no runtime role lookup.

### Priority matrix

| Source | Capabilities | Brain | Observations |
|---|---|---|---|
| Agent explicit | included (union) | **wins** | n/a |
| Role default | included (union) | fallback if agent has no brain | advisory only |
| Neither | `frozenset()` | `ValueError` at startup | empty |

---

## Agent metadata

After role expansion, `agent.metadata["role"]` is set to the role name string. This propagates through the existing metadata path (EventBus events, brain context, snapshots) without any additional changes.

It is distinct from the `role` key in observations (injected by Unity's `BiomataAgent` into the observation dict). The Python-side metadata is an engine-internal field; the observation-dict `role` is what the brain LLM prompt sees.

---

## Adding a new role

1. Add a `RoleName:` entry to the `roles:` block in `sim.yaml`
2. Assign agents `role: RoleName`
3. Regenerate `BiomataRoles.json` for Unity:

```sh
python -c "
from src.config.schema import SimConfig
from src.config.roles import export_roles_json
import yaml
cfg = SimConfig.model_validate(yaml.safe_load(open('sim.yaml')))
export_roles_json(cfg.roles, 'Assets/Resources/BiomataRoles.json')
"
```

4. Run **Biomata > Validate Roles** in the Unity editor to check that all `BiomataAgent` role fields match the manifest.

---

## Unity integration

### `BiomataAgent` role field

The `role` field on `BiomataAgent` has always existed as a string that gets injected into the observation dict. Phase 2 adds **role expansion in `Awake()`**:

In `CreateAtRuntime` mode, before the registration RPC is sent, `BiomataAgent` loads `BiomataRoles.json` and fills in any empty fields:

| BiomataAgent field | Behaviour when role set |
|---|---|
| `capabilities` | Auto-populated from role if empty |
| `brainClass` | Auto-populated from role `brain_class` if empty |
| `role` observation | Injected into observation dict as before (unchanged) |

Agent-level Inspector values always win. The role only fills in what is absent.

In `BindToExisting` mode, role expansion does not apply — the agent is pre-declared on the backend with its capabilities and brain already set via YAML.

### Capabilities now sent on registration

Before Phase 2, `capabilities` from Unity were injected into the observation dict only — they were never sent to the backend during `CreateAtRuntime` registration. This meant that capability-gated actions were inaccessible to runtime-registered agents.

`AgentRegistration.Capabilities` is now populated and serialized in the registration RPC:
```json
{
  "method": "register_agent",
  "params": {
    "agent_id": "guard_001",
    "capabilities": ["guard", "patrol", "authority"],
    ...
  }
}
```

### `BiomataRoles.json`

Generated from `sim.yaml` roles and committed alongside the Unity project. Unity reads it via `Resources.Load<TextAsset>("BiomataRoles")`. Format:

```json
{
  "version": "1",
  "roles": [
    {
      "name": "Guard",
      "capabilities": ["guard", "patrol", "authority"],
      "observations": ["position", "nearby_agents", "patrol_waypoints"],
      "brain_provider": "ollama",
      "brain_class": "src.plugins.builtin.ollama.brain.OllamaLLMBrain"
    }
  ]
}
```

`brain_provider` is included for documentation. Only `brain_class` is used by Unity for auto-populating `brainClass`.

### Editor validator

**Biomata > Validate Roles** scans all `BiomataAgent` components in open scenes and prefabs, checks their `role` field against `BiomataRoles.json`, and logs warnings for any unrecognised role names.

---

## Provider map extension

The built-in provider map is:

```python
from src.config.roles import BRAIN_PROVIDERS
# {"ollama": "src.plugins.builtin.ollama.brain.OllamaLLMBrain", ...}
```

Add custom providers at import time:
```python
from src.config.roles import BRAIN_PROVIDERS
BRAIN_PROVIDERS["my_provider"] = "myproject.brains.MyBrain"
```

Custom brains used in roles can also always use the `class:` field directly — the provider map is a convenience, not a requirement.

---

## Common mistakes

**Role not in manifest when Unity validates**: After adding a role to `sim.yaml`, regenerate `BiomataRoles.json`. The validator compares against the JSON file, not the YAML.

**Brain not expanding in `BindToExisting` mode**: Role expansion only applies in `CreateAtRuntime`. In `BindToExisting`, the backend agent already exists with its configured brain; Unity doesn't register it and so doesn't need to know the brain class.

**Capabilities not reaching the backend (pre-Phase 2 projects)**: If you have an existing project where Unity registers agents, verify that `AgentRegistration.Capabilities` is populated. This was fixed in Phase 2 — old code that builds `AgentRegistration` manually must be updated to set `Capabilities`.

**Additive capabilities not expected**: `agent.capabilities` and `role.capabilities` are unioned, not one-or-the-other. An agent with `capabilities: [commander]` and `role: Guard` (which has `[guard, patrol, authority]`) gets `{guard, patrol, authority, commander}`. This is intentional — the agent's explicit capabilities supplement the role; they don't replace it.

---

## Known limitations

- **No role inheritance or composition**: A role is a flat dict. If two roles share most of their capabilities, you must repeat them. Role composition (`extends: BaseGuard`) was explicitly rejected to keep the system simple.
- **`observations:` list is advisory only**: Python does not use it to select or filter observation providers. It documents intent. Automatic observation-provider registration driven by role was considered but deferred.
- **`brain_provider` not resolved by Unity**: Unity can only use `brain_class` (a string it can pass to the backend). `brain_provider` is a Python-side shorthand. If a role only specifies `brain: {provider: ollama}` without a `class:`, Unity cannot auto-populate `brainClass` and will log a warning.
- **No role hot-swap**: Changing an agent's role mid-simulation is not supported. Roles are expanded at agent construction time; the runtime `Agent` object has no reference to a `RoleConfig`.
