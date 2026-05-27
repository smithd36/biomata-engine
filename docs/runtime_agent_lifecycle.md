# Runtime Agent Lifecycle

Reference for creating, inspecting, and removing agents at runtime through the Biomata WebSocket protocol.

---

## Overview

Biomata supports two agent creation pathways:

| Pathway | When | Entry point |
|---|---|---|
| **YAML-static** | Engine startup | `sim.yaml` → `Simulation.from_config()` |
| **Runtime dynamic** | Any time after startup | `register_agent` WebSocket RPC → `Simulation.register_agent()` |

Both pathways produce identical `Agent` objects and participate fully in the tick loop.

---

## Agent definition fields

The following fields are accepted by the `register_agent` RPC and the `AgentDefinition` domain model:

| Field | Type | Required | Description |
|---|---|---|---|
| `agent_id` | `string` | Yes | Unique identifier. Alphanumeric, underscores, hyphens; max 128 chars. |
| `agent_name` | `string` | Yes | Human-readable display name. |
| `brain_class` | `string` | Yes | Dotted Python path to a Brain protocol implementation. |
| `brain_config` | `object` | No | Keyword arguments forwarded to the brain constructor. |
| `memory_class` | `string` | No | Dotted Python path to a Memory protocol implementation. Defaults to `SimpleMemory`. |
| `memory_config` | `object` | No | Keyword arguments forwarded to the memory constructor. |
| `capabilities` | `string[]` | No | Capability tags that gate action schema and observation provider visibility. |
| `inventory` | `object` | No | Starting item counts, e.g. `{"gold": 10}`. |
| `metadata` | `object` | No | Arbitrary key/value pairs for downstream inspection. Not consumed by the tick loop. |
| `reconnect` | `boolean` | No | If `true` and the agent is already registered, return its current state without error. Safe for use on WebSocket reconnect. |

---

## Registration lifecycle

### 1. Validate

`AgentDefinition` is validated structurally before any Python imports are attempted:

- `agent_id` must be non-empty, match `[A-Za-z0-9_\-]+`, and be ≤ 128 characters.
- `agent_name` must be non-empty.
- `brain_class` must be non-empty.
- `capabilities` entries must be strings.
- `inventory` keys must be strings.

Validation errors are returned as `VALIDATION_ERROR (-6)` with a message listing all failures.

### 2. Duplicate check

If `reconnect=false` (default) and `agent_id` is already registered, the server returns `AGENT_EXISTS (-2)`.

If `reconnect=true` and the agent is already registered, the server returns:

```json
{
  "agent_id": "scout_001",
  "reconnected": true,
  "capabilities": ["patrol"]
}
```

No new agent is created; the existing one continues unaffected.

### 3. Construct

The engine imports `brain_class` (and optionally `memory_class`) using the same dynamic import mechanism as the YAML loader. The brain is instantiated with `brain_config` as keyword arguments; same for the memory.

Import failures produce `IMPORT_ERROR (-4)`. Constructor failures produce `VALIDATION_ERROR (-6)`.

### 4. Register

On success, the new agent is:

1. Appended to `Simulation.agents`.
2. Added to the social graph (`social.add_agent(id, name)`) if a social system is configured.
3. Passed to `world.register_agents(agents)` if the world supports it (e.g. `HostedWorld`).
4. Announced via an `agent_registered` event on the engine bus.

The response is:

```json
{
  "agent_id": "scout_001",
  "reconnected": false,
  "capabilities": ["patrol"]
}
```

### 5. Participate in ticks

The agent enters the scheduler's agent list immediately. On the next tick call:

- Observations are collected (providers filtered by capabilities).
- The brain receives the observation and returns an `Intent`.
- The intent is validated and dispatched through the action registry.
- Results and engine commands are returned in the tick response.

No tick boundary synchronization is required — the agent participates from the next tick after registration.

---

## Unregistration lifecycle

Send a `remove_agent` RPC:

```json
{
  "type": "req",
  "method": "remove_agent",
  "params": { "agent_id": "scout_001" }
}
```

On success:

1. The agent is removed from `Simulation.agents`.
2. `world.register_agents(agents)` is called with the updated list.
3. `brain.close()` is called if the brain implements the `Closeable` protocol.
4. An `agent_unregistered` event is emitted on the engine bus.

If `agent_id` is not registered, the server returns `AGENT_NOT_FOUND (-3)`.

---

## Reconnect-safe registration

If the Unity client (or any host engine) disconnects and reconnects, its agents remain registered on the server — the simulation continues running across WebSocket disconnects. On reconnect, the client should re-register with `reconnect=true` for each agent it owns:

```json
{
  "type": "req",
  "method": "register_agent",
  "params": {
    "agent_id": "scout_001",
    "agent_name": "Scout",
    "brain_class": "src.plugins.builtin.idle_brain.brain.IdleBrain",
    "reconnect": true
  }
}
```

If `scout_001` is still registered, the server responds with `"reconnected": true` and no new agent is created. If the server was restarted (agent no longer exists), the client's registration creates the agent fresh.

---

## Event stream

Subscribe to `agent_registered` and `agent_unregistered` events to track dynamic agents:

```json
{ "type": "req", "method": "subscribe_events",
  "params": { "event_types": ["agent_registered", "agent_unregistered"] } }
```

**`agent_registered` event data:**

```json
{
  "agent_name":   "Scout",
  "capabilities": ["patrol"],
  "metadata":     { "scene": "level_01" }
}
```

**`agent_unregistered` event data:**

```json
{
  "agent_name": "Scout"
}
```

---

## Comparison: YAML-static vs runtime-dynamic

| Aspect | YAML-static | Runtime-dynamic |
|---|---|---|
| Defined in | `sim.yaml` agents block | `register_agent` RPC params |
| When created | Before first tick | Any time after startup |
| Capabilities | `capabilities:` YAML list | `capabilities` RPC param |
| Brain config | `brain.personality` etc. | `brain_config` dict |
| `state_ext` | Supported | Not supported (use inventory + metadata for per-agent custom state) |
| Social graph | Always populated at startup | Added on registration if social is configured |
| Tick participation | From tick 1 | From next tick after registration |
| Snapshot/restore | Full support | Full support (agents in list are snapshotted) |
| Reconnect safety | Stateless — no re-registration needed | Use `reconnect=true` |

---

## Error codes

| Code | Name | Cause |
|---|---|---|
| `-2` | `AGENT_EXISTS` | `agent_id` already registered and `reconnect=false` |
| `-3` | `AGENT_NOT_FOUND` | `agent_id` not registered (for `remove_agent`) |
| `-4` | `IMPORT_ERROR` | `brain_class` or `memory_class` dotted path cannot be resolved |
| `-6` | `VALIDATION_ERROR` | Structural validation failed (see error message for field list) |

---

## Implementation map

| Concept | File | Symbol |
|---|---|---|
| Definition model | `src/engine/agent_definition.py` | `AgentDefinition` |
| Validation | `src/engine/agent_definition.py` | `validate_definition()` |
| Construction | `src/engine/agent_definition.py` | `build_agent_from_definition()` |
| Engine lifecycle | `src/engine/simulation.py` | `Simulation.register_agent()`, `.unregister_agent()` |
| Service delegation | `src/service/session.py` | `SimulationSession.register_agent()`, `.unregister_agent()` |
| Transport RPC | `src/transport/websocket/handler.py` | `_handle_register_agent()`, `_handle_remove_agent()` |
| Bus events | `src/engine/event_bus.py` | `AGENT_REGISTERED`, `AGENT_UNREGISTERED` |
| Error codes | `src/transport/websocket/protocol.py` | `ErrorCode.VALIDATION_ERROR` |
