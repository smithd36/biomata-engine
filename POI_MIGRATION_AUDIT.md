# Biomata POI v1 → v2 Migration — Architectural Audit

**Date:** 2026-06-06  
**Scope:** Phases 1–4 — `src/contracts/world.py`, `src/contracts/action.py`, `BiomataPOIData.cs`, `POIObservationProvider.cs`, `MoveActionHandler.cs`  
**Auditor:** graphify /review

---

## 1. Executive Summary

**Migration status: CONDITIONALLY SAFE — one critical invariant violated.**

The migration is structurally sound and backward-compatible with v1 agents. The Phase 4 portal system introduces no hidden graph. However, **Phase 3 introduced a dual authority violation**: both Python (`MoveAction.to_navigate_command`) and Unity (`MoveActionHandler.ExtractTarget` Path 3) independently resolve POI anchors → world coordinates, from different data sources, with which one runs determined by ephemeral runtime state. This breaks the "single source of truth" invariant and produces non-deterministic navigation targets across ticks.

**Main risk category:** Dual authority / inconsistent resolution source for anchor → coordinate mapping.

---

## 2. Critical Issues

### C1 — Dual anchor resolution authority  
**Files:** `src/contracts/action.py:286–299`, `MoveActionHandler.cs:201–216`  
**Severity:** Critical — violates the single source of truth invariant

Both Python and Unity independently resolve the same semantic operation (POI anchor → world coordinates):

**Python path** (`action.py:286–299`):
```python
# Path 2 in MoveAction.to_navigate_command()
if self.poi_id is not None:
    if poi_lookup:
        poi = poi_lookup.get(self.poi_id)
        if poi is not None:
            coords = resolve_poi_target(poi, anchor=self.anchor)
            if coords:
                return {"type": "navigate",
                        "x": coords[0], "y": coords[1], "z": coords[2]}
```
When `poi_lookup` is present and contains the target POI, Python resolves the anchor using **observation snapshot data** and emits explicit world coordinates.

**Unity path** (`MoveActionHandler.cs:212–215`):
```csharp
// Path 3 in ExtractTarget()
TryGetStr(cmd, "anchor", out var anchorName);
var poiData     = t.GetComponent<BiomataPOIData>();
var anchorWorld = poiData?.GetWorldAnchor(anchorName ?? "approach");
return anchorWorld ?? t.position;
```
When the command arrives with a `destination` string, Unity resolves the anchor from the **live Transform** at execution time.

**The problem:** When Python's `poi_lookup` is populated and the POI is found, Unity receives a Path 1 command (explicit `x/y/z`). Unity's Path 3 anchor resolution is entirely bypassed — it never runs. When `poi_lookup` is absent or the POI is not in it, Unity falls through to Path 3 and resolves from the live Transform.

The same brain intent (`move to approach anchor of POI X`) produces coordinates from different data sources depending on whether the POI was observed that tick. This is non-deterministic from the brain's perspective.

**Snapshot vs live discrepancy:** Python's coordinates come from the observation (a snapshot taken at tick start). Unity's coordinates come from the live Transform (current world state at command execution). For moving POIs — or POIs in scenes where the authoritative position may change between observe and act — these diverge.

---

### C2 — `poi_lookup` silently determines which resolution authority runs  
**File:** `src/contracts/action.py:287–299`  
**Severity:** Critical — hidden behavioral switch

The path taken by `to_navigate_command()` is a silent branch on `bool(poi_lookup)`:

- **`poi_lookup` present, POI found** → Python resolves anchor → Unity executes explicit coords (snapshot-based)  
- **`poi_lookup` absent OR POI not found** → Python emits `destination + anchor` → Unity resolves anchor → Unity executes (live Transform-based)

There is no indication at the call site or in the returned command which resolution path was taken. The brain cannot distinguish between these two behaviors. A caller that sometimes passes `poi_lookup` and sometimes doesn't will get inconsistent coordinate sources across ticks for the same intent.

---

## 3. Architectural Concerns (non-blocking)

### A1 — `ExtractPortal` duplicates Path 3 parsing from `ExtractTarget`  
**File:** `MoveActionHandler.cs:154–170` vs `MoveActionHandler.cs:201–222`

Both methods independently:
1. Iterate `decision.EngineCommands`
2. Filter for `type == "navigate"`
3. Read the `destination` string
4. Look up in `_poiCache`

If Path 3 command structure changes (new key names, format changes), `ExtractPortal` will not automatically update. These two parse loops must be kept in sync manually and there is no mechanism enforcing this.

### A2 — `anchor` key suppressed when value equals the default  
**File:** `src/contracts/action.py:297`

```python
if self.anchor != "approach":
    cmd["anchor"] = self.anchor
```

When `anchor == "approach"` (the default), no `anchor` key is emitted. Unity defaults to `"approach"` when the key is absent. Behaviorally consistent, but the command structure differs between explicit-`"approach"` and default-`"approach"` callers. Any future consumer that distinguishes "key absent" from "key present with value approach" would break silently.

### A3 — Portal destination lookup depends on `Awake()` cache  
**File:** `MoveActionHandler.cs:123–130`

`TryPortalTransition` looks up `portal.ConnectsTo` in `_poiCache`, which is built once in `Awake()`. If the destination POI is spawned after `Awake()`, the portal silently fails with a logged warning. The `RefreshPOICache()` mitigation exists but must be called explicitly by the integrator. This is a deployment hazard for dynamically-spawned portal destinations, not a current bug.

### A4 — `_ensure_3d` assumes 2-element lists are `[x, z]` not `[x, y]`  
**File:** `src/contracts/world.py:108–112`

```python
def _ensure_3d(coords: list) -> list[float]:
    if len(coords) == 2:
        return [float(coords[0]), 0.0, float(coords[1])]
```

A 2-element coordinate is padded as `[x, 0.0, z]`, interpreting the second element as `z`. This is correct for v1 `[x, z]` pairs but would incorrectly interpret `[x, y]` pairs (height + something) as `[x=x, y=0, z=y]`. Unity's `POIObservationProvider` always emits 3-element `[x, y, z]` arrays, so this only affects manually-constructed Python `POI` objects. Documents the implicit assumption that 2-element coords are always `[x, z]`.

---

## 4. Compatibility Assessment

| Agent type | Status | Reasoning |
|---|---|---|
| **v1 agents** (position-only, `destination` string, `x/z` keys) | **SAFE** | `x/z` flat keys preserved as tertiary fallback in `resolve_poi_target()`. `destination` string behavior in Unity Path 3 unchanged. `MoveAction.from_intent()` returns `poi_id=None` for old brain output → Path 3 runs as before. No v1 observation field removed or reinterpreted. |
| **v2 agents** (using `poi_id` + `anchor`) | **PARTIAL** | Correct when `poi_lookup` is absent (Unity resolves from live Transform). Introduces snapshot/live divergence when `poi_lookup` is present. Which behavior runs is not visible to the brain or caller. |
| **v2 portal agents** (using `poi_id` → portal POI) | **PARTIAL** | Portal transition executes correctly after arrival. No portal chaining occurs (single-hop only, confirmed). Destination POI cache miss is handled with a warning. The dual-authority issue from C1/C2 applies here too: if the portal POI is in `poi_lookup`, Python emits explicit coords to the portal's approach anchor; `ExtractPortal` then re-parses the `destination` key — but since Python emitted explicit coords (Path 1 command), `ExtractPortal` finds no `destination` key and returns `null`, **silently skipping the portal transition entirely**. |

### Supplemental finding for portal agents (v2)

`ExtractPortal` (`MoveActionHandler.cs:154–170`) only triggers when the navigate command has a `destination` string key. If Python's `to_navigate_command()` resolved the anchor and emitted `{"type":"navigate","x":...,"y":...,"z":...}` (Path 1 command — no `destination` key), `ExtractPortal` returns `null` and the portal transition is **never triggered**. This means:

- A brain using `poi_id` pointing to a portal POI, with `poi_lookup` available, will arrive at the approach anchor of the portal but **not cross it**.
- The same brain without `poi_lookup` will correctly trigger the portal transition.

This is a latent bug in the v2 portal flow, dependent on `poi_lookup` availability.

---

## 5. Suggested Minimal Fixes

These are the minimum changes required to restore correctness. No redesign.

### Fix for C1, C2, and the portal latent bug

**Problem root cause:** `MoveAction.to_navigate_command()` Path 2 resolves anchor → explicit coordinates when `poi_lookup` is present. This is the wrong thing for it to do: Python should resolve intent (which POI, which anchor) but not world coordinates. World coordinate resolution belongs to Unity.

**Minimal fix** (`src/contracts/action.py`): Remove Python-side anchor resolution from Path 2. Always emit `destination + anchor` when working from a `poi_id`, regardless of whether `poi_lookup` is present.

Before (lines 287–298):
```python
if self.poi_id is not None:
    if poi_lookup:
        poi = poi_lookup.get(self.poi_id)
        if poi is not None:
            from src.contracts.world import resolve_poi_target
            coords = resolve_poi_target(poi, anchor=self.anchor)
            if coords:
                return {"type": "navigate",
                        "x": coords[0], "y": coords[1], "z": coords[2]}
    cmd: dict[str, Any] = {"type": "navigate", "destination": self.poi_id}
    if self.anchor != "approach":
        cmd["anchor"] = self.anchor
    return cmd
```

After:
```python
if self.poi_id is not None:
    cmd: dict[str, Any] = {"type": "navigate", "destination": self.poi_id}
    if self.anchor != "approach":
        cmd["anchor"] = self.anchor
    return cmd
```

This makes Path 2 always emit `destination + anchor`, delegating coordinate resolution to Unity Path 3 exclusively. The `poi_lookup` parameter and `resolve_poi_target` import become unused in this method and can be removed. This restores single source of truth, fixes the portal latent bug, and removes the non-deterministic resolution split.

**Scope impact:** The `poi_lookup` parameter on `to_navigate_command()` becomes dead code and should be removed from the signature to prevent future callers from passing it under the mistaken belief it is used.

### Fix for A1 (architectural concern, not critical)

Extract the shared parse logic from `ExtractTarget` Path 3 and `ExtractPortal` into a private helper:

```csharp
private BiomataPOIData? ResolveDestinationPOI(AgentDecisionResult decision, out string anchorName)
{
    anchorName = null;
    foreach (var cmd in decision.EngineCommands)
    {
        if (!TryGetStr(cmd, "type", out var type) || type != "navigate") continue;
        if (!TryGetStr(cmd, "destination", out var dest)) continue;
        var key = dest.ToLowerInvariant();
        if (_poiCache != null && _poiCache.TryGetValue(key, out var t))
        {
            TryGetStr(cmd, "anchor", out anchorName);
            return t.GetComponent<BiomataPOIData>();
        }
    }
    return null;
}
```

Then both `ExtractTarget` Path 3 and `ExtractPortal` call `ResolveDestinationPOI`. Ensures they always stay in sync. This is a refactor, not a behavior change — apply only if C1 fix is also applied, to avoid two separate changes to the same method on the same ticket.

---

## Schema Consistency Verification

| Field | Python format | Unity emission | Python read | Status |
|---|---|---|---|---|
| `x`, `z` (v1) | `poi["x"]`, `poi["z"]` | `entry["x"] = (double)poi.position.x` | `poi.get("x")` | ✓ |
| `position` (v2) | `list[float]` len 3 | `new double[] {x, y, z}` | `isinstance(pos, (list, tuple))` | ✓ |
| `anchors` (v2) | `dict[str, list[float]]` | `Dictionary<string, object>` with `double[]` values | `isinstance(anchor_pos, (list, tuple))` | ✓ |
| `traversal.is_portal` | `bool` | `true` (Python bool from C# `true`) | `raw.get("is_portal")` | ✓ |
| `traversal.connects_to` | `str \| None` | `string` | `raw.get("connects_to")` | ✓ |
| Navigate command `x/y/z` | `float` | read as `float` via `TryGetFloat` | — | ✓ |

No schema drift detected. All coordinate arrays are consistently 3-element `[x, y, z]` in Unity observation emission. The 2-element fallback in `_ensure_3d` is only reachable from manually-constructed Python POI objects.

## Hidden Graph Check

Phase 4 portal traversal does **not** introduce implicit world graph behavior:
- `TryPortalTransition` executes once per `ExecuteCoroutine` call — no loop, no recursion
- `ExtractPortal` reads one `destination` key from one command — no chaining
- The destination POI's `IsPortal` flag is **never** read during transition — no portal-to-portal chaining possible by construction
- `POITraversal.connects_to` is a single string, not a list — structural limit on connectivity
- No cross-POI coupling exists beyond the `connectsTo` metadata field

Phase 4 is correctly scoped to single-hop spatial transitions.
