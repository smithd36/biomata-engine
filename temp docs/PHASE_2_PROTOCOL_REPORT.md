# Phase 2 — Transport Protocol Extension: Implementation Report

## Summary

Extended Biomata Protocol v1 with three dot-notation agent lifecycle methods (`agent.register`, `agent.unregister`, `agent.list`). All existing `register_agent` and `remove_agent` clients continue to work unchanged. The new methods share the same engine path — no engine code was modified.

---

## Design goals vs. delivery

| Requirement | Status | Notes |
|---|---|---|
| New `agent.register` method | Done | Nested `brain:{class,config}` / `memory:{class,config}` payload |
| New `agent.unregister` method | Done | Uses `id` key; parallel to `remove_agent` which uses `agent_id` |
| New `agent.list` method | Done | Returns all agents (YAML-static + runtime-dynamic) |
| Backwards compatibility | Done | `register_agent` and `remove_agent` dispatch paths untouched |
| Capabilities advertisement | Done | Three new entries in both `_CAPABILITIES_HOST_DRIVEN` and `_CAPABILITIES_AUTONOMOUS` |
| `Method` constants | Done | `Method.AGENT_REGISTER`, `AGENT_UNREGISTER`, `AGENT_LIST` |
| Documentation | Done | `docs/transport_runtime_agents.md` |

---

## Files changed

### Modified

| File | Changes |
|---|---|
| `src/transport/websocket/protocol.py` | Added `Method.AGENT_REGISTER`, `AGENT_UNREGISTER`, `AGENT_LIST` constants; updated `Method.ALL`; added three entries to both capabilities lists |
| `src/transport/websocket/handler.py` | Added three dispatch cases; added `_handle_agent_register()`, `_handle_agent_unregister()`, `_handle_agent_list()` |

### New

| File | Purpose |
|---|---|
| `docs/transport_runtime_agents.md` | Full protocol reference for runtime agent methods |

---

## Architecture decisions

### Dot-notation naming

The new method names use a dot separator (`agent.register`, not `agentRegister` or `register-agent`). This makes the namespace explicit — any future agent-scoped methods are trivially discoverable — while staying within the existing string-method dispatch pattern. The handler checks for the dot form after all legacy names, so there is no dispatch overhead for clients that use only the old names.

### Nested payload vs. flat params

The legacy `register_agent` uses flat keys: `brain_class`, `brain_config`, `memory_class`, `memory_config`. The new `agent.register` uses nested objects:

```json
"brain":  { "class": "...", "config": {} },
"memory": { "class": "...", "config": {} }
```

Reasons for the change:
- Matches the structure of `sim.yaml` agent blocks, reducing cognitive overhead for integrators.
- Groups class + config together, making it impossible to accidentally set `memory_class` without `memory_config` as separate keys that might be missed.
- Leaves room for future sub-fields (e.g. `brain.version`, `memory.backend`) without adding more top-level keys.

The two formats are not interchangeable — `agent.register` does not accept `brain_class`, and `register_agent` does not accept a `brain` object. The mapping is documented in `docs/transport_runtime_agents.md`.

### Response key naming

The dot-notation methods return `id` (not `agent_id`) in their results, consistent with their request payload keys. The legacy methods continue to return `agent_id`. This keeps each method self-consistent: the same key name used in the request appears in the response.

### `agent.list` includes all agents

`agent.list` returns both YAML-static agents (loaded at startup from `sim.yaml`) and runtime-dynamic agents (registered via WebSocket). Order matches the scheduler's agent list. This is more useful than a runtime-only list because:
- Clients using `agent.list` to build a UI agent roster need all agents, not just their own.
- YAML-static agents can become dynamic post-startup; there is no meaningful architectural distinction to expose at the protocol level.

### No engine changes

All three new handlers build an `AgentDefinition` and route through `session.register_agent()` or `session.unregister_agent()` — the same path as the Phase 1 legacy handlers. The handler accesses `self._sim.agents` directly only for the read-only `agent.list` response and the reconnect shortcut lookup, both of which are already in the legacy handler. No new engine coupling was introduced.

---

## Wire examples

### Register

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
    "metadata": { "scene": "level_01" }
  }
}
```

Response:

```json
{
  "ok": true,
  "result": { "id": "scout_001", "reconnected": false, "capabilities": ["patrol"] }
}
```

### Unregister

```json
{
  "type": "req",
  "id": "r-002",
  "method": "agent.unregister",
  "params": { "id": "scout_001" }
}
```

Response:

```json
{ "ok": true, "result": { "id": "scout_001" } }
```

### List

```json
{ "type": "req", "id": "r-003", "method": "agent.list", "params": {} }
```

Response:

```json
{
  "ok": true,
  "result": {
    "agents": [
      { "id": "guard_001", "name": "Aldric", "capabilities": ["patrol", "authority"], "metadata": {} },
      { "id": "scout_001", "name": "Scout",  "capabilities": ["patrol"], "metadata": { "scene": "level_01" } }
    ],
    "count": 2
  }
}
```

---

## Backwards compatibility

All existing behavior is preserved:

- `register_agent` and `remove_agent` dispatch paths are unchanged.
- Their handler methods (`_handle_register_agent`, `_handle_remove_agent`) are unchanged.
- Error codes `-1` through `-6` are unchanged.
- The capabilities list is additive only — no existing entry was removed.
- The `Method.ALL` tuple is extended; nothing that iterated it previously breaks.
- The server hello frame gains three new capability strings; clients that ignore unknown capabilities are unaffected.

---

## What is NOT in Phase 2

Deliberately deferred:

- **`agent.update`** — modifying a registered agent's capabilities or brain config at runtime. Deferred because it requires defining semantics for in-flight ticks (does the brain restart? does memory carry over?).
- **`agent.get`** — single-agent lookup by id. Covered by `agent.list` for now; a dedicated method can be added if payload size becomes a concern at large agent counts.
- **Filtering in `agent.list`** — filtering by capability, metadata field, or registration source. The current result set is small (tens to low hundreds of agents); a filter param can be added without breaking callers when needed.
