# Biomata Village Life Sandbox

First-pass flagship showcase: 10 autonomous NPCs living in a primitive village,
driven by Biomata's hybrid cognition backend.

## What it demonstrates

**As game AI middleware**
- Autonomous NPCs that patrol, wander, idle, interact, and speak without scripted state machines
- Hybrid cognition: deterministic brains for predictable agents, LLM brains for socially interesting ones
- Real-time Unity integration over WebSocket — one backend session, many simultaneous NPCs

**As interactive simulation laboratory**
- Observe per-agent decisions in the live inspector
- Tune tick rate and watch how cognition speed affects emergent behaviour
- Swap brain classes in `sim.yaml` without touching Unity

---

## Architecture overview

```
Unity 6 (rendering + physics)
  └── VillageLifeDemo.cs          procedural scene + HUD + event handling
  └── UnitySimulationManager      WebSocket tick driver
  └── UnityAgentBridge × 10       per-NPC observation + decision routing
  └── MoveActionHandler           translates navigate commands → Transform movement
  └── SpeakActionHandler          speech bubbles + speak events
  └── InteractActionHandler       interact events

WebSocket (JSON, localhost:8765)

biomata-engine (Python)
  └── HostedWorld                 receives Unity observations, emits engine_commands
  └── SimultaneousScheduler       all 10 brains decide in parallel each tick
  └── VillagerBrain × 7           deterministic POI-cycling (no LLM)
  └── WaypointBrain × 2           guard patrol loop (no LLM)
  └── OllamaLLMBrain × 3          merchant, innkeeper, traveler (Ollama-backed)
  └── EventBus                    tick_start, tick_end, action_completed, brain_decided
```

### Agents

| ID              | Name   | Role       | Cognition     | Behaviour                                 |
|-----------------|--------|------------|---------------|-------------------------------------------|
| guard_001       | Aldric | Guard      | Deterministic | Waypoint patrol N → E → Town Square       |
| guard_002       | Berna  | Guard      | Deterministic | Waypoint patrol S → W → Town Square       |
| villager_001    | Mira   | Villager   | Deterministic | Cycles Square → Well → Market → Tavern    |
| villager_002    | Tomas  | Villager   | Deterministic | Cycles Well → Square → Market             |
| merchant_001    | Silas  | Merchant   | LLM (Ollama)  | Stays near Market, speaks to passersby    |
| farmer_001      | Edith  | Farmer     | Deterministic | Cycles Farm → Well → Market → Farm        |
| innkeeper_001   | Rogan  | Innkeeper  | LLM (Ollama)  | Anchored to Tavern, occasional Square     |
| traveler_001    | Lyra   | Traveler   | LLM (Ollama)  | Roams all locations, comments aloud       |
| townsfolk_001   | Bram   | Townsfolk  | Deterministic | Cycles Square → Well → Tavern             |
| townsfolk_002   | Nessa  | Townsfolk  | Deterministic | Cycles Tavern → Square → Market           |

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

## Prerequisites

| Requirement      | Version      |
|------------------|--------------|
| Python           | 3.11+        |
| biomata-engine   | this repo    |
| PyYAML           | `pip install pyyaml` |
| httpx            | `pip install httpx`  |
| websockets       | `pip install "biomata-engine[websocket]"` |
| Unity            | 6000.0 (Unity 6) |
| Ollama           | latest (for 3 LLM agents) |

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
1. Press **Connect** — the backend will respond and 10 agents will appear.
2. Press **Start Auto** — ticks begin at the configured rate (default 0.2 Hz).
3. Watch the village come alive.

- Click any NPC capsule to inspect their last decision.
- Use **< Prev / Next >** to cycle through agents in the inspector.
- Press **Force Tick** to fire an immediate tick.
- Press **Pause / Resume** to stop/start sending ticks.
- Press **Reset** to disconnect and reconnect.

---

## Tick rate tuning

The default tick rate is **0.2 Hz** (one tick every 5 seconds). This is conservative to
account for the 3 Ollama LLM calls that fire concurrently on each tick.

| Hardware             | Recommended tick rate |
|----------------------|-----------------------|
| GPU with 16 GB VRAM  | 0.5 Hz (2 s/tick)     |
| GPU with 8 GB VRAM   | 0.2 Hz (5 s/tick)     |
| CPU-only Ollama      | 0.1 Hz (10 s/tick)    |
| Deterministic only   | 2 Hz (0.5 s/tick)     |

Adjust the **Tick Rate** field on the `VillageLifeDemo` Inspector, or change it directly
in `sim.yaml` → not applicable (tick rate is Unity-side).

**Deterministic-only mode** (no Ollama required): comment out `merchant_001`,
`innkeeper_001`, and `traveler_001` in `sim.yaml` and use a higher tick rate.

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
→ `VillageLifeDemo` has `autoRegister=false` — agents are declared in `sim.yaml`.
→ If you restart Unity without restarting the backend, the backend still has the agents.
→ Restart the backend (`Ctrl+C` and re-run `biomata-ws --config ...`) to reset state.

**Speech bubbles not appearing**
→ The `EventVisualizer` component is added automatically. Speech bubbles display above
agents when they choose the `speak` action (LLM agents only).

---

## Extending the demo

**Add a new agent**
1. Add a new entry in `examples/village/sim.yaml` → `agents:`
2. Add a matching `VillageAgentSpec` in `AgentSpecs[]` inside `VillageLifeDemo.cs`
3. Restart the backend and reconnect in Unity

**Change a brain**
Edit the `brain.class` in `sim.yaml` and restart the backend. No Unity changes needed.

**Add a new action**
1. Add a handler in `examples/village/sim/handlers.py`
2. Register it in `examples/village/sim/registry.py`
3. Add a matching `ActionHandlerBase` subclass on each NPC in `VillageLifeDemo.cs`
