# Transport — Runtime Agent Lifecycle

Reference for the Biomata WebSocket Protocol v1 runtime agent methods.

---

## Overview

Two parallel method sets manage runtime agents over the wire:

| Style | Methods | Payload keys | Since |
|---|---|---|---|
| **Legacy flat** | `register_agent`, `remove_agent` | `agent_id`, `agent_name`, `brain_class`, `brain_config`, … | v1 (Phase 1) |
| **Dot-notation** | `agent.register`, `agent.unregister`, `agent.list` | `id`, `name`, `brain:{class,config}`, `memory:{class,config}`, … | v1 (Phase 2) |

Both styles route to the same `Simulation.register_agent()` / `unregister_agent()` engine methods and produce identical side effects. The legacy names remain fully functional; existing clients need no changes.

New integrations should prefer the dot-notation style — it groups brain and memory configuration naturally and aligns with how `sim.yaml` agent blocks are structured.

---

## Capabilities advertisement

The server hello frame (`hlo`) includes a `capabilities` list. Phase 2 adds three new entries that appear in both tick modes:

```json
{
  "type": "hlo",
  "capabilities": [
    "tick", "register_agent", "remove_agent", "send_observation",
    "snapshot", "restore", "events",
    "agent.register", "agent.unregister", "agent.list"
  ]
}
```

Clients can check for `"agent.register"` in `capabilities` before calling the dot-notation methods.

---

## `agent.register`

Register a new agent or reconnect to an existing one.

### Request

```json
{
  "type": "req",
  "v": 1,
  "id": "r-001",
  "method": "agent.register",
  "params": {
    "id":   "scout_001",
    "name": "Scout",
    "capabilities": ["patrol"],
    "brain": {
      "class":  "src.plugins.builtin.idle_brain.brain.IdleBrain",
      "config": {}
    },
    "memory": {
      "class":  "src.plugins.builtin.simple_memory.memory.SimpleMemory",
      "config": {}
    },
    "inventory": { "torch": 1 },
    "metadata":  { "scene": "level_01", "owner": "client_A" },
    "reconnect": false
  }
}
```

### Params

| Field | Type | Required | Description |
|---|---|---|---|
| `id` | `string` | Yes | Unique agent identifier. Alphanumeric, underscores, hyphens; max 128 chars. |
| `name` | `string` | Yes | Human-readable display name. |
| `brain.class` | `string` | Yes | Dotted Python path to a Brain protocol implementation. |
| `brain.config` | `object` | No | Keyword arguments forwarded to the brain constructor. |
| `memory.class` | `string` | No | Dotted Python path to a Memory implementation. Defaults to `SimpleMemory`. |
| `memory.config` | `object` | No | Keyword arguments forwarded to the memory constructor. |
| `capabilities` | `string[]` | No | Capability tags gating action schema and observation provider visibility. |
| `inventory` | `object` | No | Starting item counts, e.g. `{"gold": 10}`. |
| `metadata` | `object` | No | Arbitrary key/value pairs for downstream inspection. |
| `reconnect` | `boolean` | No | If `true` and the agent already exists, return its current state without error. Default `false`. |

### Response — new agent

```json
{
  "type": "res",
  "id": "r-001",
  "ok": true,
  "result": {
    "id":           "scout_001",
    "reconnected":  false,
    "capabilities": ["patrol"]
  }
}
```

### Response — reconnect (agent already existed)

```json
{
  "ok": true,
  "result": {
    "id":           "scout_001",
    "reconnected":  true,
    "capabilities": ["patrol"]
  }
}
```

### Error responses

| Scenario | `error.code` | `error.name` |
|---|---|---|
| `id` or `name` empty, bad chars, too long | `-6` | `VALIDATION_ERROR` |
| `brain.class` empty | `-6` | `VALIDATION_ERROR` |
| `id` already registered and `reconnect=false` | `-2` | `AGENT_EXISTS` |
| `brain.class` dotted path cannot be resolved | `-4` | `IMPORT_ERROR` |
| Brain/memory constructor raised an exception | `-6` | `VALIDATION_ERROR` |

---

## `agent.unregister`

Remove a registered agent and release its resources.

### Request

```json
{
  "type": "req",
  "v": 1,
  "id": "r-002",
  "method": "agent.unregister",
  "params": { "id": "scout_001" }
}
```

### Params

| Field | Type | Required | Description |
|---|---|---|---|
| `id` | `string` | Yes | The agent identifier to remove. |

### Response

```json
{
  "ok": true,
  "result": { "id": "scout_001" }
}
```

### Error responses

| Scenario | `error.code` | `error.name` |
|---|---|---|
| `id` missing | `-32602` | `INVALID_PARAMS` |
| Agent not found | `-3` | `AGENT_NOT_FOUND` |

### Side effects

On success the engine:

1. Removes the agent from `Simulation.agents` (excluded from the next tick).
2. Calls `world.register_agents(agents)` with the updated list if supported.
3. Calls `brain.close()` if the brain implements the `Closeable` protocol.
4. Emits an `agent_unregistered` event on the engine bus.

---

## `agent.list`

Return all currently registered agents.

### Request

```json
{
  "type": "req",
  "v": 1,
  "id": "r-003",
  "method": "agent.list",
  "params": {}
}
```

No params required.

### Response

```json
{
  "ok": true,
  "result": {
    "agents": [
      {
        "id":           "guard_001",
        "name":         "Aldric",
        "capabilities": ["patrol", "authority"],
        "metadata":     {}
      },
      {
        "id":           "scout_001",
        "name":         "Scout",
        "capabilities": ["patrol"],
        "metadata":     { "scene": "level_01" }
      }
    ],
    "count": 2
  }
}
```

The list includes both YAML-static agents (created at startup) and runtime-dynamic agents (created via `agent.register` or `register_agent`). Order matches the scheduler's agent list.

---

## Event stream

Subscribe to lifecycle events to track agents across all clients:

```json
{
  "type": "req",
  "method": "subscribe_events",
  "params": { "event_types": ["agent_registered", "agent_unregistered"] }
}
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

Both events include the standard envelope fields: `agent_id`, `tick`, `seq`, `ts`.

---

## Reconnect-safe flow

When a Unity client reconnects after a WebSocket drop, agents remain registered on the server. The recommended reconnect flow using dot-notation:

```json
{
  "method": "agent.register",
  "params": {
    "id":   "scout_001",
    "name": "Scout",
    "brain": { "class": "src.plugins.builtin.idle_brain.brain.IdleBrain" },
    "reconnect": true
  }
}
```

If the agent is still registered the server returns `"reconnected": true` without creating a new agent. If the server was restarted the client's registration creates the agent fresh. The client calls this unconditionally on every reconnect — no server state tracking needed.

---

## Legacy method reference

The original flat-param methods continue to work unchanged:

| Method | Equivalent dot-notation | Key difference |
|---|---|---|
| `register_agent` | `agent.register` | Uses `agent_id` / `agent_name` / `brain_class` / `brain_config` flat keys |
| `remove_agent` | `agent.unregister` | Uses `agent_id` instead of `id` |

There is no legacy equivalent of `agent.list` — it is new in Phase 2.

---

## Implementation map

| Concept | File | Symbol |
|---|---|---|
| Method constants | `src/transport/websocket/protocol.py` | `Method.AGENT_REGISTER`, `.AGENT_UNREGISTER`, `.AGENT_LIST` |
| Capabilities lists | `src/transport/websocket/protocol.py` | `_CAPABILITIES_HOST_DRIVEN`, `_CAPABILITIES_AUTONOMOUS` |
| Dispatch | `src/transport/websocket/handler.py` | `ConnectionHandler._dispatch()` |
| Register handler | `src/transport/websocket/handler.py` | `_handle_agent_register()` |
| Unregister handler | `src/transport/websocket/handler.py` | `_handle_agent_unregister()` |
| List handler | `src/transport/websocket/handler.py` | `_handle_agent_list()` |
| Engine lifecycle | `src/engine/simulation.py` | `Simulation.register_agent()`, `.unregister_agent()` |
| Domain model | `src/engine/agent_definition.py` | `AgentDefinition`, `validate_definition()` |
