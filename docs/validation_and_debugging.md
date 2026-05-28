# Unity Validation & Debugging

This document covers the complete validation surface in the Biomata Unity SDK: what is checked, when it runs, and how to interpret and act on each diagnostic.

---

## Validation surface overview

| Check | When | Where |
|---|---|---|
| Empty Agent ID | Edit time (OnValidate + Inspector) | BiomataAgent Inspector |
| Duplicate Agent ID in scene | Edit time (Inspector) | BiomataAgent Inspector |
| CreateAtRuntime: no brain and no role | Edit time (Inspector) | BiomataAgent Inspector |
| Unknown role name | Edit time (Inspector) | BiomataAgent Inspector |
| Missing action handler (visible actions) | Edit time (Inspector) | BiomataAgent Inspector |
| Full project handler coverage | On demand | Biomata > Validate Action Manifest |
| Full project role coverage | On demand | Biomata > Validate Roles |
| Per-agent handler coverage at runtime | Runtime (optional) | ActionManifestLoader.ValidateCoverage() |
| No matching handler at runtime | Runtime (automatic) | ActionExecutor diagnostic log |

---

## BiomataAgent Inspector

The custom inspector for `BiomataAgent` surfaces validation issues as coloured HelpBox strips at the top of the Inspector, above all editable fields.

### Ownership modes

**BindToExisting** shows: Identity, Lifecycle, Action Coverage.  
**CreateAtRuntime** shows: Identity, Role, Capabilities, Brain, Lifecycle, Action Coverage.

Fields not relevant to the active mode are hidden entirely — there are no greyed-out inputs.

### Validation strip reference

| Strip colour | Condition | Fix |
|---|---|---|
| Yellow (Warning) | Agent ID is empty | Set a unique ID, or leave blank to auto-generate |
| Red (Error) | Duplicate ID found in scene | Rename one of the conflicting agents |
| Yellow (Warning) | CreateAtRuntime with no brain and no role | Add a Brain Class or assign a Role |
| Red (Error) | Role name not in BiomataRoles.json | Fix typo or re-export manifest |
| Yellow (Warning) | One or more visible actions have no handler | Add matching ActionHandlerBase components |

The role validation and handler-gap warnings appear only when the respective JSON manifests are loaded (see [Manifest loading](#manifest-loading)).

---

## Action Coverage panel

Every `BiomataAgent` Inspector has an **Action Coverage** foldout below the Lifecycle section.

It shows three groups:

### Visible actions

Actions the agent can see, based on its resolved capabilities (Inspector fields + role). For each visible action:

- **✓ green** — the action has a covering handler component on this GameObject
- **✗ red** — no handler found; the agent would emit a runtime warning

Example:
```
Visible to this agent (5):
✓  idle          IdleActionHandler
✓  move          MoveActionHandler
✓  speak         SpeakActionHandler
✓  interact      InteractActionHandler
✗  patrol        no handler
```

### Gated actions

Actions the agent cannot see because it lacks the required capabilities. Collapsed by default; expand to see which capabilities would unlock them.

```
Gated — capability not held (3)
  ○  patrol   needs: guard, patrol
  ○  alert    needs: guard, authority
  ○  trade    needs: trade, merchant
```

### Summary strip

- If all visible actions have handlers: green info box.
- If any visible action is missing a handler: yellow warning box with the count.

### Buttons

- **Refresh** — clears the manifest cache and reloads (useful after re-exporting JSON).
- **Validate Scene** — runs `ActionManifestValidator.Validate()` for a full project report (same as Biomata > Validate Action Manifest).

---

## Editor menu validators

### Biomata > Validate Action Manifest

Scans every `ActionHandlerBase` subclass in the project and checks which action names each covers. Reports per-action handler coverage against `BiomataActions.json`.

Output (Unity Console):
```
[Biomata] Manifest v1 — 5 covered, 1 uncovered
  ✓  idle         handled by IdleActionHandler
  ✓  move         handled by MoveActionHandler
  ✓  speak        handled by SpeakActionHandler
  ✓  interact     handled by InteractActionHandler
  ✓  patrol       handled by PatrolActionHandler
  ✗  alert        no handler found
```

Coverage is determined by reflecting on the `static HandledActions` field in each handler class. Custom handlers that use a different field name should override `DeclaredActionNames` (see [Custom handlers](#custom-handlers)).

### Biomata > Validate Roles

Scans all `BiomataAgent` components in open scenes and all prefabs. Checks that every non-empty `role` field matches a name declared in `BiomataRoles.json`.

Output:
```
[Biomata] BiomataRoles v1 — 3 declared, 4 valid, 1 invalid
  ✓  scene/NPC_Guard/Guard: role 'Guard'
  ✓  prefab/Villager/Villager: role 'Villager'
  ✗  prefab/Merchant_OLD/Merchant: role 'Tradesperson' not in manifest
```

---

## Runtime diagnostics

### ActionExecutor: no handler found

When a backend decision arrives for an action that has no covering handler on the agent's GameObject, `ActionExecutor` logs a structured warning at `Debug.LogWarning` level:

```
[Biomata] No handler for action 'patrol'
  Agent:       guard_001  ("Aldric")
  Description: Move to a named waypoint on the patrol route.
  Handlers:    IdleActionHandler, MoveActionHandler, SpeakActionHandler
  Fix:         Add a component that extends ActionHandlerBase and returns true for CanHandle("patrol").
```

The warning context object is the `UnityAgentBridge` component, so clicking the message in the Console selects the agent GameObject in the Hierarchy.

Prior to Phase 3, the message was:
```
[Biomata] No handler for action 'patrol' on agent 'guard_001'. Add a matching ActionHandlerBase component.
```

The new message adds: agent name, action description (from the manifest), the list of handlers that ARE present (to rule out component-order or disabled-component issues), and a precise fix instruction.

### BiomataAgent: unknown role at Awake

If a role name is set but not found in `BiomataRoles.json` at runtime:
```
[BiomataAgent] 'Guard_NPC': Role 'Paladin' not found in BiomataRoles.json.
Regenerate the JSON after editing the roles: block in sim.yaml.
```

### Optional: per-agent coverage at Start

Call `ActionManifestLoader.ValidateCoverage(executor)` from any MonoBehaviour's `Start` method to get a per-agent runtime coverage check:

```csharp
void Start()
{
    ActionManifestLoader.ValidateCoverage(GetComponent<ActionExecutor>());
}
```

This is redundant with the Inspector panel in edit mode, but useful for dynamically spawned agents that weren't visible during editing.

---

## Manifest loading

Both `ActionManifestLoader` and `RoleManifestLoader` use `Resources.Load<TextAsset>` with a cached result. The files must be in any `Resources` folder in your Unity project.

| File | Resource name | Expected path |
|---|---|---|
| `BiomataActions.json` | `"BiomataActions"` | `Assets/Resources/BiomataActions.json` |
| `BiomataRoles.json` | `"BiomataRoles"` | `Assets/Resources/BiomataRoles.json` |

If either file is absent, the corresponding validators and inspector panels show an informational notice rather than erroring.

**To regenerate after editing sim.yaml:**

```sh
# Actions
python -c "
from src.config.manifest import ActionManifest
ActionManifest.load('simulation/actions.yaml') \
  .export_json('Assets/Resources/BiomataActions.json')
"

# Roles
python -c "
from src.config.schema import SimConfig
from src.config.roles import export_roles_json
import yaml
cfg = SimConfig.model_validate(yaml.safe_load(open('sim.yaml')))
export_roles_json(cfg.roles, 'Assets/Resources/BiomataRoles.json')
"
```

---

## Custom handlers

For the manifest validator to detect your custom handler, it must have a static field named `HandledActions`:

```csharp
[AddComponentMenu("Biomata/Actions/Patrol")]
public class PatrolActionHandler : ActionHandlerBase
{
    private static readonly HashSet<string> HandledActions = new() { "patrol" };

    public override bool CanHandle(string action) =>
        HandledActions.Contains(action?.ToLowerInvariant());

    // ExecuteCoroutine ...
}
```

If you use a different naming convention, override `DeclaredActionNames`:

```csharp
public override IReadOnlyCollection<string> DeclaredActionNames
    => new[] { "patrol" };
```

The `DeclaredActionNames` property is also used by the Inspector's Action Coverage panel to match handlers to manifest entries.

---

## Debugging common scenarios

### The agent always idles — I expected it to patrol

1. Check **Action Coverage** in the Inspector for the agent. Is `patrol` showing ✗ (red)?
   - If yes: add `PatrolActionHandler` to the agent GameObject.
2. Is `patrol` in the **Gated** section?
   - If yes: the agent lacks the required capabilities (`guard`, `patrol`). Add them in the Inspector or assign the Guard role.
3. Is `patrol` not listed at all?
   - The action is not in `BiomataActions.json`. Add it to `simulation/actions.yaml` and re-export.
4. Check the backend: is the agent registered with the right capabilities? In `CreateAtRuntime` mode, capabilities must be set in the Inspector (or come from the role) and sent during registration.

### The Inspector shows a role validation error

`BiomataRoles.json` either doesn't exist or doesn't contain the role name. Two possible causes:
- You added the role to `sim.yaml` but haven't regenerated the JSON: run the export command above.
- The role name in the Inspector has a typo: compare against the keys in `sim.yaml`'s `roles:` block.

### "Duplicate Agent ID" error in the Inspector

Two `BiomataAgent` components in the scene have the same `Agent ID`. Each must be unique within the simulation. If you want deterministic IDs, ensure each prefab instance has a distinct value. If you leave them blank, IDs are auto-generated from the GameObject name and instance ID.

### The validator says a handler covers an action but the action still logs "no handler found"

The manifest validator uses `DeclaredActionNames` (via reflection on `HandledActions`). The runtime uses `CanHandle()`. If these disagree (e.g., `HandledActions` has `"patrol"` but `CanHandle` doesn't), the validator shows coverage but the runtime fails. Keep `CanHandle` and `HandledActions` in sync — they should both check the same set.

### BiomataActions.json is out of date

Click **Refresh** in the Action Coverage panel, or call `ActionManifestLoader.ClearCache()` in code. Then re-run the Python export if the YAML has changed.
