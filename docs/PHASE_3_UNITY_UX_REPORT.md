# Phase 3 — Unity SDK Validation & UX Report

> **Refactor goal**: Eliminate silent runtime failures by surfacing configuration problems at edit time in the Inspector, and make runtime errors actionable with structured diagnostic output.

---

## What was delivered

### New files

| File | Purpose |
|---|---|
| `docs/unity/validation_and_debugging.md` | Operational guide covering all validation surfaces, manifest loading, debugging scenarios |
| `PHASE_3_UNITY_UX_REPORT.md` | This file |

### Modified files

| File | Change |
|---|---|
| `unity_sdk/Editor/Integration/BiomataAgentEditor.cs` | Full rewrite: enhanced validation notices, mode-separated field display, new Action Coverage panel |
| `unity_sdk/Runtime/Integration/Agents/ActionExecutor.cs` | Structured diagnostic log for missing handler with agent identity, action description, and fix instruction |

---

## What was NOT changed

- `BiomataAgent.cs` runtime behaviour — unchanged
- `ActionManifestValidator.cs` — existing menu item unchanged
- `RoleManifestValidator.cs` — existing menu item unchanged
- `ActionManifestLoader.cs` — unchanged
- `RoleManifestLoader.cs` — unchanged
- All Python files — untouched
- Wire protocol — untouched

---

## Inspector changes in detail

### Before Phase 3

The Inspector had:
- One HelpBox for empty Agent ID
- One HelpBox for missing Brain Class
- A duplicate ID scan
- All fields shown regardless of ownership mode (Brain/Role/Capabilities always visible)
- No action coverage information
- No role validation

### After Phase 3

**Validation strip** (top, always visible):

| Check | Level | Condition |
|---|---|---|
| Empty Agent ID | Warning | ID field is blank |
| Duplicate Agent ID | Error | Another BiomataAgent in scene has the same ID |
| No brain and no role (CreateAtRuntime) | Warning | Both Brain Class and Role are blank |
| Unknown role name | Error | Role string not in BiomataRoles.json |
| Missing handler for visible action | Warning | Manifest loaded + agent has visible actions with no handler |

**Mode-separated fields**: Brain Class, Memory Class, Brain Config, Role, Capabilities are only rendered in `CreateAtRuntime` mode. In `BindToExisting` mode the Inspector is shorter and focused: the backend owns those settings and showing them creates confusion.

**Role-derived capabilities hint**: When a role is set and the role has capabilities, a note below the Capabilities array shows `"Role 'Guard' adds: guard, patrol, authority"` so designers know what they're inheriting.

**Brain source hint**: When Brain Class is empty and a role provides one, a note shows `"Brain will be supplied by Role 'Guard': src.plugins.builtin.idle_brain.brain.IdleBrain"`.

**Action Coverage foldout** (new):
- Loads BiomataActions.json via `ActionManifestLoader`
- Computes effective capabilities from Inspector + role manifest
- Gets handlers from `GetComponents<ActionHandlerBase>()`
- Filters manifest actions to visible (capability-matched) vs gated (not visible)
- For each visible action: ✓ green with handler class name, or ✗ red with "no handler"
- Gated actions collapsible; shows required capabilities
- Summary strip + "Refresh" and "Validate Scene" buttons
- Graceful degradation: shows info notice if JSON not found

**Runtime state**: Now includes `Role` field alongside ID and Display Name.

---

## Runtime diagnostic changes in detail

### Before Phase 3

`ActionExecutor` logged at `Debug.Log` (info level):
```
[Biomata] No handler for action 'patrol' on agent 'guard_001'. Add a matching ActionHandlerBase component to the agent GameObject.
```

Problems: info level (easy to miss), no action description, no list of what handlers ARE present, generic fix instruction.

### After Phase 3

Logs at `Debug.LogWarning` with the `UnityAgentBridge` as context (clicking in Console selects the GameObject):

```
[Biomata] No handler for action 'patrol'
  Agent:       guard_001  ("Aldric")
  Description: Move to a named waypoint on the patrol route.
  Handlers:    IdleActionHandler, MoveActionHandler, SpeakActionHandler
  Fix:         Add a component that extends ActionHandlerBase and returns true for CanHandle("patrol").
```

What each line tells you:

- **Agent** — which agent the failure is on, with both ID and display name
- **Description** — from the manifest; confirms you're looking at the right action
- **Handlers** — lists what IS there, ruling out "I thought I added it" and component-disabled/order issues
- **Fix** — specific, not generic

The description line only appears when `BiomataActions.json` is loaded. The diagnostic is fully self-contained when it is.

---

## Design decisions

**Why rewrite BiomataAgentEditor rather than extend it?**  
The new Action Coverage panel requires `CollectEffectiveCapabilities()` and `CanAgentSeeAction()` which need access to serialized properties. These helpers were easier to integrate cleanly in a full rewrite, and the original file was small enough (221 lines) that a rewrite stays reviewable.

**Why put validation notices at the top before editable fields?**  
A designer opening an agent to fix a problem sees the issue before scrolling. Validation notices after the fields require the user to know the problem exists before they look for it.

**Why hide Brain/Role fields in BindToExisting mode?**  
In `BindToExisting`, the backend owns capabilities and brain. Showing those fields implies they do something — they don't. Hidden fields are less confusing than greyed-out fields, and the mode HelpBox explains why. Users who switch to `CreateAtRuntime` and then back will see the values were preserved (SerializedFields retain their values).

**Why warn (not error) on missing handlers?**  
An agent prefab might be shared across scenes; some scenes only use a subset of actions. A warning respects that; an error would block workflows where missing handlers are intentional (e.g., a placeholder agent that always idles).

**Why use `DeclaredActionNames` for Inspector coverage, not `CanHandle()`?**  
`CanHandle()` requires a MonoBehaviour instance. The Inspector works on serialized objects without instantiation. `DeclaredActionNames`'s reflection fallback provides the same information without needing a live component. This is consistent with how `ActionManifestValidator` works.

**Why is the Description line conditional on the manifest being loaded?**  
The diagnostic is useful with or without the manifest. Rather than blocking the log when the manifest isn't found, we emit what we have and make the description an additive bonus.

---

## Known limitations

- **Action Coverage panel performance**: On every `OnInspectorGUI` call, `GetComponents<ActionHandlerBase>()` runs. This is a Unity Editor call and is fast for typical agent component counts (< 10 handlers). Not a production concern.
- **Gated actions calculated from Inspector capabilities only**: In `BindToExisting` mode, the backend agent's capabilities may differ from what's in the Inspector (backend is source of truth). The panel notes this limitation with a label.
- **`DeclaredActionNames` reflection is best-effort**: Custom handlers that don't follow the `HandledActions` convention and don't override `DeclaredActionNames` will appear as "no handler found" in the Inspector even if they work at runtime. Fix: override the property.
- **Manifest cache cleared on "Refresh" only**: If you re-export JSON during an edit session, click Refresh in the panel or restart the Editor. There is no file-watcher that auto-invalidates the cache.
- **Duplicate ID check scans only the open scene**: Duplicates in a closed additive scene will not be detected. Use Biomata > Validate Roles for a cross-prefab scan.
