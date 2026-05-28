# Action Manifest

> **Phase 1 of the Biomata architecture consolidation.**  
> Introduced in commit following `ca684cb`.

---

## What it is

`simulation/actions.yaml` is the canonical, human-readable declaration of every action in a simulation. It is the single place where an action's name, description, parameters, required capabilities, and Unity engine command type are defined.

Before this existed, those facts lived in three separate locations with no enforced consistency:

| Fact | Old location |
|---|---|
| Action name | Python `ActionSchema.name` AND Unity `ActionHandlerBase.CanHandle()` string |
| Parameters | Python `ActionSchema.parameters_schema` |
| Required capabilities | Python `ActionSchema.tags` |
| Engine command type | Convention in Python `ActionResult.commands` |

A typo between Python and Unity produced a silent failure. There was no single file to read to understand what a simulation could do.

## What it is NOT

- Not a replacement for Python `ActionHandler` implementations — logic still lives in code
- Not a runtime enforcement mechanism — the engine still validates intent against capability at dispatch time
- Not a replacement for manual `ActionRegistry` factories — those continue to work unchanged
- Not Unity-authoritative — Python is authoritative; Unity reads a JSON sidecar exported from the YAML

---

## Files

| File | Purpose |
|---|---|
| `simulation/actions.yaml` | Source of truth — edit this |
| `src/config/manifest.py` | Python loader: `ActionManifest.load()` |
| `unity_sdk/Runtime/Resources/BiomataActions.json` | Generated sidecar — do not edit manually |
| `unity_sdk/Runtime/Integration/Actions/ActionManifestLoader.cs` | Unity runtime loader |
| `unity_sdk/Editor/ActionManifestValidator.cs` | Unity editor validator menu item |

---

## Manifest format

```yaml
version: "1"

actions:
  - name: patrol
    description: Move to a named waypoint on the patrol route.
    parameters:
      waypoint: str
    required_capabilities: [guard, patrol]
    engine_command: navigate
    kind: host
```

### Field reference

| Field | Type | Required | Default |
|---|---|---|---|
| `name` | string | yes | — |
| `description` | string | yes | — |
| `parameters` | mapping (str→type-spec) | no | `{}` |
| `required_capabilities` | list of strings | no | `[]` (universal) |
| `engine_command` | string | no | — (documentary only) |
| `kind` | `host` \| `engine` \| `hybrid` | no | `hybrid` |

**Parameter type specs**: `str`, `int`, `float`, `bool`, or any string. Append `?` to mark optional (e.g. `float?`). Descriptive strings like `"str — agent id to target"` are also valid and get passed through to the LLM prompt as-is.

**`required_capabilities`**: Empty list or absent means the action is universal — all agents see it. Non-empty means an agent must have at least one matching capability string in `Agent.capabilities` to see or execute the action.

**`engine_command`**: Purely documentary. It records what `type` value the Python `ActionHandler` puts in `ActionResult.engine_commands`, so Unity developers know what to look for in `decision.EngineCommands`. The engine does not read or validate this field at runtime.

---

## Ownership boundaries

```
simulation/actions.yaml        ← human edits happen here
        │
        │  ActionManifest.load()
        ▼
Python ActionManifest           ← vends ActionSchema objects
        │
        │  manifest.build_registry({name: handler, ...})
        ▼
Python ActionRegistry           ← runtime action dispatch (unchanged)
        │
        │  manifest.export_json(path)
        ▼
BiomataActions.json             ← Unity reads this (do not edit manually)
        │
        │  ActionManifestLoader.Load()
        ▼
Unity editor validator          ← checks ActionHandlerBase coverage
Unity runtime loader            ← optional runtime coverage checks
```

Python owns the manifest. Unity reads an exported snapshot. The export must be re-run whenever `actions.yaml` changes.

---

## Python integration

### Loading the manifest

```python
from src.config.manifest import ActionManifest

manifest = ActionManifest.load("simulation/actions.yaml")
```

Raises `ManifestValidationError` on:
- Missing `actions` key
- Duplicate action names
- Invalid `kind` value

Raises `FileNotFoundError` if the path doesn't exist.

### Building a registry

```python
from myproject.handlers import PatrolHandler, AlertHandler, IdleHandler

registry = manifest.build_registry({
    "idle":    IdleHandler(),
    "patrol":  PatrolHandler(),
    "alert":   AlertHandler(),
})
```

Rules:
- Actions in manifest with a matching handler → registered
- Actions in manifest without a handler → skipped with a `WARNING` log
- Handler names NOT in manifest → `ManifestValidationError` (catches typos at startup)

### Manual registration (existing code, unchanged)

```python
registry = ActionRegistry()
registry.register(manifest.schema("patrol"), PatrolHandler())
registry.register(manifest.schema("alert"),  AlertHandler())
```

Existing code that builds `ActionSchema` objects manually and calls `registry.register()` works exactly as before.

### Via sim.yaml

```yaml
registry:
  class: myproject.registry.build_registry
  manifest: simulation/actions.yaml
```

The config loader detects `manifest:`, loads it, and passes `manifest=ActionManifest(...)` as a kwarg to the factory function. The factory declares `manifest=None` in its signature to opt in:

```python
def build_registry(manifest=None, social=None, **kwargs):
    if manifest:
        return manifest.build_registry({
            "idle":    IdleHandler(),
            "patrol":  PatrolHandler(),
        })
    # fallback to manual construction
    registry = ActionRegistry()
    registry.register(ActionSchema("idle", "Do nothing."), IdleHandler())
    return registry
```

Factories that don't declare `manifest` in their signature receive nothing — the loader uses `inspect.signature` filtering, so the manifest kwarg is silently dropped. Zero breaking change for existing factories.

### Exporting JSON for Unity

```python
manifest.export_json("unity_sdk/Runtime/Resources/BiomataActions.json")
```

Or as a one-liner from the terminal:

```sh
python -c "
from src.config.manifest import ActionManifest
ActionManifest.load('simulation/actions.yaml') \
  .export_json('unity_sdk/Runtime/Resources/BiomataActions.json')
"
```

Run this after any change to `actions.yaml`. Commit the generated JSON alongside your Unity project.

---

## Unity integration

### How Unity reads the manifest

`ActionManifestLoader.Load()` calls `Resources.Load<TextAsset>("BiomataActions")`. The JSON file must be in any `Resources` folder in the project. The most common location is `Assets/Resources/BiomataActions.json`.

### Validating handler coverage (editor)

**Biomata > Validate Action Manifest**

The editor menu item (`ActionManifestValidator.cs`):
1. Loads `BiomataActions.json` from Resources
2. Scans all non-abstract `ActionHandlerBase` subclasses in the project using `TypeCache`
3. For each type, reflects on the conventional `static HandledActions` field to get covered action names
4. Logs a warning for every manifest action with no covering handler

Output example:
```
[Biomata] Manifest v1 — 4 covered, 2 uncovered
  ✓  idle         handled by IdleActionHandler
  ✓  move         handled by MoveActionHandler
  ✓  speak        handled by SpeakActionHandler
  ✓  interact     handled by InteractActionHandler
  ✗  patrol       no handler found
  ✗  alert        no handler found
```

### Validating handler coverage (runtime)

Call `ActionManifestLoader.ValidateCoverage(executor)` from a MonoBehaviour's `Awake` or `Start`:

```csharp
void Start()
{
    var executor = GetComponent<ActionExecutor>();
    ActionManifestLoader.ValidateCoverage(executor);
}
```

This checks only the handlers on the same GameObject, which is useful for debugging agent-specific coverage at runtime.

### Convention for custom handlers

The validator discovers action names by reflecting on the `static HandledActions` field — the same field all built-in handlers use:

```csharp
private static readonly HashSet<string> HandledActions = new() { "patrol", "navigate" };
```

Custom handlers that use this convention are automatically detected. Handlers that use a different pattern will not be detected by the editor validator. Override `DeclaredActionNames` to declare names explicitly without relying on the convention:

```csharp
public class PatrolActionHandler : ActionHandlerBase
{
    private static readonly HashSet<string> HandledActions = new() { "patrol" };

    // Optional explicit override — makes the validator more robust
    public override IReadOnlyCollection<string> DeclaredActionNames => HandledActions;

    public override bool CanHandle(string action) =>
        HandledActions.Contains(action?.ToLowerInvariant());
    // ...
}
```

---

## Runtime sequence

```
Startup:
  ActionManifest.load("simulation/actions.yaml")
  → validate YAML structure
  → build dict[name → ActionSchema]

Registry construction:
  manifest.build_registry({"patrol": PatrolHandler(), ...})
  → validate handler names against manifest
  → call ActionRegistry.register(schema, handler) for each

Per tick (unchanged):
  registry.schemas_for(agent.required_capabilities)
  → filters by required_capabilities (was: tags)
  → passed to brain.decide() as available_actions

  registry.validate_intent(intent, capabilities)
  → checks name, capability gate, parameters

  registry.dispatch(intent, agent, world)
  → handler.execute() → ActionResult
```

Nothing in the per-tick hot path changed. The manifest is a startup concern only.

---

## `ActionSchema.tags` rename

`ActionSchema.tags` has been renamed to `ActionSchema.required_capabilities`.

**Backwards compatibility**: Code that passes `tags=` as a keyword argument continues to work. `tags` is kept as a deprecated field that mirrors `required_capabilities` after construction:

```python
# Old — still works
schema = ActionSchema("patrol", "Move to waypoint.", tags=frozenset({"guard"}))
assert schema.required_capabilities == frozenset({"guard"})
assert schema.tags == frozenset({"guard"})  # reads .required_capabilities

# New — preferred
schema = ActionSchema("patrol", "Move to waypoint.", required_capabilities=frozenset({"guard"}))
```

`ActionSchema.tags` is now a field that is always set to the value of `required_capabilities` after `__post_init__`. Reading `schema.tags` returns the same value as `schema.required_capabilities`. Writing `tags=` at construction time migrates to `required_capabilities`. Both names work for the foreseeable future.

---

## Adding a new action

1. Add an entry to `simulation/actions.yaml`
2. Write a Python `ActionHandler` subclass
3. Register it in your registry factory
4. Re-export `BiomataActions.json` 
5. Write or extend a Unity `ActionHandlerBase` subclass with the action name in `HandledActions`
6. Add the handler component to NPC prefabs that need it
7. Run **Biomata > Validate Action Manifest** to confirm coverage

---

## Common mistakes

**Manifest action name ≠ handler string**: If `actions.yaml` says `name: patrol` but the Unity handler has `HandledActions = new() { "Patrol" }` (capital P), the validator will warn. Handler strings are lowercased during comparison — keep all action names lowercase.

**Forgetting to export JSON**: Editing `actions.yaml` without re-running `export_json` means Unity sees a stale manifest. The validator will show mismatched results. Add the export command to your project's build script or pre-commit hook.

**Missing `manifest:` in factory signature**: If your factory function doesn't declare `manifest=None` in its signature, the loader's `_call_factory` drops the kwarg silently. The factory still runs — it just won't receive the manifest. Check the signature if the manifest doesn't arrive.

**Handler without `HandledActions` field**: Custom handlers that use a different static field name, or that determine supported actions dynamically, won't be found by the editor validator. Override `DeclaredActionNames` explicitly.

---

## Known limitations

- Parameter type-checking in the manifest validates Python types only. Unity does not parse or validate the `parameters` field at runtime.
- The `engine_command` field is documentary — neither Python nor Unity validates that handlers produce commands with the declared type.
- The editor validator uses static reflection and cannot discover dynamically-determined action sets (e.g., a handler that reads actions from a ScriptableObject at runtime).
- No incremental validation — the validator checks all manifest actions against all project handler types; there is no per-prefab check in the editor (only at runtime via `ValidateCoverage`).
