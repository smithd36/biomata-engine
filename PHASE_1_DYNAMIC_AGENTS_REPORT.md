# Phase 1 — Dynamic Agent Creation: Implementation Report

## Summary

Added a complete runtime agent registration API to biomata-engine. Agents can now be created, validated, and removed after engine startup via the existing WebSocket transport. All YAML-defined agent workflows are unchanged; the new pathway is purely additive.

---

## Design goals vs. delivery

| Requirement | Status | Notes |
|---|---|---|
| Preserve existing behavior | Done | YAML agents, examples, tick loop untouched |
| `AgentDefinition` domain model | Done | `src/engine/agent_definition.py` |
| Full field support (id, name, capabilities, brain, memory, inventory, metadata) | Done | All fields wired end-to-end |
| Validation with clean rejection | Done | `validate_definition()` — structural errors before any import |
| Duplicate ID rejection | Done | `AGENT_EXISTS (-2)` |
| Reconnect-safe registration | Done | `reconnect=true` param returns existing agent without error |
| `unregister_agent` | Done | Closes brain resources, emits event, updates world |
| Scheduler participation | Done | Agent enters `Simulation.agents` immediately; next tick includes it |
| Social system integration | Done | `social.add_agent()` called on registration if social configured |
| Lifecycle events | Done | `AGENT_REGISTERED`, `AGENT_UNREGISTERED` on engine bus |
| Validation error code | Done | `VALIDATION_ERROR (-6)` |
| `docs/runtime_agent_lifecycle.md` | Done | Full reference |

---

## Files changed

### New

| File | Purpose |
|---|---|
| `src/engine/agent_definition.py` | `AgentDefinition`, `AgentDefinitionError`, `validate_definition()`, `build_agent_from_definition()` |
| `docs/runtime_agent_lifecycle.md` | End-to-end lifecycle reference |

### Modified

| File | Changes |
|---|---|
| `src/engine/agent.py` | Added `metadata: dict[str, Any]` field (default `{}`, backward-compatible) |
| `src/engine/event_bus.py` | Added `AGENT_REGISTERED`, `AGENT_UNREGISTERED` constants |
| `src/engine/simulation.py` | Added `register_agent()`, `unregister_agent()` methods |
| `src/service/session.py` | Added `register_agent()`, `unregister_agent()` delegation |
| `src/transport/websocket/protocol.py` | Added `VALIDATION_ERROR = -6` error code |
| `src/transport/websocket/handler.py` | Refactored both agent RPCs; removed direct `sim` mutation |

---

## Architecture decisions

### `AgentDefinition` as a first-class domain model

Previously the transport handler owned all registration logic: it parsed raw params, imported classes, and directly mutated `sim.agents`. This created coupling between transport protocol and engine internals.

`AgentDefinition` moves the specification into the engine layer. The handler's job is now only protocol translation — parse wire params, build `AgentDefinition`, delegate to session. No engine types leak into the handler.

### Validation before construction

`validate_definition()` is fast and side-effect-free — it checks string constraints without importing anything. Import failures (dotted path not found, constructor rejected kwargs) are a separate concern handled in `build_agent_from_definition()` with distinct error types.

This separation means:
- Validation errors produce `VALIDATION_ERROR (-6)` with field-annotated messages.
- Import failures produce `IMPORT_ERROR (-4)`.
- Constructor failures produce `VALIDATION_ERROR (-6)` with a `brain_config`/`memory_config` annotation.

### Lifecycle owned by `Simulation`, not the handler

`Simulation.register_agent()` and `unregister_agent()` own the full lifecycle:

```
register_agent(defn)
  validate duplicate id
  → build_agent_from_definition(defn)
  → agents.append(agent)
  → social.add_agent(id, name)         # if social configured
  → world.register_agents(agents)      # if world supports it
  → bus.emit(AGENT_REGISTERED)
  return agent

unregister_agent(agent_id)
  find agent
  → agents = [a for a != id]
  → world.register_agents(agents)
  → brain.close()                      # if Closeable
  → bus.emit(AGENT_UNREGISTERED)
  return agent
```

The `ConnectionHandler` docstring explicitly states it should not import from `src.engine`. The refactored handler respects this: it imports only from `src.engine.agent_definition` (the domain model) and `src.service` (the session).

### Reconnect-safe semantics

The `reconnect=true` shortcut is checked before validation, before construction, and before any side effects. If the agent is already registered, the handler returns immediately with `"reconnected": true`. This makes client-side reconnect logic trivial: always call `register_agent` with `reconnect=true` after a WebSocket reconnect; the server does the right thing regardless of whether the agent survived the disconnect.

### Social system integration

`WeightedGraphSocial.add_agent(id, name)` is called during `Simulation.register_agent()` using `hasattr` duck-typing — the same pattern used elsewhere in the codebase. If no social system is configured (`self.social is None`), this is a no-op.

### `metadata` field on `Agent`

Added as a default-empty dict on the `Agent` dataclass. Since it has a default, all existing `Agent(id=..., name=..., brain=..., memory=...)` call sites compile and run without changes. YAML-loaded agents have `metadata={}`.

Metadata is stored on the agent (not only in the definition) so it is:
- Accessible after registration via `agent.metadata`.
- Included in the `AGENT_REGISTERED` event's `data` dict.
- Preserved across snapshot/restore (it's part of the `Agent` object which snapshot serializes by reference).

---

## What is NOT in Phase 1

Deliberately excluded to keep scope tight:

- **`state_ext` support for dynamic agents** — `AgentStateExtension` construction requires simulation-specific subclasses and is rarely needed for runtime agents. YAML agents still support it fully.
- **Scheduler reordering** — `SequentialScheduler` uses a fixed agent order. New agents append to the end. Explicit ordering for runtime agents is deferred.
- **Unity SDK changes** — `BiomataAgent.autoRegister` already sends `register_agent` via `UnityAgentBridge`. The new fields (`capabilities`, `inventory`, `metadata`, `reconnect`) are immediately available to the Unity SDK in a future phase without backend changes.
- **Agent listing RPC** — a `list_agents` method was considered but not added; `health_check` already returns `agent_count` and the event stream provides add/remove deltas.

---

## Wire protocol examples

### Register a new agent

```json
{
  "type": "req",
  "id": "r-001",
  "method": "register_agent",
  "params": {
    "agent_id":    "scout_001",
    "agent_name":  "Scout",
    "brain_class": "src.plugins.builtin.idle_brain.brain.IdleBrain",
    "capabilities": ["patrol"],
    "inventory":   { "torch": 1 },
    "metadata":    { "scene": "level_01", "owner": "client_A" }
  }
}
```

Response:

```json
{
  "type": "res",
  "id": "r-001",
  "ok": true,
  "result": {
    "agent_id":     "scout_001",
    "reconnected":  false,
    "capabilities": ["patrol"]
  }
}
```

### Reconnect-safe re-registration

```json
{
  "type": "req",
  "method": "register_agent",
  "params": {
    "agent_id":    "scout_001",
    "agent_name":  "Scout",
    "brain_class": "src.plugins.builtin.idle_brain.brain.IdleBrain",
    "reconnect":   true
  }
}
```

If already registered:

```json
{
  "ok": true,
  "result": { "agent_id": "scout_001", "reconnected": true, "capabilities": ["patrol"] }
}
```

### Validation failure

```json
{
  "ok": false,
  "error": {
    "code": -6,
    "name": "VALIDATION_ERROR",
    "message": "id: must contain only alphanumeric characters, underscores, and hyphens; brain_class: must be a non-empty dotted Python path"
  }
}
```

### Unregister

```json
{ "type": "req", "method": "remove_agent", "params": { "agent_id": "scout_001" } }
```

Response:

```json
{ "ok": true, "result": { "agent_id": "scout_001" } }
```

---

## Preserved behavior

- All sim.yaml examples continue to work unchanged.
- `Simulation.from_config()` path is untouched.
- `Agent` dataclass construction with positional or keyword args (excluding `metadata`) is backward-compatible.
- All existing event types (`tick_start`, `tick_end`, `action_completed`, etc.) are unchanged.
- Error codes `-1` through `-5` are unchanged.
- The `_SIGNING_KEY` snapshot security mechanism is unchanged.
