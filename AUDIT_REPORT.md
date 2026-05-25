# Biomata Engine — Technical Audit Report
**Date:** 2026-05-24  
**Auditor:** Claude Sonnet 4.6  

---

## Executive Summary

Biomata Engine is a well-structured Python NPC backend with a clear layered architecture. The core contracts, engine, service, and transport layers are coherent and show deliberate design. However, the codebase has accumulated meaningful technical debt in several areas: dead symbols that are defined but never used, a mismatch between the `BrainContext.social_context` field (defined, never populated), a village simulation where every agent has `capabilities: [social]` (defeating the entire capability-gating system that was built for it), a builtin `OllamaLLMBrain` that is hardcoded to "village simulation" domain language, and a `ComponentConfig.kwargs()` Pydantic model method that the config loader never calls. The Unity SDK also marks three agents as "Deterministic" when all 13 are actually running OllamaLLMBrain in the YAML.

### Top 10 Issues

1. **`BrainContext.social_context` (str field) is declared in the contract but never populated by the engine anywhere in the codebase.** The field exists in `brain.py` but `agent_runtime.py` does not inject it; no brain or observation provider reads it. It is effectively a stub.

2. **All 13 village agents have `capabilities: [social]` in `sim.yaml`,** meaning the capability-gating system (`ActionRegistry.schemas_for`, `ObservationRegistry.schemas_for`) never filters anything for this simulation. The socialize action tag system was built precisely to differentiate agents, but the configuration bypasses it entirely.

3. **`OllamaLLMBrain._SYSTEM_TEMPLATE` contains "You are an autonomous character in a village simulation."** — this domain-specific prompt text is hardcoded inside a general-purpose plugin that is supposed to be reusable across medieval, corporate, patrol, and any future simulations.

4. **`ACTION_DISPATCHED`, `AGENT_DIED`, `CHECKPOINT_SAVED`, and `SOCIAL_UPDATED` event constants are defined in `event_bus.py` but never emitted by any code in the repository.** They are placeholders with no subscribers and no emitters.

5. **`ComponentConfig.kwargs()` in `schema.py` is never called by the config loader.** The loader uses its own manual dict manipulation (`raw.pop("class")`) rather than the Pydantic model's `.kwargs()` accessor, making `ComponentConfig` partially decorative.

6. **`Agent.view()` method in `agent.py` is never called anywhere in the codebase.** The engine constructs `AgentView` directly in `agent_runtime.py` without using this convenience method.

7. **`SocialContextProvider` in `src/plugins/builtin/observations/providers.py` is defined but not used in any simulation.** The village demo uses a custom `SocialMemoryProvider` instead; no other simulation uses the builtin. The builtin also calls `.get_relationships()` which exists on `VillageRelationships` but not on `WeightedGraphSocial` (which has `.describe()` instead), creating a silent API incompatibility.

8. **`SimulationTimeProvider` in `src/plugins/builtin/observations/providers.py` is defined but not used in any simulation.** The village demo uses its own `TimeOfDayProvider`; medieval and corporate have no obs registry; patrol has no obs registry.

9. **`ReplayBrain` in record mode initializes `self._by_agent = {}` with the comment "unused in record, kept for symmetry."** This is acknowledged dead state in a production class.

10. **The Unity C# `VillageLifeDemo.cs` labels guards and farmer as `"Deterministic"` (`CognitionType`) but `sim.yaml` assigns them `OllamaLLMBrain`.** The label is display-only but will mislead developers inspecting live behavior.

### Scores
- **Architectural Risk:** 3/10 — core architecture is solid; risk is localized to dead code and drift
- **Maintainability:** 6/10 — good structure but the village sim conflation with general engine is a growing liability
- **Dead Code Estimate:** ~12% of codebase (unused event constants, unused BrainContext field, unused methods, two unused builtin providers, unused Pydantic accessor)

### Quick Wins
- Remove `social_context` from `BrainContext` or populate it (one-line fix in `agent_runtime.py`)
- Remove 4 undefined event constants or add emitters for them
- Fix `OllamaLLMBrain` system prompt to use a configurable template parameter
- Call `ComponentConfig.kwargs()` in `loader.py` or delete the method
- Remove `Agent.view()` or use it in `agent_runtime.py`
- Fix `VillageLifeDemo.cs` `CognitionType` labels to match reality

### Dangerous Hidden Issues
- **Pickle deserialization in `handler.py:347`** (`pickle.loads(base64.b64decode(b64))`) — deserializing client-supplied data without any validation is a remote code execution vector if the WebSocket server is ever exposed beyond localhost.
- **`ObservationRegistry.collect()` silently swallows all exceptions** (`except Exception: pass` at `obs_registry.py:119`) — a buggy provider will produce empty observations with no log output, causing silent behavioral failure that is very hard to diagnose in a running simulation.
- **`SocialContextProvider.observe()` also swallows all exceptions** — same issue.
- **Module-level singletons in `examples/village/sim/social.py`** (`_relationships`, `_inbox`) are process-global. If two village simulations are loaded in the same Python process (e.g., in tests), they share state. There is no reset mechanism.

---

## 1. Dead / Unused Code

| Symbol | File | Reason Unused | Confidence | Recommendation |
|--------|------|---------------|------------|----------------|
| `ACTION_DISPATCHED` | `src/engine/event_bus.py:44` | Constant defined, never imported or emitted anywhere in repo | High | Remove or add emitter in `registry.py:dispatch()` |
| `AGENT_DIED` | `src/engine/event_bus.py:50` | Constant defined, never imported or emitted | High | Remove (future feature stub — document if intentional) |
| `CHECKPOINT_SAVED` | `src/engine/event_bus.py:51` | Constant defined, never imported or emitted | High | Remove or emit from `save_snapshot()` |
| `SOCIAL_UPDATED` | `src/engine/event_bus.py:49` | Constant defined, never imported or emitted | High | Remove or emit from `SocialEffectSubscriber` |
| `BrainContext.social_context` | `src/contracts/brain.py:35` | Field declared but never set by engine; no brain reads it; `OllamaLLMBrain` uses `obs.get("social_relationships")` from observation dict instead | High | Remove or populate in `AgentRuntime.step()` |
| `Agent.view()` | `src/engine/agent.py:29` | Method never called; engine constructs `AgentView` directly at `agent_runtime.py:92` | High | Remove or call in `agent_runtime._build_observation()` |
| `ComponentConfig.kwargs()` | `src/config/schema.py:31` | Method never called; `loader.py` uses manual `raw.pop("class")` instead of this accessor | High | Either call it in `loader.py` or remove it |
| `SimulationTimeProvider` | `src/plugins/builtin/observations/providers.py:22` | Defined but not registered in any simulation's obs registry | High | Document as "ready-to-use but opt-in" or add to a default registry |
| `SocialContextProvider` | `src/plugins/builtin/observations/providers.py:38` | Defined but not used; incompatible API with `WeightedGraphSocial` (calls `.get_relationships()` which exists on `VillageRelationships` but not `WeightedGraphSocial`) | High | Fix API or document the requirement clearly in docstring |
| `FunctionProvider` | `src/plugins/builtin/observations/providers.py:108` | Defined but not used in any simulation in the repo | Med | Document as a utility adapter; add usage example in tests |
| `EventLogSubscriber` | `src/engine/event_bus.py:108` | Only used in `src/cli/main.py`; not surfaced in any example | Low | Keep — legitimate utility |
| `ObservabilitySubscriber` | `src/engine/event_bus.py:125` | Exported from `src/engine/__init__.py`, never used in examples or tests | Med | Remove or add usage |
| `ReplayBrain._by_agent` (record mode) | `src/plugins/builtin/replay_brain/brain.py:75` | Explicitly commented "unused in record" | High | Remove the field; it was a premature symmetry |
| `VillagerBrain`, `SocialVillagerBrain` | `examples/village/sim/brain.py` | Fully implemented and documented, but `sim.yaml` uses only `OllamaLLMBrain` for all 13 agents | Med | Either wire them in sim.yaml or move them to a separate "deterministic" example |

---

## 2. Architectural Drift

### 2.1 OllamaLLMBrain Is a Village-Specific Brain Disguised as a General Plugin

**Evidence:** `src/plugins/builtin/ollama/brain.py:36` contains:
```
You are an autonomous character in a village simulation.
```
The `_build_prompt` method hardcodes observation keys specific to the village (`"nearby_pois"`, `"nearest_poi"`, `"time_of_day"`, `"social_relationships"`) and silently skips them for non-village worlds via a `skip` set at lines 188-195. Medieval and corporate simulations that use `OllamaLLMBrain` will receive prompts with large empty sections ("Nearby POIs: none visible").

**Impact:** The "builtin" plugin is not actually reusable. Any non-village simulation using it gets a misleading, poorly-structured prompt with blank sections. The medieval and corporate examples presumably do not use this brain, but the registration in the YAML loader's docstring shows it as the canonical example.

**Files:** `src/plugins/builtin/ollama/brain.py:36`, `src/plugins/builtin/ollama/brain.py:153-233`

### 2.2 Village Simulation Capability System Negated by Configuration

**Evidence:** `examples/village/sim.yaml` lines 47, 71, 97, 122, 146, 171, 197, 224, 252, 279, 306, 334, 358 — every single agent has `capabilities: [social]`. The `ActionRegistry.schemas_for()` and `ObservationRegistry.schemas_for()` filtering logic was designed to hide `socialize` from guards and farmers, but guards (`guard_001`, `guard_002`) and farmer (`farmer_001`) are all tagged `social`, so they all see `socialize`. The registry's docstring in `registry.py` even says "The 'socialize' action is tagged so guards and farmers (no social capability) cannot choose it" — contradicted by the YAML.

**Impact:** Medium behavioral (guards and farmers can attempt socialize), medium design (the differentiation system is bypassed, making its value invisible to future developers).

**Files:** `examples/village/sim.yaml`, `examples/village/sim/registry.py:7-9`

### 2.3 Two Parallel Social Systems for the Village

**Evidence:** The village has `VillageRelationships` (bilateral familiarity + affinity, in `examples/village/sim/social.py`) and the engine also supports `WeightedGraphSocial` (directed graph, in `src/plugins/builtin/simple_social/social.py`). The village `sim.yaml` does NOT configure any social system via the YAML `social:` key — meaning the `SocialEffectSubscriber` wired in `loader.py:154` is never set up for the village. Instead, social tracking happens through the module-level singleton `VillageRelationships` directly called from `SocializeHandler`. This means the `EventBus` social effect path (`action_completed` → `SocialEffectSubscriber` → `social.update()`) is completely unused for the village.

**Impact:** Two divergent social tracking mechanisms exist. The village bypasses the designed event-driven path entirely, making `side_effects` in `ActionResult` unused for this simulation. Future developers may add social logic to both systems independently.

**Files:** `examples/village/sim/social.py`, `examples/village/sim/handlers.py:83-115`, `src/engine/event_bus.py:94-106`

### 2.4 Config Loader Validates with Pydantic but Does Not Use the Validated Model

**Evidence:** `src/config/loader.py:107-121` — the loader validates with Pydantic (`SimConfig.model_validate(cfg)`), but then only uses `eng_cfg` (the engine config) from the result. All other validated components (`world`, `registry`, `observations`, `social`, `agents`) are re-read from the raw `cfg` dict. The Pydantic validation provides error-checking but not structured access — it is applied and then discarded.

**Impact:** The validation is still useful (it catches malformed YAML), but the `ComponentConfig.kwargs()` method, `AgentConfig`, and all the other Pydantic models defined in `schema.py` are never actually used to drive construction. This is a partially-implemented abstraction that gives a false sense of schema enforcement for construction parameters.

**Files:** `src/config/loader.py:107-214`, `src/config/schema.py`

---

## 3. Partial Implementations

### 3.1 BrainContext.social_context Is Never Populated

`BrainContext` declares `social_context: str = ""` at `src/contracts/brain.py:35`. The `AgentRuntime.step()` method at `src/engine/agent_runtime.py:103-109` constructs a `BrainContext` and does not set `social_context`. No other engine component sets it. No brain implementation reads it — `OllamaLLMBrain` reads `obs.get("social_relationships")` from the observation dict instead, which is the observation-registry mechanism. This field was likely a holdover from a pre-observation-registry design and was never removed.

### 3.2 Snapshot System Is Complete but `Scheduler.Snapshotable` Never Exercised

`SimulationSnapshot.scheduler` field exists and `Simulation.snapshot()` attempts to serialize the scheduler (`src/engine/simulation.py:205-209`). Neither `SimultaneousScheduler` nor `SequentialScheduler` implements `Snapshotable`, so `scheduler_bytes` is always `None`. The restore path at `simulation.py:281-283` is never triggered. This is a safe partial implementation (works without snapshot) but is a hidden gap if scheduler state ever becomes meaningful (e.g., `SequentialScheduler._order` changes at runtime).

### 3.3 `ReplayBrain.close()` Has No Lifecycle Hook in the Engine

`ReplayBrain` opens a file in record mode and provides `close()` and `__del__()` for cleanup. The engine and `SimulationSession` never call `close()` on brains. The `__del__` finalizer is the only protection, which is unreliable. A run aborted mid-tick in record mode may leave the JSONL file partially flushed. No `Brain` contract method for cleanup exists.

### 3.4 `ObservabilitySubscriber` Is Exported but Not Wired

`src/engine/__init__.py:3` exports `ObservabilitySubscriber`. The CLI (`src/cli/main.py`) does not use it, examples do not use it. It is an API surface with no demonstrated usage path.

---

## 4. Design Problems

### Priority: HIGH

**H1: Unsafe pickle deserialization of client-supplied data**

`src/transport/websocket/handler.py:347`:
```python
snap = pickle.loads(base64.b64decode(b64))
```
The server deserializes a `data_b64` field sent by the WebSocket client with no validation beyond checking it is a string. If this server is ever exposed outside localhost (which `biomata-ws` supports via `--host`), this is a remote code execution vulnerability. The snapshot should be validated for type and version before unpickling, or use a safer serialization format (JSON, MessagePack) for the over-the-wire format.

**H2: Silent exception swallowing in ObservationRegistry.collect()**

`src/engine/obs_registry.py:119`:
```python
except Exception:
    pass
```
A provider that raises an exception will silently produce an empty observation slice with no log output. A brain may receive incomplete or wrong observation data and make a decision based on it. The bug will be invisible at runtime. At minimum, this should `logger.warning(...)` the exception type and provider name.

**H3: Module-level singletons prevent test isolation**

`examples/village/sim/social.py:143-154` defines process-global `_relationships` and `_inbox`. Any test that imports `build_village_registry()` or `build_village_obs_registry()` will share state with any other test that does the same. The singletons have no reset mechanism. This is not just a test concern — running two village simulations in the same process (e.g., parallel testing) produces cross-contamination.

### Priority: MEDIUM

**M1: OllamaLLMBrain system prompt is not configurable**

The system prompt template at `src/plugins/builtin/ollama/brain.py:35-49` is a hardcoded string literal. A domain-specific simulation (medieval, corporate) cannot customize the system prompt instruction without subclassing the brain. The `Personality` dataclass at line 29 allows personalizing the user prompt, but the system prompt instruction framework ("You are an autonomous character in a village simulation") is fixed. This limits reuse.

**M2: WorldContext.rng is not passed to ActionHandlers**

The `WorldContext` protocol declares `rng: random.Random` at `src/contracts/world.py:89`. This is meant to be used by handlers for determinism. The medieval handlers at `examples/medieval/sim/handlers.py:125,234` use `context.rng` correctly. However, the `HostedWorld` (the village simulation's world) has `rng` set on it as an attribute by `Simulation.__init__`, but handlers receive `self.world` as the `context` argument and the village handlers do not use `context.rng` at all. Village behavior is thus influenced by `random.random()` in `SocialVillagerBrain` (line `examples/village/sim/brain.py:183,193`) which uses the module-level `random`, bypassing the seeded simulation RNG. This makes the village non-reproducible from seed.

**M3: VillagerBrain and SocialVillagerBrain are documented but unused in the live sim.yaml**

`examples/village/sim/brain.py` is a full, polished, documented implementation of deterministic brains. The village `sim.yaml` comment at line 8 says "Hybrid cognition backend (deterministic + Ollama LLM)" and the Unity C# has `CognitionType: "Deterministic"` and `"Social"` labels. But the YAML does not reference `VillagerBrain` or `SocialVillagerBrain` at all — all 13 agents use `OllamaLLMBrain`. The deterministic brains are complete dead code for the primary demo.

**M4: Two versions of engine_commands access (world vs. TickSummary)**

`HostedWorld.apply()` collects `engine_commands` into `self._pending_commands`, accessible via `world.collect_commands()`. `TickSummary.engine_commands()` reads directly from `ActionResult.engine_commands` in memory. These two paths produce the same data. The `src/engine/simulation.py:66-72` docstring acknowledges both exist. The Unity C# (`VillageLifeDemo.cs`) uses neither — it reads `d.Parameters` from the `AgentDecisionResult` on the bridge side, not `engine_commands` directly. This triplication of data access paths for the same information will cause confusion.

### Priority: LOW

**L1: `_SYSTEM_TEMPLATE` uses `{{` / `}}` escaping for JSON braces**

`src/plugins/builtin/ollama/brain.py:36-49` — the template uses Python `.format()` escaping for literal braces, which is fragile and confusing. If a future developer adds `{new_field}` to the template without realizing it is a format template, they will get a `KeyError`. A template library (even simple `string.Template`) or f-string would be clearer.

**L2: `Simulation._tick()` emits `"tick_start"` as a string literal instead of `TICK_START` constant**

`src/engine/simulation.py:309`: `type="tick_start"` — the other tick event at line 337 uses `TICK_END` (the constant). This inconsistency means a typo in the literal would not be caught by IDEs or static analysis, while the constant form would.

**L3: `ReplayBrain._by_agent` comment calls it "unused"**

`src/plugins/builtin/replay_brain/brain.py:75`: explicitly `# unused in record, kept for symmetry`. Either remove it or justify the field. "Kept for symmetry" is not a valid reason to maintain dead state in production code.

---

## 5. Domain Model Inconsistencies

### 5.1 Two Social Tracking Contracts With Different APIs

`SocialSystem` protocol (`src/contracts/social.py`) defines `.update(from_id, to_id, delta: float)`, `.relationship()`, `.describe()`. The `VillageRelationships` class (`examples/village/sim/social.py`) uses `.interact()`, `.get()`, `.get_relationships()`, `.summary_for()`. These are completely different APIs. `VillageRelationships` does not implement `SocialSystem` — it cannot be swapped in. The `SocialContextProvider` builtin assumes `.get_relationships(agent_id)` returning `dict[str, float]`, which matches `VillageRelationships` but not `WeightedGraphSocial` (which returns relationship weights per edge, not a name-to-score dict).

### 5.2 Nearby Agents: Two Different Shapes in the Same Key

The `nearby_agents` observation key is populated in three different places with different shapes:
- `AgentRuntime._build_observation()` (`agent_runtime.py:216-217`): `[{"id", "name", "inventory", "ext"}]`
- `NearbyAgentsProvider.observe()` (`observations.py:134-140`): `[{"id", "name", "distance", "role", "inventory", "ext"}]`
- `HostedWorld.push_observation()` (Unity side): whatever Unity pushes, typically `[{"id", "name"}]` (see `VillageLifeDemo.cs`)

`OllamaLLMBrain._build_prompt()` reads `a.get('role','?')` and `a.get('distance','?')` from each nearby agent — these keys only exist in the observation-registry path. For non-registry simulations (medieval, corporate), these fields are absent and the prompt shows `role:?  dist:?` for every nearby agent.

### 5.3 Observation Key Conflicts Are Silent

The `AgentRuntime._build_observation()` method merges observation layers in this order: registry providers → world → engine-injected identity. A registry provider that returns `{"agent_id": "something"}` will be silently overwritten by the engine-injected `world_obs["agent_id"] = agent.id`. This is documented as intentional ("world wins") but there is no warning mechanism when a provider key conflicts with a reserved engine key. Reserved keys (`agent_id`, `agent_name`, `inventory`, `state_ext`, `state_advice`, `state_str`, `nearby_agents`) are not documented anywhere in the `ObservationProvider` contract.

---

## 6. Config / Runtime Mismatches

### 6.1 `sim.yaml` observations field uses `class:` pointing to a builder function

`examples/village/sim.yaml:29`: `class: examples.village.sim.obs_registry.build_village_obs_registry`. The `loader.py:138-142` handles this correctly by calling the imported symbol as a function. But `SimConfig.observations` is typed as `ComponentConfig | None`, and `ComponentConfig.class_` is validated as a dotted path. This works but the semantic is a function, not a class — the naming `class:` is misleading for builder functions.

### 6.2 `engine.log_level` Is Parsed by Config/Schema but Never Used

`src/config/schema.py:19`: `log_level: str = "normal"`. `src/engine/simulation.py:35`: `log_level: str = "normal"`. The loader reads `log_level` and passes it to `SimulationConfig`. But neither `Simulation` nor `AgentRuntime` inspects `self.config.log_level` for any behavior. Verbose/quiet logging modes are not implemented.

### 6.3 `SequentialScheduler._order` Has No YAML Support

`src/engine/scheduler.py:70` — `SequentialScheduler` accepts an `order: list[str]` parameter. The loader at `src/config/loader.py:157-158` constructs only `SequentialScheduler()` with no arguments when `scheduler: sequential` is in the YAML. There is no way to specify order via YAML configuration.

### 6.4 Agent `inventory` Cannot Be Set via YAML

`src/config/schema.py:36-44` — `AgentConfig` has no `inventory` field. The loader at `src/config/loader.py:185-192` creates `Agent` with no `inventory` argument (defaults to `{}`). There is no way to give an agent starting inventory from YAML. The medieval simulation requires items but must populate them programmatically.

---

## 7. Unity / SDK Findings

### 7.1 CognitionType Labels in C# Are Incorrect

`unity_sdk/samples/VillageDemo/VillageLifeDemo.cs:161-178`:
- `guard_001` ("Aldric"): labeled `"Deterministic"` → actually `OllamaLLMBrain` in YAML
- `guard_002` ("Berna"): labeled `"Deterministic"` → actually `OllamaLLMBrain` in YAML
- `farmer_001` ("Edith"): labeled `"Deterministic"` → actually `OllamaLLMBrain` in YAML
- `villager_001`–`004`, `townsfolk_001`–`002`: labeled `"Social"` → actually `OllamaLLMBrain` in YAML (not using `SocialVillagerBrain`)

Only `merchant_001`, `innkeeper_001`, `traveler_001`, `scholar_001` are correctly labeled `"LLM (Ollama)"`.

The `CognitionType` field is display-only (used in the inspector text at line 980), but it creates a persistent incorrect mental model for developers debugging the demo.

### 7.2 `SetField` Uses Reflection to Bypass Unity Component Encapsulation

`VillageLifeDemo.cs:1354-1359`: `SetField` uses `BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance` with `GetField` and `SetValue`. This bypasses the public API of SDK components (`MoveActionHandler`, `UnityAgentBridge`, `SpeakActionHandler`, etc.), setting private fields directly. This will silently fail if those field names change — no compile-time error. For production SDK code, the SDK components should expose public properties or a configuration constructor.

### 7.3 `awning`, `bush02`, `rock01`, `rock02`, `rockSmall`, `bannerA`, `bannerB`, `logWood`, `sky` Prefabs Have No Null Check on Spawn

The `Spawn()` helper at line 656 guards `if (prefab == null) return false`, but scene-building methods call `Spawn()` and discard the return value for most non-critical props. If these prefabs fail to load (RPGPP_LT not imported), the scene builds silently with missing deco. Only `BuildWell()` and `BuildTavern()` have explicit fallback logic. This is acceptable for a demo but should be documented.

### 7.4 `ConnectionHandler` Accesses `session._sim` Directly

`src/transport/websocket/handler.py:97-99`:
```python
self._sim   = session._sim
self._world = session._sim.world
```
The handler reaches into the session's private `_sim` attribute. The `SimulationController` protocol explicitly exists to avoid this coupling. The handler should only use the public `session` API. If `SimulationSession` needs to expose `world` for the `push_observation` path, it should do so via a public property or method, not by the caller reading private attributes.

---

## 8. Missed Existing Implementations

### 8.1 `SocialContextProvider` Could Replace `SocialMemoryProvider`

`src/plugins/builtin/observations/providers.py:38` provides `SocialContextProvider`. `examples/village/sim/observations.py:147` provides `SocialMemoryProvider` — which does roughly the same thing. The builtin was written (or left) without adapting the village to use it. The API difference (`VillageRelationships.get_relationships()` returns `dict[str, dict[str, float]]` but `SocialContextProvider` expects `dict[str, float]`) could be resolved by fixing either the builtin or the village social class.

### 8.2 `SimulationTimeProvider` Could Replace `TimeOfDayProvider`

`src/plugins/builtin/observations/providers.py:22` injects `simulation_tick`. `examples/village/sim/observations.py:38` (`TimeOfDayProvider`) injects both `simulation_tick` and `time_of_day`. The builtin only does tick; the village adds time-of-day derivation. The builtin was not extended to include the time-of-day logic, so a parallel implementation was written instead.

### 8.3 `ComponentConfig.kwargs()` Was Built to Simplify Loader but Is Not Used

`src/config/schema.py:31`: `def kwargs(self) -> dict[str, Any]: return self.model_extra or {}`. This was built specifically to simplify the loader, but `loader.py` never calls it. The loader duplicates this logic manually (`raw.pop("class")` then `**raw_remaining`). Adopting `ComponentConfig.kwargs()` would make the Pydantic validation meaningful for kwargs construction.

### 8.4 `Agent.view()` Was Built to Create AgentViews but the Engine Builds Them Inline

`src/engine/agent.py:29` — `view()` delegates to `AgentView.from_agent(self)`. `agent_runtime.py:92-97` constructs `AgentView` directly with the same fields. The convenience method exists but is never exercised. Using it would make the construction location clearer and keep the creation logic in one place.

---

## 9. Recommended Refactor Plan

### Immediate (Do Now)

1. **Fix `BrainContext.social_context`** — either populate it in `AgentRuntime.step()` from the social system, or remove the field. The observation-registry pattern (which is the right approach) makes it redundant. Remove and simplify.

2. **Fix the 4 dead event constants** — add a log-level warning emitter for `ACTION_DISPATCHED` in `ActionRegistry.dispatch()`, remove `AGENT_DIED`, `CHECKPOINT_SAVED`, `SOCIAL_UPDATED` entirely (or file them as GitHub issues with clear intent).

3. **Add logging to `ObservationRegistry.collect()`** — replace `except Exception: pass` with `except Exception as exc: logger.warning("provider %s failed: %s", type(provider).__name__, exc)`. Same for `SocialContextProvider`.

4. **Fix `VillageLifeDemo.cs` CognitionType labels** — `guard_001`, `guard_002`, `farmer_001` should be `"LLM (Ollama)"` or match reality. Update to `"LLM (Ollama)"` or introduce a `"Social-LLM"` category.

5. **Add `agent_id` and other reserved keys to `ObservationProvider` docstring** — list the keys that are engine-reserved so provider authors don't accidentally collide.

### Short-term (Next Sprint)

1. **Fix `OllamaLLMBrain` system prompt** — make the first line configurable via `Personality.role_description: str = "an autonomous NPC"`. Replace the hardcoded `"village simulation"` with `{role_description}` in `_SYSTEM_TEMPLATE`. This makes the builtin genuinely general-purpose.

2. **Adopt `ComponentConfig.kwargs()` in `loader.py`** — replace the manual dict manipulation with the Pydantic model's accessor. This activates the schema validation for kwargs and reduces loader complexity.

3. **Replace `SetField` reflection in `VillageLifeDemo.cs`** with SDK component public properties or a structured `Configure(...)` call pattern.

4. **Fix capability assignments in `sim.yaml`** — guards and farmer should have no `social` capability if the socialize action is supposed to be gated. This would activate the capability-filtering system as designed. If all agents should have social capability, remove the tags from the `socialize` action schema and simplify the model.

5. **Break module-level singletons in `social.py`** — move `_relationships` and `_inbox` into a context object passed to `build_village_registry()` and `build_village_obs_registry()`. This enables test isolation and multiple simultaneous simulations.

6. **Either use `VillagerBrain`/`SocialVillagerBrain` in `sim.yaml` or move them to a separate `examples/village/sim_deterministic/` configuration** so they are not dead code relative to the main demo.

### Long-term (Architectural)

1. **Resolve the dual-social-system problem** — establish one canonical social API. Either make `VillageRelationships` implement `SocialSystem`, or redesign `SocialSystem` to have a richer API. The observation-registry path (`SocialContextProvider`) and the handler-direct path (`SocializeHandler → VillageRelationships.interact()`) should both go through the same social object via the same interface.

2. **Add a `Brain` cleanup contract** — add an optional `close()` or `shutdown()` method to the `Brain` protocol (or a separate `Closable` protocol), and call it from `Simulation.shutdown()` or `SimulationSession.shutdown()`. This would give `ReplayBrain` a reliable file-close path.

3. **Harden the WebSocket snapshot restore** — replace `pickle.loads(base64.b64decode(b64))` with a two-step approach: deserialize with pickle only after verifying the snapshot's magic bytes or using a format that doesn't allow arbitrary code execution. At minimum, wrap in a strict try/except that logs the client IP on failure.

4. **Consolidate the three `engine_commands` access paths** — define a canonical post-tick interface: either always use `TickSummary.engine_commands()` (in-memory, already available), or always use `world.collect_commands()` (for HostedWorld). Remove the duplicate path. Update the Unity SDK to use one consistent pattern.

5. **Add `inventory` to `AgentConfig` in `schema.py`** — allow agents to start with items defined in YAML. This is a basic feature gap that forces programmatic initialization for any simulation with starting state.
