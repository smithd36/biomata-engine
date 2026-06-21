# Survival sample — Needs + Stations

An opt-in battery for spatial survival/colony sims, built on the core SDK. Nothing here is
referenced by the engine; copy or adapt freely.

It combines two core pieces (`Biomata.Integration.Needs`) with two sample components:

| Piece | Where | Role |
|-------|-------|------|
| `NeedsComponent` / `Need` | core (`Runtime/.../Needs/`) | data-driven drives (hunger, energy, …) that decay over time |
| `NeedsObservationProvider` | core | publishes each need as `{key}`, `{key}_max`, `{key}_threshold`, `{key}_critical` |
| `Station` | this sample | a world object: a place + duration + need effects |
| `UseStationActionHandler` | this sample | walks the agent to a station, waits, applies its effects |

## Wiring a survival agent

On the agent GameObject (alongside `BiomataAgent`):

1. **`NeedsComponent`** — add your needs as data, e.g.
   `hunger` (value 50, max 100, decayPerSecond -1 so it *grows*, threshold 80, actWhenAbove ✓),
   `energy` (value 80, decayPerSecond 0.5, threshold 20, actWhenAbove ✗).
2. **`NeedsObservationProvider`** — auto-publishes those needs to the brain each tick.
3. **`NavMeshMoveActionHandler`** (recommended) — `UseStationActionHandler` reuses it to walk.
   Without it, the handler does a simple straight-line move.
4. **`UseStationActionHandler`** — set its `verbs` to the actions your stations serve
   (`eat`, `sleep`, `work`, …).

## Wiring a station

On a world object:

1. Tag it `BiomataPOI` (or match `UseStationActionHandler.stationTag`).
2. Add **`Station`**: set `duration` and `effects` (e.g. `hunger: -40` for food,
   `energy: +60` for a bed). Use `deactivateOnUse` for consumables.

## Backend contract

Declare the verbs in `simulation/actions.yaml` (and re-export `BiomataActions.json`) so the
brain knows it can `eat`/`sleep`/etc. The brain targets a station by name via either a
`navigate` engine command (`destination`) or a `target`/`station` action parameter — the
handler resolves both.
