# Biomata Engine — Semantic Model

> **Purpose**: Define what each concept actually is and how it actually works.  
> Not aspirational. Grounded in the code as of Phase 3.

This document maps the five concept domains — Actions, Observations, Capabilities, Roles, Commands — their internal structure, their relationships to each other, and the places where the names diverge from the behaviour.

---

## The five domains

```
┌─────────────────────────────────────────────────────────────────────┐
│                         Tick Cycle                                  │
│                                                                     │
│  World         ──observe──►  Observation   ──► Brain.decide()      │
│  ObsRegistry   ──collect──►     dict        ──►   │                 │
│                                                    │                │
│                                               Intent                │
│                                                    │                │
│  ActionRegistry ◄──validate──────────────────────◄┘                │
│       │                                                             │
│       └──dispatch──► ActionHandler.execute()                        │
│                                │                                    │
│                         ActionResult                                │
│                         ├─ state_mutations  ──► Agent state         │
│                         ├─ side_effects     ──► SocialSystem (via   │
│                         │                       EventBus)           │
│                         └─ engine_commands  ──► Host (Unity)        │
└─────────────────────────────────────────────────────────────────────┘
```

---

## Domain 1: Actions

### What an action actually is

An action is a **named decision a brain can make**. It has two halves:

- **ActionSchema** — metadata: name, description, parameters spec, capability gate. Shown to the brain (LLM) to inform its decision. Never modified at runtime.
- **ActionHandler** — code: takes the decision and produces effects. Invisible to the brain.

The brain sees schemas. The engine calls handlers. The two halves are joined only by the action name string.

### ActionResult: three output channels

Every handler returns one `ActionResult` with three channels, each flowing to a different consumer:

| Channel | Field | Consumer | What it expresses |
|---|---|---|---|
| Agent state | `state_mutations: dict` | `AgentRuntime` directly | Inventory deltas; anything `StateExtension.apply_mutations()` accepts |
| Social/event effects | `side_effects: list[dict]` | `SocialEffectSubscriber` via EventBus | Social graph updates |
| Host execution | `engine_commands: list[dict]` | `HostedWorld` → `ConnectionHandler` → Unity | Arbitrary structured commands to the rendering host |

These channels are independent. A handler can populate any combination.

### What `ActionKind` actually does

`ActionKind` (HOST, ENGINE, HYBRID) has **zero effect on dispatch, validation, or mutation logic**. The engine never reads `schema.kind` or `result.kind` during a tick.

Its only runtime effect is a label appended to the action description in the LLM system prompt:

```python
# From ActionSchema.prompt_block()
kind_label = f"  [{self.kind.value}]" if self.kind != ActionKind.HYBRID else ""
lines      = [f"  {self.name}: {self.description}{kind_label}"]
```

`HYBRID` shows no label; `HOST` appends `[host]`; `ENGINE` appends `[engine]`. The label tells the LLM whether an action's effects are handled by Python, by Unity, or both. It does not change what Python or Unity actually does.

**Consequence**: calling a handler `ActionKind.ENGINE` doesn't prevent it from returning `engine_commands`. Calling it `ActionKind.HOST` doesn't prevent it from mutating `state_mutations`. The classification is advisory documentation for the LLM prompt, not a behavioral constraint.

### Parameter validation: what it catches and what it doesn't

`ActionSchema.validate_parameters()` validates only parameters whose spec resolves to a known type token (`str`, `int`, `float`, `bool`, or a string starting with one of those tokens). Descriptive specs like `"north|south|east|west"` or nested dicts are silently skipped. The validation is a partial type-check, not a full schema validation.

### `ActionSchema.examples`: what it produces

Only `examples[0]` is ever rendered (in `prompt_block()`). Additional examples are silently ignored. The field is a `list` for a future that hasn't arrived.

---

## Domain 2: Observations

There are **two separate observation systems** in Biomata. They are architecturally disconnected and are joined only in the final observation dict assembled by `AgentRuntime._build_observation()`.

### System 1: Python `ObservationRegistry` + `ObservationProvider`

Lives in `src/engine/obs_registry.py` and `src/contracts/observation.py`.

- Registered by user code in Python
- Filtered by `ObservationSchema.tags` (agent capability gate)
- Called each tick via `registry.collect(agent_id, capabilities, world)`
- Returns a `dict[str, Any]` slice

### System 2: Unity `ObservationCollector` + `ObservationProviderBase`

Lives in Unity `ObservationCollector.cs` and `ObservationProviderBase.cs`.

- Assembled each tick in Unity before sending the `tick` RPC
- Returns a `dict` sent as `StepRequest.agent_observations`
- In `HostedWorld`, becomes `world.observe(agent_id)` output

### How they merge

`AgentRuntime._build_observation()` merges all sources in a strict priority order. **Last write wins** within each tier; higher-tier entries overwrite lower-tier entries for the same key:

```
Priority 1 (lowest): Python ObservationRegistry providers
Priority 2:          world.observe(agent_id)          ← Unity observations arrive here via HostedWorld
Priority 3:          VisibilityWorld.nearby_agents     (only if world didn't provide "nearby_agents")
Priority 4 (highest): Engine identity injection        (agent_id, agent_name, inventory, state_ext, ...)
```

**Reserved keys**: The engine always overwrites `agent_id`, `agent_name`, `inventory`, `state_ext`, `state_advice`, `state_str` at priority 4. A Python provider or Unity component that writes any of these keys is silently overridden. The `ObservationProvider` docstring documents this contract but it is not enforced.

### Naming collision: `observe` appears twice

Both `World.observe(agent_id)` and `ObservationProvider.observe(agent_id, capabilities, world)` use the verb "observe." They are different operations:

- `World.observe()` returns the complete world-state dict for an agent (authoritative)
- `ObservationProvider.observe()` returns a partial dict slice (additive, lowest priority)

The shared verb implies parity that doesn't exist. World observations overwrite provider observations on key conflict.

### `ObservationSchema.payload_schema`

Documents what keys a provider is *expected* to return. Never validated at runtime — providers can write any keys. The schema is documentation only. A provider that writes keys not in its `payload_schema` works perfectly; a provider that fails to write a key listed in its schema produces no error.

### `role.observations`: strings with no referent

The `observations:` list in a role definition is `list[str]`. It is:
- Not validated against `ObservationSchema.name` values
- Not validated against `ObservationProviderBase` class names in Unity
- Not used to select or filter providers at runtime
- Not checked by any existing validator

It is documentation stored in a struct that looks operational.

---

## Domain 3: Capabilities

### What capabilities actually are

A capability is a string that an agent holds. The engine uses it for exactly one operation:

```python
# From ActionRegistry.schemas_for() and ObservationRegistry.schemas_for():
if not schema.required_capabilities or schema.required_capabilities & capabilities:
    result.append(schema)
```

This is **set intersection**: an agent can see an action/observation schema if their capability set intersects the schema's required_capabilities set. One matching string is sufficient — OR semantics, not AND.

That's the complete runtime behaviour of capabilities. They don't drive anything else.

### Capabilities are injected into two places

The same `BiomataAgent.capabilities` string array in Unity is used for two different purposes:

1. **Sent to the backend during `CreateAtRuntime` registration** — sets `Agent.capabilities` in Python, which drives the intersection check
2. **Injected into the observation dict** — `collector.SetData("capabilities", resolvedCapabilities)` — puts the capability list in the observation as data the brain can read

These are separate concerns sharing the same source value. The first is an access-control mechanism; the second is observation data.

### The naming inconsistency

`ActionSchema.required_capabilities` (renamed in Phase 1) and `ObservationSchema.tags` (not renamed) are the same concept: a frozenset of capability strings used in an identical intersection check. The rename is half-complete.

### No vocabulary enforcement

There is no registry of valid capability strings. A typo (`"gard"` instead of `"guard"`) produces no error at any layer:

- Python config loader accepts any strings in `capabilities: [...]`
- `ActionManifest` validates action names, not capability strings
- `BiomataRoles.json` exports whatever strings are in the role
- The intersection check against an empty frozenset silently grants no access

The system relies entirely on manual consistency between agent capabilities and schema tags.

---

## Domain 4: Roles

### What a role is at runtime

A role is a Python dict that is consumed once — at agent construction time — and then discarded. After `load_simulation()` runs, no runtime object retains a reference to any `RoleConfig`. The role expands into:

- `Agent.capabilities` — union of role capabilities and agent-explicit capabilities
- `Agent.brain` — constructed from role brain config if agent has no explicit brain
- `Agent.metadata["role"]` — string name stored for downstream inspection

The `observations:` list produces nothing. It is not applied to any runtime object.

### Two brain config types for the same pattern

`BrainRoleConfig` and `ComponentConfig` both represent "a dotted class path plus arbitrary kwargs." Their difference:

- `ComponentConfig` requires `class:` — used for world, registry, brain (YAML agent-level), memory, state_ext
- `BrainRoleConfig` allows `provider:` as an alternative to `class:` — used only for role brain

If `ComponentConfig` supported an optional `provider:` field that resolved via the same provider map, `BrainRoleConfig` would be unnecessary.

### `AgentOwnershipMode` describes lifecycle, not ownership

`BindToExisting`: the agent was created by the backend (YAML or previously registered). Unity attaches a visual shell.  
`CreateAtRuntime`: Unity creates and destroys the agent. The agent is registered/unregistered as the GameObject enters/leaves the scene.

The meaningful difference is **who drives the agent lifecycle** (create / destroy). The word "ownership" carries data-ownership connotations that don't apply here — both modes result in Python owning all agent state.

---

## Domain 5: Commands and effects

### The three mutation channels compared

| Channel | Name | Who consumes | Shape contract | What happens if malformed |
|---|---|---|---|---|
| Agent state | `state_mutations` | `AgentRuntime` (inventory), `StateExtension` | No formal schema; two implicit conventions | Unknown keys are silently ignored by engine; `StateExtension` may error |
| Graph effects | `side_effects` | `SocialEffectSubscriber` via EventBus | `{"type": "social", "from": id, "to": id, "delta": float}` | Non-social types silently ignored |
| Host execution | `engine_commands` | `HostedWorld` → wire → Unity `ActionExecutor` | Completely user-defined; `type` key by convention | Unity logs "no handler" or silently ignores |

### The only `side_effects` type that exists

The code comment in `action.py` lists two types:
```python
# side_effect shapes:
#   {"type": "social",  "from": id, "to": id, "delta": float}
#   {"type": "event",   ...}   — future extensibility
```

The `{"type": "event", ...}` shape is mentioned once in a comment and nowhere else. No code produces it; no code consumes it. It is speculative extensibility written as documentation.

### `engine_commands` has no schema layer

The shape of each dict in `engine_commands` is entirely user-defined. `ActionManifest.engine_command` documents the `type` value (e.g., `"navigate"`) but not the full shape. Each Python handler and its corresponding Unity handler must agree on the shape by convention. There is no validation layer between Python and Unity for this data.

---

## Domain 6: The "host" overload

The word "host" appears in four distinct contexts with related but different meanings:

| Usage | Meaning |
|---|---|
| `HostedWorld` | World whose authoritative state is provided by an external system (Unity/game engine) |
| `HOST_DRIVEN` tick mode | The client (Unity) triggers each tick via RPC |
| `ActionKind.HOST` | The action's effects are executed by the host (Unity), not Python |
| `engine_commands` → host | Commands flow from engine to host (Unity) for execution |

All four uses are consistent in meaning "external client / Unity side." But a newcomer encountering `HostedWorld` without context doesn't know if "hosted" means "hosted by the server," "hosted by the game engine," or something else entirely. The concept would be clearer as `ExternallyDrivenWorld` or `ClientAuthoritative`.

---

## Concept overlap map

```
"Required capabilities" ──► ActionSchema.required_capabilities (Phase 1 name)
                      └───► ObservationSchema.tags              (pre-Phase-1 name)
                      └───► RoleConfig.capabilities             (list form in role)
                      └───► Agent.capabilities                  (runtime frozenset)
                      └───► AgentConfig.capabilities            (YAML list)
                      └───► BiomataAgent.capabilities           (Unity string[])

"Brain config" ────────────► ComponentConfig   (YAML agent-level, world, registry...)
               └───────────► BrainRoleConfig   (role-level only; adds provider: shorthand)

"Observation data" ────────► Python ObservationProvider.observe() → slice dict
                   └───────► Unity ObservationProviderBase.Populate() → slice dict
                   └───────► World.observe() → authoritative dict  ← wins over both

"Action execution channel" ► state_mutations   → Python agent state
                           ► side_effects      → SocialSystem (via EventBus)
                           ► engine_commands   → Host / Unity
```

---

## What the engine does NOT read at runtime

Listed here to prevent false assumptions about what these fields do during a tick:

| Field | What it's for | What uses it at runtime |
|---|---|---|
| `ActionSchema.kind` | LLM prompt label | `prompt_block()` only — no dispatch effect |
| `ActionSchema.examples` | LLM prompt example | First item only in `prompt_block()` |
| `ObservationSchema.payload_schema` | LLM prompt docs | `prompt_block()` only — providers not validated |
| `ObservationSchema.examples` | LLM prompt example | First item only in `prompt_block()` |
| `RoleConfig.observations` | Documentation / Unity hint | Nothing — not applied at runtime |
| `ActionResult.side_effects["type"] == "event"` | Speculative future | Nothing — no consumer exists |
