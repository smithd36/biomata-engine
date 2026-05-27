# Phase 3 — Unity SDK Dual Ownership: Implementation Report

## Summary

Added `AgentOwnershipMode` enum to `BiomataAgent`, enabling two distinct ownership models for NPC agents. `BindToExisting` binds a visual shell to a pre-existing backend agent without touching the protocol. `CreateAtRuntime` preserves and extends the current auto-register/unregister lifecycle. All existing workflows continue unchanged.

---

## Design goals vs. delivery

| Requirement | Status | Notes |
|---|---|---|
| `AgentOwnershipMode` enum | Done | `BindToExisting`, `CreateAtRuntime` |
| `BindToExisting` — bind without registration RPC | Done | `MarkBoundToExisting()` on connect |
| `BindToExisting` — inspector shows id, name, debug | Done | Brain group hidden; Lifecycle shown |
| `CreateAtRuntime` — show all fields | Done | Role, capabilities, brain, memory all shown |
| `CreateAtRuntime` — register on connect | Done | Existing `Bridge.Configure` + `autoRegister` path |
| `CreateAtRuntime` — unregister on destroy | Done | `BiomataAgent.OnDestroy()` fire-and-forget |
| Conditional validation warnings | Done | Brain Class warning only in `CreateAtRuntime` |
| Custom inspector per mode | Done | Groups hidden/shown; labels context-sensitive |
| Runtime state badge per mode | Done | "Bound/Not bound" vs "Registered/Not registered" |
| `Configure()` includes ownershipMode | Done | Fully usable from procedural code |
| Preserve existing workflows | Done | All existing samples compile unchanged |

---

## Files changed

### New

| File | Purpose |
|---|---|
| `unity_sdk/Runtime/Integration/Agents/AgentOwnershipMode.cs` | `AgentOwnershipMode` enum with XML docs |

### Modified

| File | Changes |
|---|---|
| `unity_sdk/Runtime/Integration/Agents/BiomataAgent.cs` | `ownershipMode` field; conditional Awake; Start for BindToExisting; OnDestroy for CreateAtRuntime; mode-sensitive OnValidate |
| `unity_sdk/Runtime/Integration/Agents/UnityAgentBridge.cs` | `MarkBoundToExisting()` public method |
| `unity_sdk/Editor/Integration/BiomataAgentEditor.cs` | Conditional group rendering; mode help box; context-sensitive labels; Bind/Register buttons |

---

## Architecture decisions

### `BindToExisting` does not call `RegisterAsync`

In `BindToExisting` mode the bridge receives `autoRegister: false` in `Awake()`. `BiomataAgent.Start()` checks `_manager.IsConnected` and calls `Bridge.MarkBoundToExisting()` (sets `IsRegistered = true`) directly. No RPC is sent because the assumption is that the backend agent exists (in sim.yaml or registered by another client). This keeps the two modes truly distinct — `BindToExisting` is never at risk of an `AGENT_EXISTS` error.

If the backend agent does not exist, observations still flow but the backend has no agent to tick. This is the expected failure mode — it logs the same as any unreachable agent in the tick response.

### `CreateAtRuntime` unregister via fire-and-forget Task

`UnityAgentBridge.OnDestroy()` doesn't call the backend unregister (it only removes the bridge from the manager's local list). For `CreateAtRuntime`, `BiomataAgent.OnDestroy()` calls `_manager.Client.Agents.TryRemoveAsync(_resolvedId)` and discards the Task with `_ = ...`. Since `Task` objects in .NET continue executing even when the issuing MonoBehaviour is destroyed, the backend receives the `remove_agent` RPC as long as the WebSocket is still open.

`_resolvedId` is cached in Awake so it remains available in OnDestroy even if the Bridge component is destroyed first.

### Mode is transparent to the tick pipeline

Both modes produce the same observation and decision pipeline. `Bridge.BuildObservation()` and `Bridge.ApplyDecision()` are called identically by `UnitySimulationManager` regardless of mode. The only runtime difference is whether `IsRegistered` was set by a backend acknowledgement or by `MarkBoundToExisting()`.

### Inspector groups collapsed by ownership mode

The custom editor replaces `DrawDefaultInspector()` with explicit `EditorGUILayout.PropertyField` calls. Fields serialized on the component (role, capabilities, brain class, etc.) are always there — only their Inspector visibility changes. Serialized data is never discarded when the mode is toggled, so a designer can switch from `CreateAtRuntime` to `BindToExisting` and back without losing the brain configuration they entered.

### `autoRegister` label is context-sensitive

The same boolean is shown as "Auto Bind" in `BindToExisting` mode and "Auto Register" in `CreateAtRuntime` mode. This avoids adding a second field for the same concept and keeps the field count minimal.

---

## Inspector behavior

### `BindToExisting`

```
[Ownership]
  Mode:          BindToExisting  ← dropdown
  ℹ agent is pre-declared on the backend; no registration RPC is sent.

[Identity]
  Agent ID:      guard_001
  Display Name:  Aldric

[Debug]
  Auto Bind:     ✓
```

### `CreateAtRuntime`

```
[Ownership]
  Mode:          CreateAtRuntime  ← dropdown
  ℹ Unity owns this agent. Registered on connect, unregistered on destroy. Brain Class required.

[Identity]
  Agent ID:      scout_001
  Display Name:  Scout

[Role]
  Role:          patrol
  Capabilities:  patrol, authority

[Brain]
  Brain Class:   src.plugins.builtin.ollama.brain.OllamaLLMBrain
  Memory Class:  (empty)
  Brain Config:  {"model": "qwen2.5:14b"}
  Memory Config: (empty)

[Debug]
  Auto Register: ✓
```

---

## Runtime state panel (play mode)

| Mode | Registered | Label |
|---|---|---|
| `BindToExisting` | Yes | ● Bound |
| `BindToExisting` | No | ○ Not bound |
| `CreateAtRuntime` | Yes | ● Registered |
| `CreateAtRuntime` | No | ○ Not registered |

The play-mode buttons change accordingly:
- `BindToExisting`: **Bind** (calls `MarkBoundToExisting()`)
- `CreateAtRuntime`: **Register** / **Unregister** (calls `Bridge.Register()` / `Bridge.Unregister()`)

---

## Lifecycle comparison

| Aspect | BindToExisting | CreateAtRuntime |
|---|---|---|
| Backend registration RPC | None | `register_agent` on connect |
| `IsRegistered` set by | `MarkBoundToExisting()` | Backend ACK from `RegisterAsync` |
| Backend unregistration RPC | None | `remove_agent` on `OnDestroy()` |
| Backend agent survives destroy | Yes | No (unregistered) |
| Brain Class required | No | Yes |
| Fields shown in inspector | id, name, lifecycle | id, name, role, capabilities, brain, memory, lifecycle |
| Validation warning for brain | Never | When brain class is empty |

---

## Preserved behavior

- `UnityAgentBridge` remains the low-level primitive. It is unchanged except for the new `MarkBoundToExisting()` method which has no effect on existing code paths.
- `VillageLifeDemo` and all other samples use `UnityAgentBridge` directly and are unaffected.
- All existing `BiomataAgent` components in serialized scenes load with `ownershipMode = BindToExisting` (index 0, the field initializer default). This is the correct default for agents already configured in sim.yaml.
- The `Configure()` method signature is extended with an optional `ownershipMode` parameter at the end; all existing call sites are unaffected by the default value.
- `ProductionIntegration` sample components need `ownershipMode` set to `CreateAtRuntime` if they supply a brain class and expect runtime registration — this is a one-field change in the Inspector.
