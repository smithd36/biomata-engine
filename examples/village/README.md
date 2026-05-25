# Biomata Social Village MVP

A flagship showcase: 13 autonomous NPCs living in a medieval village, driven by
Biomata's hybrid cognition backend. Agents patrol, wander, trade gossip, and form
social relationships — entirely without scripted state machines.

## What it demonstrates

**As game AI middleware**
- Autonomous NPCs that patrol, wander, idle, interact, speak, and *socialize* without scripted state machines
- Hybrid cognition: deterministic brains for predictable agents, LLM brains for socially interesting ones
- Capability-filtered actions and observations — guards see different actions than social agents
- Emergent social relationships: familiarity and affinity tracked per agent-pair and reflected in prompts
- Real-time Unity integration over WebSocket — one backend session, many simultaneous NPCs

**As interactive simulation laboratory**
- Observe per-agent decisions and social connection counts in the live inspector
- Watch social interactions animate: agents face each other, display speech bubbles
- Tune tick rate and watch how cognition speed affects emergent behaviour
- Swap brain classes in `sim.yaml` without touching Unity

---

## Architecture overview

```
Unity 6 (rendering + physics)
  └── VillageLifeDemo.cs          procedural scene + HUD + inspector
  └── UnitySimulationManager      WebSocket tick driver
  └── UnityAgentBridge × 13       per-NPC observation + decision routing
  └── MoveActionHandler           translates navigate commands → Transform movement
  └── SpeakActionHandler          speech bubbles + speak events
  └── InteractActionHandler       interact events
  └── socialize (inline)          face-target + pink flash + social log

WebSocket (JSON, localhost:8765)

biomata-engine (Python)
  └── HostedWorld                 receives Unity observations, emits engine_commands
  └── SimultaneousScheduler       all 13 brains decide in parallel each tick
  └── ObservationRegistry         capability-filtered observation providers
      ├── TimeOfDayProvider        → simulation_tick, time_of_day
      ├── SelfStateProvider        → nearest_poi, position
      ├── NearbyPoiProvider        → nearby_pois (sorted by distance)
      ├── NearbyAgentsProvider     → nearby_agents, nearby_count  [social only]
      └── SocialMemoryProvider     → social_relationships         [social only]
  └── ActionRegistry              capability-filtered action schemas
      ├── navigate / speak / interact / idle  (universal)
      └── socialize                           [social only, HYBRID]
  └── VillageRelationships        familiarity + affinity per agent-pair
  └── WaypointBrain × 2           guard patrol loop
  └── VillagerBrain × 1           farmer daily routine (no social capability)
  └── SocialVillagerBrain × 6     deterministic social wanderers (villagers + townsfolk)
  └── OllamaLLMBrain × 4          merchant, innkeeper, traveler, scholar (Ollama-backed)
  └── EventBus                    tick_start, tick_end, action_completed, brain_decided
```

### Agents

| ID            | Name   | Role       | Cognition        | Capabilities  | Behaviour                               |
|---------------|--------|------------|------------------|---------------|-----------------------------------------|
| guard_001     | Aldric | Guard      | Deterministic    | guard         | Waypoint patrol N → E → Town Square     |
| guard_002     | Berna  | Guard      | Deterministic    | guard         | Waypoint patrol S → W → Town Square     |
| villager_001  | Mira   | Villager   | Social           | social        | Cycles Square → Well → Market → Tavern  |
| villager_002  | Tomas  | Villager   | Social           | social        | Cycles Well → Square → Market           |
| villager_003  | Finn   | Villager   | Social           | social        | Cycles Square → Tavern → Well           |
| villager_004  | Dalia  | Villager   | Social           | social        | Cycles Market → Well → Square           |
| merchant_001  | Silas  | Merchant   | LLM (Ollama)     | social        | Near Market, seeks customers to trade   |
| farmer_001    | Edith  | Farmer     | Deterministic    | (none)        | Cycles Farm → Well → Market → Farm      |
| innkeeper_001 | Rogan  | Innkeeper  | LLM (Ollama)     | social        | Anchored to Tavern, shares gossip       |
| traveler_001  | Lyra   | Traveler   | LLM (Ollama)     | social        | Roams all locations, brief observations |
| scholar_001   | Wren   | Scholar    | LLM (Ollama)     | social        | Visits all POIs, initiates conversation |
| townsfolk_001 | Bram   | Townsfolk  | Social           | social        | Cycles Square → Well → Tavern           |
| townsfolk_002 | Nessa  | Townsfolk  | Social           | social        | Cycles Tavern → Square → Market         |

**Cognition types:**
- **Deterministic** — rule-based brain, no LLM, predictable paths
- **Social** — deterministic brain with probabilistic greetings when near other agents
- **LLM (Ollama)** — full language model decision-making with personality + social context

### Capabilities

| Capability | Who has it    | Unlocks                                              |
|------------|---------------|------------------------------------------------------|
| `social`   | 10 agents     | `socialize` action, `nearby_agents` + `social_relationships` observations |
| `guard`    | 2 guards      | No extra actions (guards don't socialize)            |
| (none)     | Edith (farmer)| Universal actions only: navigate, speak, interact, idle |

### Village POI coordinates (x, z)

| Location   | x   | z   |
|------------|-----|-----|
| TownSquare |   0 |   0 |
| Well       |   6 |   4 |
| Market     |  14 |   0 |
| Tavern     | -10 |   5 |
| Farm       |   2 | -14 |
| NorthGate  |   0 |  14 |
| SouthGate  |   0 | -14 |

---

## Social system

### VillageRelationships

Every pair of social agents tracks two values:

| Metric      | Range  | Meaning                          |
|-------------|--------|----------------------------------|
| familiarity | 0 → 1  | How often they have interacted   |
| affinity    | -1 → 1 | Positive or negative feeling     |

Each `socialize` action increases familiarity by 0.05 and nudges affinity +0.02.
Relationships are summarised into natural language injected into LLM prompts:

```
Your relationships:
  Mira (familiar+), Silas (acquaintance~), Lyra (stranger-)
```

### socialize action

`socialize` is a **HYBRID** action — it runs Python-side (relationship update) *and*
emits a host command so Unity can animate the interaction.

```json
{
  "action": "socialize",
  "target": "villager_001",
  "parameters": { "target_id": "villager_001", "message": "Fine morning for a walk!" }
}
```

Unity response: the actor turns to face the target, flashes pink for ~3 seconds, and
logs the exchange in the Social + Event Log.

### SocialVillagerBrain

Deterministic agents with the `social` capability use `SocialVillagerBrain`. When
idle at a POI with nearby agents, they probabilistically greet them:

- `social_chance` (default 0.45): probability of choosing `socialize` per tick
- `speak_chance` (default 0.25): probability of choosing `speak` when no nearby agent
- 6-tick cooldown per target — no repeated greetings to the same agent

---

## Observation providers

Agents receive structured perception data from five providers, registered in
`examples/village/sim/obs_registry.py`:

| Provider             | Keys produced                          | Capability filter |
|----------------------|----------------------------------------|-------------------|
| `TimeOfDayProvider`  | `simulation_tick`, `time_of_day`       | universal         |
| `SelfStateProvider`  | `nearest_poi`, `position`              | universal         |
| `NearbyPoiProvider`  | `nearby_pois` (list, sorted by dist)   | universal         |
| `NearbyAgentsProvider`| `nearby_agents`, `nearby_count`       | social            |
| `SocialMemoryProvider`| `social_relationships`               | social            |

These schemas also render into LLM system prompts so models know what keys to
expect in their perception:

```
OBSERVATION FIELDS (these keys appear in your perception):
  nearby_agents — List of agents within 12m. Fields: id, name, distance, role ...
  social_relationships — Text summary of your relationships with other villagers ...
```

---

## Prerequisites

| Requirement    | Version                                  |
|----------------|------------------------------------------|
| Python         | 3.11+                                    |
| biomata-engine | this repo                                |
| PyYAML         | `pip install pyyaml`                     |
| httpx          | `pip install httpx`                      |
| websockets     | `pip install "biomata-engine[websocket]"`|
| Unity          | 6000.0 (Unity 6)                         |
| Ollama         | latest (for 4 LLM agents)               |

---

## Quick start

### 1. Install the backend

```bash
cd /path/to/biomata-engine
pip install -e ".[websocket]"
```

### 2. Start Ollama (for LLM agents)

```bash
# Install from https://ollama.com if not already installed
ollama serve

# Pull the model (first run only — ~8 GB download)
ollama pull qwen2.5:14b
```

**Alternative lighter models** (edit `llm.model` in `sim.yaml`):
- `ollama pull qwen2.5:7b`   (faster, slightly less coherent)
- `ollama pull llama3.2:3b`  (smallest, ~2 GB, works on CPU)

### 3. Start the biomata backend

```bash
biomata-ws --config examples/village/sim.yaml --port 8765
```

You should see:
```
Biomata WebSocket server on 0.0.0.0:8765 | session ...
```

### 4. Open Unity

1. Open your Unity 6 project with the Biomata SDK installed.
2. Import the `VillageDemo` sample from **Package Manager → Biomata Simulation SDK → Samples → Village Demo → Import**.
3. Open a new empty scene.
4. Create an empty GameObject and attach the `VillageLifeDemo` component.
5. Press **Play**.

### 5. Run the demo

In the in-game HUD:
1. Press **Connect** — the backend will respond and 13 agents will appear.
2. Press **Start Auto** — ticks begin at the configured rate (default 0.2 Hz).
3. Watch the village come alive.

- Click any NPC capsule to inspect their last decision, role, cognition type, and social count.
- Use **< Prev / Next >** to cycle through agents in the inspector.
- Press **Force Tick** to fire an immediate tick.
- Press **Pause / Resume** to stop/start sending ticks.
- Press **Reset** to disconnect and reconnect.
- Watch the **Social + Event Log** at the bottom for socialisation exchanges.

---

## Tick rate tuning

The default tick rate is **0.2 Hz** (one tick every 5 seconds). This accounts for
4 concurrent Ollama LLM calls per tick.

| Hardware             | Recommended tick rate |
|----------------------|-----------------------|
| GPU with 16 GB VRAM  | 0.5 Hz (2 s/tick)     |
| GPU with 8 GB VRAM   | 0.2 Hz (5 s/tick)     |
| CPU-only Ollama      | 0.1 Hz (10 s/tick)    |
| Deterministic only   | 2 Hz (0.5 s/tick)     |

**Deterministic-only mode** (no Ollama required): comment out `merchant_001`,
`innkeeper_001`, `traveler_001`, and `scholar_001` in `sim.yaml` and use a higher
tick rate. The remaining 9 agents run entirely without LLM calls.

---

## Troubleshooting

**`Connection refused` when pressing Connect**
→ Confirm the backend is running: `biomata-ws --config examples/village/sim.yaml --port 8765`

**Ollama agents never produce a decision / tick takes forever**
→ Check Ollama is running: `curl http://localhost:11434/api/tags`
→ Check the model is pulled: `ollama list`
→ Try a smaller model in `sim.yaml` → `llm.model: qwen2.5:7b`

**Agents are stuck at their start positions**
→ They are waiting for the first tick. Press **Start Auto** or **Force Tick**.

**`agent already registered` error in backend logs**
→ Agents are declared in `sim.yaml`. If you restart Unity without restarting the backend,
  the backend still holds the agent state.
→ Restart the backend (`Ctrl+C` and re-run `biomata-ws --config ...`) to reset state.

**Speech bubbles not appearing**
→ The `EventVisualizer` component is added automatically. Speech bubbles appear when
  agents use `speak` or `socialize`.

**Socialize flash not visible**
→ The pink flash uses `MaterialPropertyBlock` on the agent capsule's renderer.
  Ensure the NPC material supports `_Color` (Standard or URP Lit shader).

---

## Extending the demo

**Add a new agent**
1. Add an entry in `examples/village/sim.yaml` → `agents:` with the desired `capabilities`
2. Add a matching `VillageAgentSpec` in `AgentSpecs[]` inside `VillageLifeDemo.cs`
3. Add the agent's ID/name/role to `AGENT_NAMES` and `AGENT_ROLES` in `examples/village/sim/social.py`
4. Restart the backend and reconnect in Unity

**Change a brain**
Edit `brain.class` in `sim.yaml` and restart the backend. No Unity changes needed.

**Add a new action**
1. Define a handler in `examples/village/sim/handlers.py`
2. Register it with an `ActionSchema` in `examples/village/sim/registry.py`
   - Set `kind=ActionKind.HOST` if Unity executes it, `ENGINE` if Python handles it, `HYBRID` for both
   - Set `tags=frozenset({"social"})` (or another capability) to restrict which agents can use it
3. If the action produces a host command, handle it in Unity via `bridge.OnDecisionReceived`

**Add a new observation provider**
1. Create a class with `observe(agent_id, capabilities, world) -> dict[str, Any]`
2. Define an `ObservationSchema` describing the keys it produces
3. Register both in `examples/village/sim/obs_registry.py`
4. The keys will automatically appear in LLM prompts and agent perception
