# Biomata SDK — Visual Demo

This sample contains two components. Use the one that matches your goal.

| Component | Agents | Brain | Purpose |
|-----------|--------|-------|---------|
| `VisualValidationDemo` | 1 | `OllamaLLMBrain` | Prove the full LLM pipeline end-to-end |
| `MultiAgentOrchestrationDemo` | 20 | `WaypointBrain` | Prove concurrent multi-agent orchestration |

Both use the same Python backend: `biomata-ws --config examples/visual_demo/sim.yaml --port 8765`

---

# Visual Validation Demo (single agent, LLM)

End-to-end proof that a real LLM drives visible gameplay in Unity:

End-to-end proof that a real LLM drives visible gameplay in Unity:

```
OllamaLLMBrain (Python) → navigate intent → NavigateHandler → engine_command
  → WebSocket transport
    → InterruptibleMoveHandler → visible cube movement in Unity
```

A yellow cube NPC wanders a flat plane. Each tick, Python calls Ollama, gets a target coordinate, and returns a `navigate` command. Unity moves the cube to that coordinate. No fake movement, no hardcoded paths.

Drop `VisualValidationDemo` on an empty GameObject and press Play.

---

## What you see

- **Yellow cube** (Wanderer) on a dark plane — turns **green** when actively navigating
- **Cyan sphere** marker shows the active target coordinate
- **Decision log** (bottom left) — one line per tick: agent name, action, target coordinate, outcome text from the LLM
- **Event log** (bottom right) — raw event stream from the backend
- **Status bar** (top) — connection state and tick rate

---

## Prerequisites

### 1. Ollama

Install Ollama and pull the model:

```bash
ollama pull qwen2.5:14b
```

Ollama must be running at `http://localhost:11434` (or override in the Inspector).

Any Ollama-compatible model works. Smaller models (`qwen2.5:3b`, `llama3.2:3b`) run faster but produce less coherent navigate targets.

---

### 2. Start the Python backend

From the repo root:

```bash
pip install -e ".[websocket]"
```

Set `PYTHONPATH`:

```powershell
$env:PYTHONPATH="C:\path\to\biomata-engine"
```

Then launch:

```bash
biomata-ws --config examples/visual_demo/sim.yaml --port 8765
```

Expected output:

```text
WebSocket server listening on ws://0.0.0.0:8765
```

The `sim.yaml` starts with **no pre-configured agents**. The agent (`npc_001`) is registered at runtime from Unity, which sends the full `OllamaLLMBrain` configuration (model, personality, `llm_config`) via `AgentRegistration.BrainConfig`.

---

### 3. Add the SDK to a Unity 6 project

Add via **Window → Package Manager → + → Add package from disk…** and select:

```text
unity_sdk/package.json
```

Or add to `Packages/manifest.json`:

```json
"com.biomata.sdk": "file:../path/to/biomata-engine/unity_sdk"
```

Unity pulls `com.unity.nuget.newtonsoft-json` automatically.

---

### 4. Import the sample

**Biomata Simulation SDK → Samples → Import → Visual Validation Demo**

Sample path after import:

```text
Assets/Samples/Biomata Simulation SDK/<version>/Visual Validation Demo/
```

---

### 5. Unity input settings

```text
Edit → Project Settings → Player → Active Input Handling → Both
```

Restart Unity. (Unity 6 defaults to the new Input System; the runtime UI uses legacy `EventSystem`.)

---

## Run

1. Open or create an empty scene
2. Create an empty GameObject (any name, e.g. `VisualDemo`)
3. Add component: **Biomata → Samples → Visual Validation Demo**
4. Configure the Inspector (see reference table below)
5. Press Play
6. Click **Connect**
7. Click **Register Agent**
8. Watch the cube move

---

## Interaction

| Button | What it does |
|--------|--------------|
| **Connect** | Opens WebSocket connection, starts event stream |
| **Register Agent** | Sends `npc_001` to Python with `OllamaLLMBrain` personality and `llm_config` |
| **Force Tick** | Fires an extra tick immediately; useful for testing |

Connection is manual (not auto-connect) so you can inspect logs before ticks begin.

---

## Inspector reference

| Field | Default | Notes |
|-------|---------|-------|
| Host | `localhost` | Backend hostname or IP |
| Port | `8765` | Must match `--port` on backend |
| Transport | `WebSocket` | Use `WebSocket` for `biomata-ws` |
| Tick Rate | `0.3` | Ticks/s — keep ≤ 0.5; Ollama needs time between calls |
| Agent ID | `npc_001` | Must be unique on the backend |
| Agent Name | `Wanderer` | Display name sent to Python |
| Ollama Model | `qwen2.5:14b` | Any model available in your Ollama instance |
| Ollama Base URL | `http://localhost:11434` | Override for remote Ollama |
| LLM Temperature | `0.7` | Higher = more varied targets |
| Move Speed | `8` | Units/s for cube movement |

---

## Expected output

### After Connect

```text
Connected to localhost:8765  |  Click Register Agent to start
[tick_end] engine
```

### After Register Agent

```text
Connected  |  Agent registered  |  Ticking at 0.30/s
registered 'npc_001' with OllamaLLMBrain
```

### Decision log (each tick)

```text
[14:32:01] t1  Wanderer: navigate (3.5, -6.2)  "moving to explore new area"
[14:32:04] t2  Wanderer: navigate (-7.1, 2.8)  "heading to unvisited region"
```

The cube moves to the coordinate in parentheses. Cube turns green while moving, back to yellow on arrival.

---

## Troubleshooting

### Connect fails

Backend not running. Verify:

```bash
biomata-ws --config examples/visual_demo/sim.yaml --port 8765
```

### Register Agent fails: "already registered"

The backend session persists between Play runs in the editor. Restart the Python backend and press Play again.

### Cube does not move

LLM returned a non-`navigate` action, or the backend returned an error. Check the Decision log for the action name and any error messages. If the model is too small it may return idle or unrecognised actions — try a larger model.

### Very slow ticks

Ollama inference time dominates. Lower `Tick Rate` to `0.2` or use a smaller model. The cube will still move correctly; ticks just space further apart.

### "No module named 'examples'"

Set PYTHONPATH:

```powershell
$env:PYTHONPATH="C:\path\to\biomata-engine"
```

### Cube jitters or snaps

The `InterruptibleMoveHandler` exits any in-flight movement when a new decision arrives. If `Tick Rate` is faster than the LLM's response time, the backend queues ticks and decisions arrive in bursts. Lower `Tick Rate` or increase `Move Speed` so the cube reaches each target before the next decision.

---

## How registration works

`OllamaLLMBrain` requires a personality and `llm_config` at construction time. The demo sends these from Unity as `AgentRegistration.BrainConfig`:

```
Unity → register_agent RPC
  → BrainConfig["llm_config"] = { model, base_url, temperature }
  → BrainConfig["personality"] = { traits, goals, backstory }
```

Python's WebSocket handler calls `OllamaLLMBrain(**brain_config)` directly. No sim.yaml agent entry is needed — and intentionally avoided, because a YAML-declared agent would conflict with Unity's runtime registration.

The personality instructs the LLM to always choose `navigate`, pick targets ≥ 3 units away, stay within ±9 on both axes, and never idle.

---

## Architecture note

`VisualValidationDemo` builds the entire scene with no prefabs:

- `UnitySimulationManager` drives the tick loop (`autoConnect = false` — Connect button controls this)
- NPC stack: `TransformObservationProvider` → `ObservationCollector` → `InterruptibleMoveHandler` → `ActionExecutor` → `UnityAgentBridge`
- `UnityAgentBridge` has `autoRegister = false` — Register Agent button sends the full `AgentRegistration`
- `InterruptibleMoveHandler` extends `MoveActionHandler` with a token-based interrupt so new decisions cancel in-flight movement immediately

Python never knows about Unity. It sees `position_x` / `position_z` floats, calls Ollama, and returns a `navigate` engine command. Unity owns visuals and timing; Biomata owns cognition.

---

## Where to next

- **Add more agents** — call `RegisterAsync` for additional `AgentRegistration` entries (each with its own personality)
- **Swap the model** — `llama3.2:3b` for speed; `qwen2.5:32b` for richer reasoning
- **Extend the observation** — add inventory or nearby-agent data in `TransformObservationProvider` and the LLM will reason about it
- **Add action handlers** — register new action names (e.g. `interact`, `speak`) in the Python registry and teach the LLM about them via the personality `goals`
- **Scale to many agents** — see `MultiAgentOrchestrationDemo` in this sample

---

---

# Multi-Agent Orchestration Demo (20 agents, WaypointBrain)

Proves that Biomata orchestrates many agents concurrently through a single WebSocket session.

```
Python WaypointBrain × 20 → navigate engine_commands (simultaneous)
  → WebSocket transport (single connection)
    → 20 MoveActionHandlers → 20 cubes patrolling simultaneously
```

20 colored cubes arranged in a 4×5 grid each patrol a small square loop around their grid position. All 20 registrations are sent in parallel at connect time. Python processes them in one tick cycle using `SimultaneousScheduler`.

No LLM required — `WaypointBrain` is pure Python arithmetic, so 3–5 Hz tick rates are achievable with no warmup time.

---

## What you see

- **20 cubes** filling a 4×5 grid — each a unique color from the HSV wheel
- Dim **cylinder markers** at each cube's four waypoints showing the patrol square
- Cubes **brighten** when actively moving, return to base color on arrival
- **Indicator grid** (top-left UI): 20 small colored squares that mirror the moving/idle state in real time
- **Stats panel** (top-right UI): Registered count, total ticks, total decisions, currently-moving count
- **Event log** (bottom): registration confirmations, then a line per tick showing decision count

---

## Setup

### 1. Start the Python backend

```bash
pip install -e ".[websocket]"
```

```powershell
$env:PYTHONPATH="C:\path\to\biomata-engine"
```

```bash
biomata-ws --config examples/visual_demo/sim.yaml --port 8765
```

The `sim.yaml` starts with no agents — all 20 are registered dynamically from Unity.

### 2. Add the SDK and import the Visual Demo sample

Same steps as the Visual Validation Demo above.

### 3. Run

1. Open or create an empty scene
2. Create empty GameObject → add component: **Biomata → Samples → Multi-Agent Orchestration Demo**
3. Configure Inspector (defaults work for a local backend)
4. Press Play → click **Connect** → watch all 20 cubes begin patrolling

---

## Inspector reference

| Field | Default | Notes |
|-------|---------|-------|
| Host | `localhost` | Backend hostname or IP |
| Port | `8765` | Must match `--port` on backend |
| Transport | `WebSocket` | `WebSocket` pairs with `biomata-ws` |
| Tick Rate | `3` | Ticks/s — safe to push to 5 for local backends |
| Move Speed | `5` | Units/s for cube movement |
| Waypoint Radius | `3` | Radius of each cube's square patrol loop |
| Grid Spacing | `9` | World-space distance between grid centres |

---

## Expected output

### After Connect

```text
[14:10:01] registered agent_001 (A01)
[14:10:01] registered agent_002 (A02)
...
[14:10:02] registered agent_020 (A20)
```

All 20 cubes begin moving within 1–2 seconds.

### Per tick

```text
[14:10:03] t1: 20 decisions
[14:10:03] t2: 20 decisions
[14:10:04] t3: 20 decisions
```

Stats panel updates in real time: `Moving: 15 / 20` etc.

---

## Troubleshooting

### Some agents never start moving

Python's `SimultaneousScheduler` processes all agents each tick. If an agent isn't moving it likely wasn't registered successfully — check the event log for `registered agent_XXX` confirmations. If missing, the backend returned an error; restart the backend and press Play again.

### Connect → immediate disconnect

Backend not running. Verify:

```bash
biomata-ws --config examples/visual_demo/sim.yaml --port 8765
```

### "No module named 'examples'"

```powershell
$env:PYTHONPATH="C:\path\to\biomata-engine"
```

### Cubes overlap

`Grid Spacing` is too small relative to `Waypoint Radius`. The rule: `gridSpacing > 2 × waypointRadius`. At defaults (9 and 3) there is 3 units of clearance between patrol squares.

---

## Architecture note

`MultiAgentOrchestrationDemo` builds the entire scene at runtime — no prefabs, no authoring:

- `UnitySimulationManager` drives the tick loop at `tickRate` Hz
- Per-cube stack (×20): `TransformObservationProvider` → `ObservationCollector` → `MoveActionHandler` → `ActionExecutor` → `UnityAgentBridge`
- All 20 bridges have `autoRegister = false`; parallel `RegisterAsync` calls in `RegisterAllAgents()` send `WaypointBrain` + waypoint config simultaneously
- Python receives 20 concurrent registration RPCs, creates 20 `WaypointBrain` instances, and processes them each tick via `SimultaneousScheduler`
- Waypoints are passed as `BrainConfig["waypoints"]` (list of `[x, z]` float arrays), deserialized directly into `WaypointBrain.__init__(waypoints=...)`

The indicator grid mirrors the `OnActionStarted` / `OnActionCompleted` events firing on each bridge — no polling, no shared state.
