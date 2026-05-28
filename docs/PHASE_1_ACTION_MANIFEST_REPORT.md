# Phase 1 — Action Manifest Report

> **Refactor goal**: Eliminate cross-runtime string parity fragility between Python and Unity by introducing a shared declarative action manifest as the source of truth.

---

## What was delivered

### New files

| File | Purpose |
|---|---|
| `simulation/actions.yaml` | Canonical action manifest — the single place to define action names, parameters, required capabilities, and Unity engine command types |
| `src/config/manifest.py` | Python loader: `ActionManifest.load()` + `build_registry()` + `export_json()` |
| `unity_sdk/Runtime/Resources/BiomataActions.json` | Generated JSON sidecar — Unity reads this; do not edit manually |
| `unity_sdk/Runtime/Integration/Actions/ActionManifestLoader.cs` | Unity runtime loader + optional `ValidateCoverage()` helper |
| `unity_sdk/Editor/ActionManifestValidator.cs` | **Biomata > Validate Action Manifest** menu item |
| `docs/architecture/action_manifest.md` | Full operational documentation |

### Modified files

| File | Change |
|---|---|
| `src/contracts/action.py` | Renamed `ActionSchema.tags` → `ActionSchema.required_capabilities`; `tags` kept as deprecated backwards-compat alias |
| `src/engine/registry.py` | Updated two `schema.tags` references to `schema.required_capabilities` |
| `src/config/loader.py` | Detects `manifest:` key in registry YAML config; loads and passes `ActionManifest` object to factory |
| `unity_sdk/Runtime/Integration/Actions/ActionHandlerBase.cs` | Added virtual `DeclaredActionNames` property with reflection-based default |

---

## What was NOT changed

- `ActionRegistry` — no changes to the runtime dispatch path
- `ActionHandler` protocol — no changes
- All existing `ActionHandler` implementations — no changes required
- All existing registry factory functions — continue to work unchanged
- `AgentRuntime` tick loop — untouched
- WebSocket protocol — untouched
- All four built-in Unity `ActionHandlerBase` subclasses (Move, Speak, Interact, Idle) — no code changes needed; the reflection-based `DeclaredActionNames` default works automatically for all of them

---

## Backwards compatibility

### Python

Existing code that constructs `ActionSchema` with `tags=`:

```python
# Before — still works exactly as before
ActionSchema("patrol", "Move to waypoint.", tags=frozenset({"guard"}))
```

`tags` is now a field that is always set to `required_capabilities` after `__post_init__`. Reading `schema.tags` returns the same value as `schema.required_capabilities`. No call site needs to change.

Existing registry factories:

```python
# Before — still works, receives no manifest kwarg
def build_registry(social=None):
    reg = ActionRegistry()
    reg.register(ActionSchema("idle", "Do nothing.", tags=frozenset()), IdleHandler())
    return reg
```

The loader's `_call_factory` uses `inspect.signature` to drop kwargs that the factory doesn't declare. A factory without `manifest=` in its signature receives nothing new.

### Unity

All four built-in `ActionHandlerBase` subclasses (Move, Speak, Interact, Idle) have `private static readonly HashSet<string> HandledActions`. The new `DeclaredActionNames` default property reflects on this field automatically. No C# changes required in any built-in handler.

Custom handlers that follow the same `HandledActions` field naming convention are detected automatically. Custom handlers that use different naming should override `DeclaredActionNames` explicitly — but the absence of an override does not break anything; it only means the editor validator cannot detect their coverage.

---

## How to use in a new project

### 1. Start with the manifest

Copy `simulation/actions.yaml`. Edit it to declare your simulation's actions.

### 2. Update your registry factory

```python
from src.config.manifest import ActionManifest

def build_registry(manifest: ActionManifest = None, social=None, **kwargs):
    if manifest:
        return manifest.build_registry({
            "idle":    IdleHandler(),
            "patrol":  PatrolHandler(),
            # only actions you have handlers for
        })
    # fallback: build manually as before
    ...
```

Point to it in `sim.yaml`:

```yaml
registry:
  class: myproject.registry.build_registry
  manifest: simulation/actions.yaml
```

### 3. Export JSON for Unity

```sh
python -c "
from src.config.manifest import ActionManifest
ActionManifest.load('simulation/actions.yaml') \
  .export_json('Assets/Resources/BiomataActions.json')
"
```

### 4. Validate in Unity

Add Unity `ActionHandlerBase` components to NPC prefabs, then run **Biomata > Validate Action Manifest** to confirm all manifest actions are covered.

---

## Migration guide for existing projects

Existing projects require **zero migration** for Python. The `tags=` keyword argument and `schema.tags` attribute both continue to work.

For Unity, place `BiomataActions.json` in a `Resources` folder to unlock the validator. Without it, nothing breaks — the validator simply reports "not found" and the runtime loader logs a warning if you call `ValidateCoverage`.

To migrate from `tags` to `required_capabilities` at your own pace:

```python
# Old (still valid)
ActionSchema("patrol", "...", tags=frozenset({"guard"}))

# New (preferred going forward)
ActionSchema("patrol", "...", required_capabilities=frozenset({"guard"}))
```

---

## Design decisions and tradeoffs

**Why YAML for the manifest, JSON for Unity?**  
YAML is more readable for humans editing the manifest. Unity does not have a built-in YAML parser for arbitrary files. JSON is native. The JSON is generated; humans never edit it.

**Why not put handler class paths in the manifest?**  
Adding `handler: myproject.handlers.PatrolHandler` to the YAML would couple the manifest to Python class paths, making it less portable and harder to refactor. Handlers are still registered in Python code. The manifest declares *what* an action is; Python code decides *how* to execute it.

**Why reflection for `DeclaredActionNames`?**  
All four built-in handlers have a `private static readonly HashSet<string> HandledActions` field. Reflection on this field works for all of them without requiring any code changes. It's a convention, not a contract — custom handlers can opt out by overriding the property. An attribute-based approach (`[BioamataHandles("patrol")]`) was considered but rejected as unnecessary abstraction for what is a one-time editor check.

**Why warn (not error) on unhandled manifest actions?**  
A simulation may declare actions in the manifest that not all projects implement — e.g., a shared manifest for a game family where `detain` is only used in one scenario. Erroring on unhandled actions would make the manifest unusable as a shared reference. The warning is actionable; the error would be over-constraining.

**Why keep `tags` as a live field instead of a property?**  
A `@property` would break pickling (snapshot serialization uses pickle). A field with `repr=False` that mirrors `required_capabilities` after `__post_init__` is the safest backwards-compatible approach for a `@dataclass`.

---

## Known limitations and follow-on work

- **No CLI command for export**: The JSON export is currently a Python one-liner. A `biomata manifest export` CLI command would be cleaner but is out of scope for Phase 1.
- **No watch mode**: Changes to `actions.yaml` require manual re-export. A file watcher or pre-commit hook is the recommended solution; not built here.
- **`engine_command` is documentary only**: Neither Python nor Unity validates that handlers produce commands with the declared type. A Phase 2 enhancement could add Unity-side validation of command shapes.
- **Editor validator is project-wide, not per-prefab**: The validator checks all handler types in the project against all manifest actions. It cannot tell you which specific prefabs are missing which handlers. `ActionManifestLoader.ValidateCoverage(executor)` fills this gap at runtime.
- **`parameters` not validated by Unity**: The JSON includes parameter specs but the Unity side does not parse or validate them. They are present for human reference only.
