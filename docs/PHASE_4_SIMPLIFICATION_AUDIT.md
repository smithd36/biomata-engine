# Phase 4 — Semantic Simplification Audit

> **Goal**: Identify duplicated concepts, misleading names, speculative extensibility, and dead abstraction across the action / capability / role / observation / command system.  
> **This document proposes changes. It does not implement them.**  
> See `docs/architecture/semantic_model.md` for the factual grounding.

---

## Executive summary

After three phases of consolidation work, the system's concepts are cleaner but still carry several layers of accumulated naming debt, two half-complete renames, one unused classification axis, and a category of documentation masquerading as structure. The items below are ordered by impact — how much confusion each creates per day of development — not by implementation effort.

None of these are emergencies. The system works correctly. These are friction costs that compound with every new contributor and every debugging session.

---

## Issue catalog

### Issue 1 — `ObservationSchema.tags` was not renamed alongside `ActionSchema.tags`

**Category**: Incomplete rename (naming inconsistency)  
**Impact**: High  
**Files**: `src/contracts/observation.py`, `src/engine/obs_registry.py`

`ActionSchema.required_capabilities` (renamed in Phase 1) and `ObservationSchema.tags` are the same concept: a `frozenset[str]` used in an identical set-intersection check. Every place the rule is documented once for actions, it needs a footnote for observations. The half-complete rename means contributors searching for "capabilities" don't find `ObservationSchema`, and contributors reading `ObservationSchema` don't know it's the same mechanism.

**Proposed change**: Rename `ObservationSchema.tags` → `ObservationSchema.required_capabilities`. Add `tags` as a backwards-compatible alias using the same `__post_init__` pattern used in Phase 1 for `ActionSchema`.

**Migration path**:
```python
# Before
ObservationSchema("nearby_agents", "...", tags=frozenset({"scout"}))

# After — both work during migration window
ObservationSchema("nearby_agents", "...", required_capabilities=frozenset({"scout"}))
ObservationSchema("nearby_agents", "...", tags=frozenset({"scout"}))  # deprecated but valid
```

Update `obs_registry.py` line 68 and line 116 from `schema.tags` to `schema.required_capabilities`.

**Tradeoff**: Pure rename. No behavioral change. The only cost is the migration period where both names work.

---

### Issue 2 — `ActionKind` has no runtime effect

**Category**: Misleading name (implies behavior that doesn't exist)  
**Impact**: High  
**Files**: `src/contracts/action.py`, `src/config/manifest.py`, builtin registries

`ActionKind.HOST/ENGINE/HYBRID` is presented as a behavioral classification. It is actually a prompt decoration: it appends `[host]` or `[engine]` to an action's description in the LLM system prompt. Nothing in the dispatch path (`ActionRegistry.dispatch`, `AgentRuntime.step`, `HostedWorld.apply`) reads `ActionKind`.

The names imply semantic constraints that are unenforced. A developer reading `ActionKind.ENGINE` reasonably concludes that `engine_commands` are suppressed for ENGINE actions. They are not.

**Proposed change**: Rename the enum and its values to reflect what it actually does:

```python
class ActionExecution(str, Enum):
    HOST   = "host"    # effects delivered via engine_commands to the host process
    ENGINE = "engine"  # effects applied by Python directly (mutations, social, etc.)
    HYBRID = "hybrid"  # both channels used
```

Or, if the distinction is genuinely just for LLM context:

```python
class ActionHint(str, Enum):
    """Hint to the LLM about where action effects are applied. Does not affect dispatch."""
    HOST   = "host"
    ENGINE = "engine"
    HYBRID = "hybrid"
```

The field name on `ActionSchema` would change from `kind` to `execution_hint` (or similar) to make the advisory nature explicit.

**Tradeoff**: Breaking change to `ActionSchema` field name and manifest YAML key. The rename is cosmetically uncomfortable but architecturally honest. If the team decides `ActionKind` *should* affect dispatch in the future, this rename makes the future intent clear; if it should stay advisory, the new name prevents incorrect assumptions.

**Alternative (lower cost)**: Keep the name but add a prominent docstring: `# Advisory only — no dispatch effect. Used exclusively for LLM prompt context.` This costs nothing but doesn't fully address the confusion.

---

### Issue 3 — `state_mutations` is stringly-typed with undocumented structure

**Category**: Fake abstraction (opaque interface with implicit conventions)  
**Impact**: High  
**Files**: `src/contracts/action.py`, `src/engine/agent_runtime.py`

`ActionResult.state_mutations: dict[str, Any]` has two implicit contracts that are enforced by convention, not by type:

1. The key `"inventory"` is a `dict[str, int]` of item deltas (`AgentRuntime` applies this)
2. All other keys are forwarded to `StateExtension.apply_mutations()` (caller-defined semantics)

A handler author reading `state_mutations: dict[str, Any]` has no indication what keys are meaningful. The inventory convention is documented in comments spread across `agent_runtime.py` but not in the `ActionResult` type itself.

**Proposed change** (minimal): Replace the bare `dict` with a structured type:

```python
@dataclass
class StateMutations:
    inventory: dict[str, int]       = field(default_factory=dict)  # +/- deltas, clamped at 0
    ext:       dict[str, Any]       = field(default_factory=dict)  # forwarded to StateExtension
```

`ActionResult.state_mutations` would become `ActionResult.mutations: StateMutations`. The engine's inventory-delta logic would read `result.mutations.inventory` instead of `result.state_mutations.get("inventory")`. `StateExtension.apply_mutations()` would receive `result.mutations.ext`.

**Migration path**: Keep `state_mutations: dict[str, Any]` as a deprecated alias that constructs a `StateMutations` if non-None. Give a one-release migration window.

**Tradeoff**: Breaks any existing handler that returns `state_mutations={"inventory": {...}, "stress": 5}`. The migration is mechanical (find-and-replace) but touches every handler in user code. The typed version prevents a class of runtime bugs (wrong key names, wrong types) that are currently silent.

---

### Issue 4 — `side_effects` contains a speculative type that no code produces or consumes

**Category**: Speculative extensibility (dead documentation)  
**Impact**: Medium  
**Files**: `src/contracts/action.py` (comment)

```python
# side_effect shapes:
#   {"type": "social",  "from": id, "to": id, "delta": float}
#   {"type": "event",   ...}   — future extensibility
```

The `"event"` type exists only in this comment. No handler produces it. No subscriber reads it. It adds noise to documentation without providing value. If someone implements an event-type side effect in the future, they will implement it — this comment doesn't help.

**Proposed change**: Remove the `{"type": "event", ...}` comment. If the event type is eventually needed, add it when it's implemented.

This is the simplest change in the document. One comment removed. Zero risk.

---

### Issue 5 — `ActionSchema.examples` renders only the first item

**Category**: Misleading type (implies more utility than exists)  
**Impact**: Low-medium  
**Files**: `src/contracts/action.py`

`ActionSchema.examples: list[dict]` suggests multiple examples are supported. `prompt_block()` silently uses only `examples[0]`. A developer who adds multiple examples expecting richer LLM context gets nothing from the additional entries.

**Proposed change A** (rename): Change the field to `example: dict | None = None`. One example, or none. Singular noun. Optional.

```python
example: dict | None = None

# In prompt_block():
if self.example:
    ex_json = json.dumps(self.example, separators=(",", ":"))
    lines.append(f"    example: {ex_json}")
```

**Proposed change B** (enforce): Keep `list[dict]` but render all examples. The LLM prompt grows but accuracy improves.

**Migration path for A**: `examples=[{...}]` → `example={...}`. Any code passing multiple examples would need to pick one; the rest would be removed. Mechanical migration.

**Tradeoff**: Change A reduces API surface and removes the false implication. Change B is additive but may increase prompt length meaningfully for agents with many examples. Change A is recommended.

---

### Issue 6 — `BrainRoleConfig` duplicates `ComponentConfig`

**Category**: Duplicated concept (two types for the same pattern)  
**Impact**: Medium  
**Files**: `src/config/schema.py`, `src/config/roles.py`

Both types represent "a dotted class path plus arbitrary kwargs." `ComponentConfig` adds a required `class:` field. `BrainRoleConfig` adds an optional `provider:` shorthand and makes `class:` optional.

The only genuine difference is `provider:`. If `ComponentConfig` accepted an optional `provider:` field that the loader resolved via `BRAIN_PROVIDERS`, `BrainRoleConfig` would not need to exist.

**Proposed change**:

```python
class ComponentConfig(BaseModel):
    model_config = ConfigDict(extra="allow", populate_by_name=True)
    class_:   str | None = Field(default=None, alias="class")
    provider: str | None = None   # shorthand: resolved to class path by loader

    def kwargs(self) -> dict[str, Any]:
        return {k: v for k, v in (self.model_extra or {}).items()}
```

The loader would resolve `provider:` → `class_` before construction, wherever `ComponentConfig` is used for brains. `BrainRoleConfig` would be removed.

**Scope of change**: All sites that use `ComponentConfig` for non-brain components would have an unused `provider` field. This is harmless. Alternatively, sub-class `ComponentConfig` for the brain-specific case without creating a parallel hierarchy.

**Tradeoff**: Slight widening of `ComponentConfig` to support a field only relevant to brains. The alternative — two parallel config hierarchies — is worse.

---

### Issue 7 — `AgentOwnershipMode` describes lifecycle control, not ownership

**Category**: Misleading name (cognitive model mismatch)  
**Impact**: Medium  
**Files**: `unity_sdk/Runtime/Integration/Agents/AgentOwnershipMode.cs`, `unity_sdk/Runtime/Integration/Agents/BiomataAgent.cs`

`AgentOwnershipMode.BindToExisting` and `AgentOwnershipMode.CreateAtRuntime` describe *who drives the agent's lifecycle* (registration and unregistration). Both modes result in the Python engine owning all agent state at runtime. The word "ownership" misleads: it implies one side has exclusive data rights, which is not the difference.

**Current semantics**:
- `BindToExisting`: agent declared in sim.yaml, exists before Unity connects. Unity provides the visual shell. No registration RPC ever sent.
- `CreateAtRuntime`: Unity registers the agent on connect and unregisters on destroy. Agent lifecycle tied to the GameObject lifecycle.

**Proposed rename**:

| Old | Proposed | Why |
|---|---|---|
| `BindToExisting` | `AttachToExisting` | "Attach" is what Unity does — it attaches a visual shell |
| `CreateAtRuntime` | `ManageLifecycle` | Unity manages the agent's create/destroy lifecycle |
| `AgentOwnershipMode` | `AgentLifecycleMode` | The enum name matches the actual concern |

**Alternative names**: `BackendManaged` / `ClientManaged` (emphasizes who manages). `Static` / `Dynamic` (emphasizes whether the agent roster is fixed). Either pair is clearer than the current names.

**Tradeoff**: Breaking change in Unity C# — every scene that serializes this enum value needs to be re-saved. Unity serializes enum by index (not by name) by default, so renaming values without changing indices is safe. The enum definition must use explicit int values to guarantee stability.

---

### Issue 8 — `role.observations` is documentation stored as structure

**Category**: Dead abstraction (looks operational, does nothing)  
**Impact**: Medium  
**Files**: `src/config/schema.py` (`RoleConfig.observations`), `src/config/roles.py` (`export_roles_json`), `sim.yaml` examples

`RoleConfig.observations: list[str]` contains strings like `["position", "nearby_agents", "patrol_waypoints"]`. Nothing in the system validates these strings against:

- `ObservationSchema.name` values in Python
- `ObservationProviderBase` class names in Unity
- Actual observation keys that appear in the brain's context

The strings are exported to `BiomataRoles.json` and are visible in the editor validator code but only as "advisory." They create the appearance of a semantic link between roles and observation providers that doesn't exist.

There are two honest paths:

**Option A — Remove it**: Delete `observations` from `RoleConfig`. Remove from YAML examples and JSON export. Three files. The concept doesn't exist yet; removing it eliminates the false implication.

**Option B — Make it real**: Validate `role.observations` strings against registered `ObservationSchema.name` values at `load_simulation()` time. Export them to `BiomataRoles.json`. Add editor-time validation in `BiomataAgentEditor` that checks whether `ObservationProviderBase` components on the GameObject cover the role's declared observations.

Option A is correct if this is truly advisory and will never be enforced. Option B is correct if there's a real intent to make observation profiles first-class.

**Recommendation**: Remove it. If observation profiles become a real feature, re-add it as part of that feature — not as aspirational scaffolding.

---

### Issue 9 — `HostedWorld` has an undescriptive name

**Category**: Misleading name (ambiguous host direction)  
**Impact**: Low-medium  
**Files**: `src/plugins/external/world.py`

"Hosted" is ambiguous: does the world host agents? Is it hosted by the game engine? Is it hosted on a server? The actual meaning is that the world's authoritative state is *provided by* (pushed from) an external host process — typically Unity. The Python engine does not compute world state; it receives it.

**Proposed rename**: `ExternalWorld` (consistent with the `ExternalWorld` protocol it implements) or `ClientDrivenWorld`. The protocol is already named `ExternalWorld`; having the concrete implementation called `HostedWorld` is a naming inconsistency.

**Migration path**: Rename the class. Update all `class: src.plugins.external.world.HostedWorld` references in YAML config files.

---

### Issue 10 — `ObservationProvider.observe` clashes with `World.observe`

**Category**: Misleading name (same verb, different semantics)  
**Impact**: Low-medium  
**Files**: `src/contracts/observation.py`

`World.observe(agent_id)` returns the complete world-state dict for an agent. This is authoritative.  
`ObservationProvider.observe(agent_id, capabilities, world)` returns a partial slice that supplements (and is overridden by) the world observation.

Same method name, different return shapes, different priorities, different consumers. A new contributor reading either one may assume the other works the same way.

**Proposed rename**: `ObservationProvider.observe` → `ObservationProvider.collect`. The `ObservationRegistry` already uses "collect" (`registry.collect()`), which matches — the registry collects slices from providers, and each provider contributes a slice.

**Migration path**: Rename the single protocol method. All provider implementations need to rename their method. Mechanical change.

---

### Issue 11 — Dual agent registration wire formats (`register_agent` / `agent.register`)

**Category**: Duplicated concept (two wire formats for one operation)  
**Impact**: Low-medium  
**Files**: `src/transport/websocket/handler.py`, `src/transport/websocket/protocol.py`

Two formats for the same operation exist due to versioning history. Both are supported. Neither is deprecated. The `register_agent` flat format and `agent.register` nested format require two handler branches in `ConnectionHandler` with identical semantics.

**Proposed change**: Officially deprecate `register_agent` in favour of `agent.register`. Add a deprecation log warning when `register_agent` is received. Remove `register_agent` in a future breaking version.

**Migration path**: Update SDK clients (`AgentClient.RegisterAsync`) to use `agent.register`. Document the deprecation in the protocol changelog.

---

### Issue 12 — `WorldContext` and `World` have an implicit relationship

**Category**: Dead abstraction (protocol boundary without enforcement)  
**Impact**: Low  
**Files**: `src/contracts/world.py`, `src/contracts/action.py`, `src/engine/registry.py`

`ActionHandler.execute(agent, intent, context: WorldContext)` declares that handlers receive a `WorldContext`. But `ActionRegistry.dispatch()` passes `self.world` directly — a `World` object. For this to work, every `World` implementation must also satisfy `WorldContext` (`rng`, `get_agent`, `get_world_data`). This is an implicit contract with no enforcement.

`HostedWorld` likely implements all three `WorldContext` methods. But nothing verifies this at instantiation time. A new `World` implementation that skips `get_agent` would compile successfully and fail silently when a handler tries to call it.

**Proposed change**: Make `World` extend `WorldContext` in the protocol definition, or add a runtime assertion in `AgentRuntime.__init__` that the provided world satisfies `WorldContext`.

---

### Issue 13 — `SimulationController` protocol has one implementation

**Category**: Speculative extensibility (one-implementation interface)  
**Files**: `src/service/interfaces.py`

`SimulationController` defines the same method signatures as `SimulationSession` with no additions. It exists to decouple `ConnectionHandler` from the concrete session class. In practice, nothing else implements this protocol.

This is a judgment call: the protocol costs ~30 lines and one file to navigate, and provides insurance against a second session type. If a second session type is never planned, it's dead abstraction. If session variants are planned (e.g., a `LocalSession` for headless research with no networking), the protocol is valuable.

**Recommendation**: Keep it, but document explicitly in the file: `# One implementation exists: SimulationSession. This protocol exists for testing isolation and future session variants.`

---

## Priority ranking

| Priority | Issue | Effort | Benefit |
|---|---|---|---|
| 1 | Rename `ObservationSchema.tags` → `required_capabilities` | Low (same pattern as Phase 1) | Removes the half-rename confusion |
| 2 | Remove `role.observations` or make it real | Low (delete 3 fields) | Eliminates fake structure |
| 3 | Clarify `ActionKind` is advisory (rename or docstring) | Medium | Prevents wrong mental model |
| 4 | Structured `StateMutations` type for `state_mutations` | High (all handlers change) | Prevents silent key bugs |
| 5 | Rename `AgentOwnershipMode` → `AgentLifecycleMode` | Medium (Unity re-save) | Corrects cognitive model |
| 6 | Merge `BrainRoleConfig` into `ComponentConfig` | Medium | One config type for one pattern |
| 7 | Rename `ObservationProvider.observe` → `collect` | Low | Removes verb collision |
| 8 | Remove `ActionSchema.examples` list / use singular | Low | Removes false implication |
| 9 | Rename `HostedWorld` → `ExternalWorld` | Low | Consistent with protocol name |
| 10 | Remove `{"type": "event"}` comment from side_effects | Trivial | Clean documentation |
| 11 | Deprecate `register_agent` flat format | Low | Removes dual-format debt |
| 12 | Rename `ActionSchema.example` → `execution_hint` | Medium | Intent clarity |
| 13 | Add runtime `WorldContext` enforcement | Low | Closes silent failure mode |

---

## What should NOT change

These items were reviewed and found sound:

**`AgentView` immutability** — the frozen snapshot passed to brains and handlers is the right boundary. Do not replace with a live reference.

**EventBus synchronous dispatch** — simple, predictable, debuggable. Async event buses exist; this one's synchronous by design and the tradeoff is correct.

**`ActionRegistry` pattern** — name-to-(schema, handler) map is clean. No changes needed.

**Snapshot protocol** — `Snapshotable.serialize() / restore()` is the right interface. The pickle backend is implementation debt but the protocol boundary is correct.

**`ObservationRegistry.collect()` try/except isolation** — providers that crash should not crash ticks. The pattern is right.

**`_call_factory` with VAR_KEYWORD detection** — the enhanced version from Phase 2 is correct. Don't revert to the partial-filtering version.

**`BiomataSimulationBootstrapper` design** — the config-asset + override flags pattern is the right approach for production Unity code. Complex but justified.

**Capability intersection semantics (OR, not AND)** — the current `a.capabilities ∩ s.required_capabilities ≠ ∅` semantics are correct for the general case. AND semantics would require agents to hold every single required capability, making broad capability sets awkward. Document the OR semantics clearly but don't change them.

---

## Migration impact summary

The items above can be grouped by migration scope:

**Python-only, backwards-compatible** (add aliases):
- Issue 1: `ObservationSchema.tags` → `required_capabilities`

**Python-only, breaking** (find-and-replace in user code):
- Issue 3: `state_mutations` → typed `StateMutations`
- Issue 7 (Python side): `BrainRoleConfig` folded into `ComponentConfig`
- Issue 9: `HostedWorld` class rename
- Issue 10: `ObservationProvider.observe` → `collect`

**YAML-breaking** (sim.yaml files need update):
- Issue 9: `class: src.plugins.external.world.HostedWorld` → new path

**Unity-breaking** (C# rename, scenes need re-save):
- Issue 5 (Unity): `AgentOwnershipMode` enum rename

**Documentation-only** (no code changes):
- Issue 4: Remove `{"type": "event"}` comment
- Issue 13: Add docstring to `SimulationController`

**Trivial cleanup**:
- Issue 4: Delete one comment line
- Issue 8: Remove `role.observations` from 3 files
- Issue 11: Deprecate `register_agent` method
