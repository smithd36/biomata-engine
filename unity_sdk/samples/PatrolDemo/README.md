# Biomata SDK — Patrol Demo

The smallest visual proof that Biomata can drive visible gameplay behaviour in Unity.

Two capsule NPCs patrol waypoint loops continuously — driven entirely by **Python `WaypointBrain` decisions over WebSocket**. No LLM, no prefabs, no scene authoring required. Drop `PatrolDemoBootstrapper` on an empty GameObject and press Play.

What it proves:

```
Python WaypointBrain → navigate engine_command
  → WebSocket transport
    → MoveActionHandler → visible Transform movement in Unity
```

---

## What you see

| NPC | Color | Route |
|-----|-------|-------|
| **Scout** | Blue | Square: (0,0) → (8,0) → (8,8) → (0,8) → … |
| **Guard** | Red | Diamond: (4,−4) → (−4,−4) → (−4,4) → (4,4) → … |

Flat cylinder markers on the floor show each waypoint. A status bar at the top shows connection state.

The tick rate defaults to **2 ticks/s** — fast enough to see smooth movement without overwhelming a local backend.

---

## Prerequisites

### 1. Start the Python backend

From the repo root:

```bash
pip install -e ".[websocket]"
```

Set `PYTHONPATH` so the example modules resolve:

```powershell
$env:PYTHONPATH="C:\path\to\biomata-engine"
```

Then launch:

```bash
biomata-ws --config examples/patrol/sim.yaml --port 8765
```

Expected output:

```text
WebSocket server listening on ws://0.0.0.0:8765
```

The `sim.yaml` pre-registers `scout_001` and `guard_001` with `WaypointBrain`. No runtime registration is needed.

---

### 2. Add the SDK to a Unity 6 project

Add via **Window → Package Manager → + → Add package from disk…** and select:

```text
unity_sdk/package.json
```

Or add directly to `Packages/manifest.json`:

```json
"com.biomata.sdk": "file:../path/to/biomata-engine/unity_sdk"
```

Unity pulls `com.unity.nuget.newtonsoft-json` automatically.

---

### 3. Import the sample

**Biomata Simulation SDK → Samples → Import → Patrol Demo**

Sample path after import:

```text
Assets/Samples/Biomata Simulation SDK/<version>/Patrol Demo/
```

---

### 4. Unity input settings

If buttons throw input exceptions go to:

```text
Edit → Project Settings → Player → Active Input Handling → Both
```

Restart Unity. (Unity 6 defaults to the new Input System; the runtime HUD uses legacy `EventSystem`.)

---

## Run

1. Open or create an empty scene
2. Create an empty GameObject (any name, e.g. `PatrolDemo`)
3. Add component: **Biomata → Samples → Patrol Demo**
4. Configure the Inspector:

```text
Host:      localhost
Port:      8765
Transport: WebSocket
Tick Rate: 2
```

5. Press Play

The bootstrapper builds the entire scene at runtime — floor, waypoint markers, NPCs with name labels, camera, HUD. NPCs connect automatically and begin patrolling within a couple of seconds.

---

## Expected behaviour

### Connection (first 1–2 s)

```text
[Patrol Demo]  Connecting to localhost:8765…
[Patrol Demo]  Connected — NPCs patrolling
```

NPCs start moving immediately after the first tick.

### Patrol loop

Each NPC moves to the next waypoint, arrives, and advances to the following one indefinitely. The WaypointBrain has no awareness of Unity; it only reads `position_x` / `position_z` from the observation and returns a `navigate` intent.

---

## Inspector reference

| Field | Default | Notes |
|-------|---------|-------|
| Host | `localhost` | Backend hostname or IP |
| Port | `8765` | Must match `--port` on backend |
| Transport | `WebSocket` | Use `WebSocket` for the `biomata-ws` server |
| Tick Rate | `2` | Ticks per second; increase for faster movement |

---

## Troubleshooting

### NPCs don't move

Backend not running, or PYTHONPATH not set. Check the status label. Verify:

```bash
biomata-ws --config examples/patrol/sim.yaml --port 8765
```

### "No module named 'examples'"

Set PYTHONPATH:

```powershell
$env:PYTHONPATH="C:\path\to\biomata-engine"
```

### NPCs freeze mid-path

Tick rate too low or backend dropped. Status bar updates on disconnect. Increase `Tick Rate` in the Inspector (4–5 is fine for local backends).

### Waypoint markers missing

Shader not found. This can happen in URP projects if the Standard shader isn't included. The demo tries `Standard` then `Universal Render Pipeline/Lit`. In a URP project, add `Universal Render Pipeline/Lit` to **Always Included Shaders** in Graphics settings.

---

## Architecture note

`PatrolDemoBootstrapper` builds the entire scene with no prefabs or serialized references:

- `UnitySimulationManager` drives the tick loop (auto-connect, auto-tick at `tickRate` Hz)
- Each NPC has: `TransformObservationProvider` → `ObservationCollector` → `MoveActionHandler` → `ActionExecutor` → `UnityAgentBridge`
- `UnityAgentBridge` handles registration (agents are pre-declared in sim.yaml so no manual `BrainConfig` is needed) and routes decisions to `MoveActionHandler`

The Python side never knows about Unity — it only sees `position_x` / `position_z` floats and emits `navigate` intents. The architecture is intentionally host-authoritative: Unity owns transform state; Biomata owns cognition.

---

## Where to next

- **Visual Validation Demo** — same architecture, one agent, driven by a real LLM (`OllamaLLMBrain`)
- Add more waypoints or agents in `sim.yaml` and mirror them in the bootstrapper's `Agents` array
- Swap `WaypointBrain` for `OllamaLLMBrain` in `sim.yaml` to see LLM-driven patrol decisions
