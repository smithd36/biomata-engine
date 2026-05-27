# Production Integration Sample

Demonstrates the intended production workflow for the Biomata Unity SDK:

- Scene and environment built in the **Unity Editor**, not at runtime
- NPC GameObjects are **prefab instances placed in the scene**
- `BiomataAgent` configures each NPC entirely in the **Inspector**
- `BiomataSimulationBootstrapper` manages the backend connection
- `ProductionIntegrationManager` is a thin coordinator — no scene building code

---

## Prerequisites

1. Python backend running:
   ```
   biomata-ws --config <your-sim.yaml> --port 8765
   ```
2. Your `sim.yaml` declares agents with IDs that match the `Agent Id` fields you set in
   the BiomataAgent Inspector.

---

## Scene hierarchy

Build this hierarchy in the Editor. No naming convention is required by the SDK.

```
Scene
├── Manager                               (empty GameObject)
│   ├── BiomataSimulationBootstrapper     ← connection + tick lifecycle
│   │     Config Asset: [optional SO]
│   │     Tick Rate: 2
│   │     Auto Connect: ✓
│   │     Auto Tick: ✓
│   └── ProductionIntegrationManager      ← drag bootstrapper reference here
│         Bootstrapper: [Manager]
│         Status Text: [optional UI Text]
│         Agent List Text: [optional UI Text]
│
├── NPCs
│   ├── Aldric                            (your mesh / prefab)
│   │   ├── BiomataAgent                  ← agentId="guard_001", role="Guard"
│   │   ├── ObservationCollector          ← added automatically by BiomataAgent
│   │   ├── ActionExecutor                ← added automatically by BiomataAgent
│   │   ├── UnityAgentBridge              ← added automatically by BiomataAgent
│   │   ├── TransformObservationProvider  ← added by Reset() when BiomataAgent first attached
│   │   ├── MoveActionHandler             ← added by Reset()
│   │   ├── SpeakActionHandler            ← added by Reset()
│   │   ├── InteractActionHandler         ← added by Reset()
│   │   └── NpcStatusDisplay              ← optional; material tint + console logging
│   │
│   ├── Silas                             (another NPC)
│   │   ├── BiomataAgent                  agentId="merchant_001", role="Merchant"
│   │   └── …
│   └── …
│
└── World                                 (pre-authored environment)
    ├── Ground
    ├── Buildings/
    ├── Props/
    └── …
```

---

## Step-by-step setup

### 1. Create the Manager object

1. **GameObject → Create Empty**, name it `Manager`.
2. **Add Component → Biomata → Simulation Bootstrapper**.
   - Set **Host**, **Port**, **Tick Rate** in the Inspector.
   - Or create a config asset (**right-click Project → Create → Biomata → Simulation Config**)
     and drag it into the **Config Asset** slot.
3. **Add Component → Biomata → Samples → Production Integration Manager**.
   - Drag the `BiomataSimulationBootstrapper` component (or its GameObject) into the
     **Bootstrapper** slot.
   - Optionally wire up UI Text references for on-screen status.

### 2. Set up an NPC

1. Add your NPC mesh/prefab to the scene.
2. **Add Component → Biomata → Agent**.
   - Unity auto-adds `ObservationCollector`, `ActionExecutor`, `UnityAgentBridge`, and
     (via `Reset()`) `TransformObservationProvider`, `MoveActionHandler`,
     `SpeakActionHandler`, `InteractActionHandler`.
3. In the **BiomataAgent** Inspector, set:
   - **Agent Id** — must match the agent declared in your `sim.yaml`.
   - **Display Name** — human-readable label.
   - **Role** — passed to the backend as part of the agent's initial observation.
   - **Capabilities** — list of capability strings the backend uses to restrict actions.
   - **Brain Class** (optional) — fully qualified Python class, e.g.
     `src.plugins.builtin.ollama_brain.brain.OllamaLLMBrain`.
   - **Brain Config JSON** (optional) — JSON object passed to the brain at registration.
4. Optionally add **NPC Status Display** for material color feedback during actions.

### 3. Extend with additional providers or handlers

Add any extra observation providers or action handlers to the NPC GameObject:

```
MoveActionHandler           ← existing
SpeakActionHandler          ← existing
InteractActionHandler       ← existing
MyCustomActionHandler       ← your subclass of ActionHandlerBase

TransformObservationProvider  ← existing
NearbyAgentsObservationProvider  ← registry-based proximity
POIObservationProvider        ← tagged point-of-interest detection
TimeObservationProvider       ← sim_time / time_of_day
MyCustomObservationProvider   ← your subclass of ObservationProviderBase
```

Component order determines priority for action handlers (first CanHandle() match wins).
Observation providers write in component order; later providers may overwrite earlier keys.

### 4. Run

1. Start the backend.
2. Press **Play**.
3. The bootstrapper connects and starts ticking automatically (if `Auto Connect` and
   `Auto Tick` are enabled).
4. Each BiomataAgent registers itself, receives decisions, and dispatches them to the
   action handlers on the same GameObject.

---

## Config asset workflow (recommended for teams)

Create one asset per environment:

```
Assets/Settings/BiomataConfig_Local.asset      host=localhost, debugLogging=true
Assets/Settings/BiomataConfig_Staging.asset    host=staging.example.com, useTls=true
```

Assign the appropriate asset to the bootstrapper's **Config Asset** slot. Use the
**Override** toggles to replace individual setting groups per-scene without creating a
new asset.

---

## Key files

| File | Purpose |
|---|---|
| `ProductionIntegrationManager.cs` | Thin scene coordinator — event subscriptions only |
| `NpcStatusDisplay.cs` | Optional per-NPC visual feedback component |
| `BiomataAgent.cs` | Core NPC component (Runtime/Integration/Agents) |
| `BiomataSimulationBootstrapper.cs` | Connection + tick lifecycle (Runtime/Integration/Simulation) |
| `BiomataSimulationConfig.cs` | Shared SO config asset (Runtime/Integration/Simulation) |
