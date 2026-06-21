# Biomata Engine — Portability & Reusability Recommendations

> **Audience:** a fresh Claude Code session working in `biomata-engine/`.
> **Author context:** these notes come from a session spent building a survival/colony
> simulation on top of the Unity SDK (a separate consumer project, "ColonyDemo"). The
> recommendations are grounded in friction and bugs hit while making a *new* simulation
> with the engine — i.e. exactly the reusability path you want to smooth.
> **Goal:** make `biomata-engine` cleanly reusable across *different* simulations
> (not just the bundled demo) with minimal per-sim ceremony.

---

## 0. Orientation (what is where)

- **Engine / Unity SDK:** `unity_sdk/Runtime/Integration/**` (asmdef: `BiomataIntegration`).
  Key areas:
  - `Actions/` — `ActionHandlerBase`, `ActionExecutor`(in `Agents/`), `MoveActionHandler`,
    `NavMeshMoveActionHandler`, `SpeakActionHandler`, `InteractActionHandler`,
    `IdleActionHandler`, `ActionManifestLoader`.
  - `Agents/` — `UnityAgentBridge`, `BiomataAgent`, `ObservationCollector`.
  - `Observations/` — `ObservationProviderBase`, `NearbyObjectsObservationProvider`,
    `POIObservationProvider`, `NearbyAgentsObservationProvider`, `TransformObservationProvider`.
  - `Simulation/` — `UnitySimulationManager`, `BiomataSimulationBootstrapper`,
    `RoleManifestLoader`, `BiomataSimulationConfig`.
- **Action manifest (the cross-language contract):** `simulation/actions.yaml`
  → exported to `unity_sdk/Runtime/Resources/BiomataActions.json`
  → loaded at runtime by `ActionManifestLoader` (`Resources.Load`).
- **Backend brains:** `src/plugins/builtin/**` (e.g. `ollama`, `replay_brain`, `simple_memory`).
- **The consumer demo (separate Unity project, not in this repo):** "ColonyDemo".
  Its `Assets/Scripts/` holds the *simulation-specific* layer: `HungerComponent`,
  `EnergyComponent`, `SocialComponent`, `FoodView`, `FoodObjectData`, `AgentView`,
  `POIStation`, `UsePoiActionHandler`, `EatActionHandler`, `AgentAnimationBridge`.
  Several of these are patterns that *should be promoted into the SDK* (see §3, §5).

---

## 1. What already works — preserve these

The core is sound; do not regress it while refactoring:

- **Dependency direction is correct.** The engine never references project/demo code.
  Everything sim-specific lives in the consumer. This is the single most important
  portability property — keep it strict.
- **Component discovery.** `ObservationCollector` auto-collects every
  `ObservationProviderBase` sibling; `ActionExecutor` dispatches to the first matching
  `ActionHandlerBase`. New behaviours plug in without touching the core.
- **Sim-agnostic loop.** `UnitySimulationManager` (tick → gather observations → backend
  decision → distribute) makes no survival/colony assumptions.
- **Role negotiation already exists.** `RolesClient.ListAsync` + `RoleManifestLoader`
  fetch role defs from the backend at connect time. This is the template to copy for
  actions (see §2).
- **One-action-per-agent contract (added this session).** `ActionHandlerBase` now has
  `CanRetarget`/`Retarget`/`OnInterrupted`; `UnityAgentBridge.ApplyDecision` runs exactly
  one action per agent and interrupts/retargets cleanly. Generic; keep it.

---

## 2. Recent engine changes made this session (current state)

So you don't re-derive or undo them:

- `ActionHandlerBase` — added `CanRetarget`, `Retarget()`, `OnInterrupted()` (opt-in,
  default no-op).
- `ActionExecutor` — tracks the running handler; runs handlers inline (`yield return`,
  not detached `StartCoroutine`) so the bridge can cancel the chain; added
  `TryRetarget()` / `CancelRunning()`.
- `UnityAgentBridge.ApplyDecision` — single `_activeAction`; retarget-or-replace per tick.
- `NavMeshMoveActionHandler` — NavMesh.SamplePosition snapping of off-mesh targets,
  hardened arrival (exits on non-`PathComplete`), `CanRetarget`/`Retarget`/`OnInterrupted`.
- `SpeakActionHandler.OnInterrupted` — clears `IsSpeaking`/`CurrentSpeech`.
- `MoveActionHandler` — removed a per-navigate `Debug.Log` (was console spam).
- `NearbyObjectsObservationProvider` — now skips `!activeInHierarchy` objects (depleted/
  despawned items no longer reported).
- `ObservationCollector` — incoming messages persist `messageLifetimeTicks` (default 4)
  instead of one tick.
- `simulation/actions.yaml` — added `work`/`sleep`/`warm`/`rest`; tightened `eat`
  description to gate on availability.

These were point fixes for real bugs; the recommendations below are the *structural*
changes that prevent that class of bug for future sims.

---

## 3. Recommendations (priority order)

Each item: **Problem → Why it hurts portability → Direction → Affected → Effort.**

### R1 — Generic data-driven `Need`/`Stat` abstraction  ⭐ highest leverage
- **Problem:** the demo has `HungerComponent`, `EnergyComponent`, `SocialComponent` — three
  near-identical MonoBehaviours (a clamped float + rate + threshold), each shadowed by a
  near-identical `*ObservationProvider`. Every new sim re-writes this per need.
- **Why it hurts:** "add a need" currently = author two new C# classes + wire them.
  Different sims want totally different needs (reputation, suspicion, supply, morale).
- **Direction:** one `Need` type (fields: `key`, `value`, `min`, `max`, `decayPerSecond`,
  `threshold`, `thresholdDirection`) and a single `NeedsComponent` holding a list of them,
  plus one `NeedsObservationProvider` that emits every need's `{key}`, `{key}_max`,
  `{key}_threshold`. Author needs as data (inspector list or a `NeedSet` ScriptableObject).
  Provide an API (`Modify(key, delta)`, `Get(key)`) so action handlers adjust needs by key.
  - Sketch:
    ```csharp
    [Serializable] public class Need {
        public string key; public float value, min = 0, max = 100;
        public float decayPerSecond; public float threshold; public bool actWhenAbove;
    }
    public class NeedsComponent : MonoBehaviour {
        public List<Need> needs;
        void Update() { foreach (var n in needs)
            n.value = Mathf.Clamp(n.value - n.decayPerSecond*Time.deltaTime, n.min, n.max); }
        public void Modify(string key, float delta) { /* find + clamp */ }
    }
    ```
- **Affected:** new files in SDK (`Runtime/Integration/Needs/`); the demo's three need
  components + providers collapse to data. Action handlers that change stats (eat, sleep)
  call `NeedsComponent.Modify`.
- **Effort:** Medium. Self-contained; can be prototyped against the demo for before/after.

### R2 — One source of truth for the action manifest  ⭐ removes the worst ceremony
- **Problem:** adding one action (`work`) required: edit `simulation/actions.yaml` →
  regenerate `BiomataActions.json` → restart backend → add a Unity handler → (here) add a
  `POIStation` verb. 3+ places that silently drift; the regen/restart loop is the single
  biggest barrier to iterating on a new sim.
- **Why it hurts:** every new action in every new sim pays this tax; mismatches fail
  silently (handler with no manifest entry never fires; manifest entry with no handler logs
  only at runtime).
- **Direction:** copy the **roles negotiation pattern** (`RolesClient` + `RoleManifestLoader`)
  for actions. On connect: Unity reports the verbs its `ActionHandlerBase` components cover
  (there's already `DeclaredActionNames`); backend reports its action space; the union/
  mismatch is surfaced at connect time, not discovered by a stuck agent. Long-term: derive
  the JSON sidecar from `actions.yaml` automatically (build step / CI) so it's never
  hand-regenerated, or drop the sidecar entirely in favour of the RPC.
- **Affected:** `ActionManifestLoader`, a new `ActionsClient`/RPC mirroring `RolesClient`,
  `src/config/manifest.py`, `UnitySimulationManager` connect path.
- **Effort:** Medium–High (touches both languages). High payoff.

### R3 — Structured `BrainConfig` instead of a JSON-string blob  ⭐ authoring pain
- **Problem:** per-agent personality lives in `BiomataAgent.brainConfigJson` — an opaque
  serialized **string** of JSON. Editing it (even appending one sentence) means rewriting
  and hand-escaping the whole blob. It's unauthorable and error-prone.
- **Why it hurts:** every sim author hand-edits JSON strings in the inspector; no reuse,
  no diff, no validation, easy to produce invalid JSON.
- **Direction:** a `BrainConfig` ScriptableObject (or structured serialized fields: name,
  systemPrompt, traits[], goals[], backstory, llm settings) with a small custom inspector.
  Serialize to JSON at registration time (`UnityAgentBridge.RegisterCoroutine`), not at
  authoring time. Personalities become reusable assets shareable across sims.
- **Affected:** `BiomataAgent`, `UnityAgentBridge` (registration), an editor inspector.
  Keep back-compat by serializing the SO to the existing wire format.
- **Effort:** Medium. Self-contained.

### R4 — Promote reusable demo patterns into the SDK as an optional module
- **Problem:** `POIStation` + `UsePoiActionHandler` ("walk to a place, do a timed thing,
  apply an effect") and the needs/observation pairing are genuinely generic but live in the
  demo's `Assets/Scripts`. Every new spatial sim would reinvent them.
- **Why it hurts:** new sims start from scratch or copy-paste the demo.
- **Direction:** ship a `Samples~/` or an opt-in module (e.g. `Biomata.Survival`) in the
  package containing: the `Need` system (R1), a generic `Station`/`UseStationActionHandler`,
  and a worked agent template. Keep the *core* lean; make batteries opt-in.
- **Affected:** package layout; lift `POIStation`/`UsePoiActionHandler` (generalised) from
  the demo.
- **Effort:** Low–Medium (mostly relocation + generalisation).

### R5 — Package the SDK as a versioned UPM package
- **Problem:** the consuming project pulls engine files via absolute paths (the generated
  `BiomataIntegration.csproj` contains
  `<Compile Include="C:\Users\D\workspace\projects\biomata-engine\unity_sdk\Runtime\...">`).
  Whatever the import mechanism, that does not relocate across machines/projects and has no
  version pin.
- **Why it hurts:** "reusable in different simulations" isn't literally true if adoption is
  "copy the folder / hard-code a path."
- **Direction:** distribute `unity_sdk` as a proper UPM package (git URL or registry) with
  `package.json` + semver. A new sim adds one line to its `manifest.json`. Verify the
  `Resources/BiomataActions.json` load path still resolves when packaged.
- **Affected:** `unity_sdk/package.json`, folder layout, docs.
- **Effort:** Low–Medium.

### R6 — Auto-wire identity; validate it
- **Problem:** IDs are hand-entered across sibling components. Real bugs hit this session:
  `AgentBrainBridge.agentId` (demo) disagreed with `BiomataAgent.agentId` on 4 of 5 agents;
  duplicate `foodId`s on duplicated objects.
- **Why it hurts:** every sim author will hit cross-component ID drift; failures are silent.
- **Direction:** one identity owner per entity (`BiomataAgent.AgentId`); siblings read it
  via `GetComponent` rather than storing their own copy. Add an editor validation that flags
  duplicate/missing IDs before play (extend the existing editor validators).
- **Affected:** `BiomataAgent`, any sibling that caches an id; `unity_sdk/Editor/`.
- **Effort:** Low.

### R7 — Treat the observation dictionary as a documented, validated contract
- **Problem:** observations are `Dictionary<string,object>` with stringly-typed keys
  (`nearby_food`, `energy_threshold`, `incoming_messages`). Two bugs this session were
  contract failures: the speech key existed but was wiped after one tick (agents "ignored"
  each other), and depleted food was still reported (agents ate nothing). A typo'd key fails
  silently.
- **Why it hurts:** a new sim's brain must *know* the keys; nothing checks producer vs
  consumer agreement.
- **Direction:** a documented canonical key registry (even just constants + a markdown
  table) and an editor check that the keys providers emit line up with what the manifest/
  brain expects. Static typing not required — discoverability + validation is.
- **Affected:** `Observations/`, docs, `unity_sdk/Editor/` validator.
- **Effort:** Low–Medium.

---

## 4. Summary table

| ID | Recommendation | Priority | Effort | Touches backend? |
|----|----------------|----------|--------|------------------|
| R1 | Generic `Need`/`Stat` abstraction | High | Medium | No |
| R2 | Single source of truth for actions | High | Med–High | Yes |
| R3 | Structured `BrainConfig` (no JSON blob) | High | Medium | No |
| R4 | Promote `Station`/needs into SDK module | Medium | Low–Med | No |
| R5 | Versioned UPM package | Medium | Low–Med | No |
| R6 | Identity auto-wire + validation | Medium | Low | No |
| R7 | Observation contract: document + validate | Medium | Low–Med | Slightly |

## 5. Suggested sequencing

1. **R6 + R5** first — cheap, unblock clean multi-project consumption and kill ID footguns.
2. **R1 + R3** — the two biggest day-to-day authoring wins; self-contained, no backend.
   Prototype each against the existing demo to validate before/after.
3. **R4** — fold R1's output into a reusable sample module.
4. **R2** — the structural fix for action ceremony; do last as it spans both languages.
5. **R7** — layer in alongside R2 (manifest and observations are the two contracts).

## 6. Guardrails while refactoring

- Keep the engine→no-project dependency direction strict (§1).
- Preserve the component-discovery and one-action-per-agent contracts.
- Every change should be validated against a real consuming sim, not just unit tests —
  the bugs that mattered here were integration/contract bugs, not logic bugs.
