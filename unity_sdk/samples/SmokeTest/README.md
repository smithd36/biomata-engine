# Biomata SDK — Smoke Test

A one-script Unity validation scene that proves the Biomata SDK compiles into a Unity project and communicates with a running biomata-engine backend.

Use this before building larger demos (village sim, gameplay prototypes, etc.) to verify Unity ↔ Biomata end-to-end health.

This smoke test validates the intended **host-driven middleware architecture**:

Unity (host runtime)
→ WebSocket transport
→ Biomata simulation backend
→ agent cognition / decisions
→ event stream
→ Unity callbacks / action execution

---

## What it checks

1. The `BiomataSDK` assembly compiles into Unity.
2. `BiomataManager` instantiates correctly.
3. `ConnectAsync()` reaches the Biomata backend over WebSockets.
4. `HealthCheck` round-trips successfully.
5. Agent registration works.
6. Event streaming works (`tick_end`, `action_completed`).
7. Host-driven ticking works (`TickAsync()`).
8. Backend decision generation works.
9. Unity receives and logs simulation events on the main thread.

This is the minimum proof that Biomata can function as **Unity AI simulation middleware**.

---

## Setup

### 1. Start the Python backend

From the repo root:

```bash
pip install -e ".[websocket]"
```

If using example simulations with Python module imports:

```powershell
$env:PYTHONPATH="C:\path\to\biomata-engine"
```

Then launch:

```bash
biomata-ws --config examples/corporate/sim.yaml --port 8765
```

Expected output:

```text
WebSocket server listening on ws://0.0.0.0:8765
```

---

### 2. Add the SDK to a Unity 6 project

Primary target:

```text
Unity 6.x
(verified with 6000.4.8f1)
```

Add package via:

**Window → Package Manager → + → Add package from disk…**

Select:

```text
unity_sdk/package.json
```

Or add to `Packages/manifest.json`:

```json
"com.biomata.sdk": "file:../path/to/biomata-engine/unity_sdk"
```

Unity will automatically install:

```text
com.unity.nuget.newtonsoft-json
```

which the WebSocket transport depends on.

---

### 3. Import the sample

Package Manager:

**Biomata Simulation SDK → Samples → Import → Smoke Test**

Sample path:

```text
Assets/Samples/Biomata Simulation SDK/<version>/Smoke Test/
```

---

### 4. Unity input settings

If buttons throw input exceptions:

Go to:

```text
Edit → Project Settings → Player → Active Input Handling
```

Set:

```text
Both
```

Then restart Unity.

(Unity 6 defaults to the new Input System only; the runtime-generated sample UI uses classic EventSystem input.)

---

### 5. Run

1. Open or create an empty scene
2. Create:

```text
Hierarchy → Create Empty
```

Name it:

```text
SmokeTest
```

3. Add component:

```text
Biomata → Samples → Smoke Test
```

4. Configure inspector:

Recommended defaults:

```text
Transport: WebSocket
Host: localhost
Port: 8765
Test Agent ID: smoke_agent_001
Brain: IdleBrain
```

5. Press Play

The sample generates its entire UI at runtime:

- Canvas
- EventSystem
- control buttons
- event log
- status display

No scene setup required.

---

## Using the UI

| Button | What it does |
|--------|--------------|
| **Connect** | Opens WebSocket connection and starts event stream subscription |
| **Health Check** | Calls backend health endpoint |
| **Register Agent** | Registers `smoke_agent_001` with `IdleBrain` |
| **Force Tick** | Executes one host-driven simulation tick |
| **Pause** | Pauses backend simulation session |
| **Resume** | Resumes backend simulation session |

---

## Expected output (happy path)

### Connection

```text
[SmokeTest] connecting…
[SmokeTest] connected
```

---

### Health

```text
[SmokeTest] health: ok state=created tick=0 agents=5
```

---

### Registration

```text
[SmokeTest] registered: smoke_agent_001 (SmokeBot)
```

---

### Tick execution

```text
[SmokeTest] forcing tick...
[SmokeTest] tick complete: t1
```

---

### Decisions

Example:

```text
[SmokeTest] decision: smoke_agent_001 -> idle
```

Or, with richer simulations:

```text
[SmokeTest] decision: agent_004 -> pitch_idea
[SmokeTest] decision: agent_005 -> gossip
```

---

### Event streaming

```text
[SmokeTest] action @ t1: smoke_agent_001 → idle
[SmokeTest] tick_end @ t1
```

This proves:

- tick execution
- backend cognition
- event propagation
- Unity event dispatch

---

## Troubleshooting

### Connect fails

Example:

```text
connect failed: WebSocket connect to ws://localhost:8765 failed
```

Cause:

- backend not running
- wrong host/port
- firewall issue

Fix:

```bash
biomata-ws --port 8765
```

---

### Backend import errors

Example:

```text
No module named 'examples'
```

Cause:

example configs reference Python-imported modules.

Fix:

```powershell
$env:PYTHONPATH="C:\path\to\biomata-engine"
```

Or properly package `examples`.

---

### Newtonsoft.Json missing

Errors referencing:

```text
Newtonsoft.Json
JObject
JToken
```

Cause:

Unity package dependency failed.

Fix:

Reimport package or confirm:

```text
com.unity.nuget.newtonsoft-json
```

is installed.

---

### Buttons visible but unclickable

Cause:

Input System mismatch.

Fix:

```text
Player Settings → Active Input Handling → Both
```

Restart Unity.

---

### No events after connect

If connected but no ticks:

This is expected.

The smoke test validates **host-driven ticking**, not autonomous backend ticking.

Press:

```text
Force Tick
```

to advance simulation.

---

## Architectural note

This sample intentionally validates the host-driven middleware model:

Unity owns:

- scene state
- update loop
- visuals
- gameplay timing

Biomata owns:

- simulation logic
- cognition
- decision generation
- event emission

That separation is intentional and forms the basis for game engine integrations.

---

## Where to next

Once this smoke test passes, the next milestone is visual validation:

**one visible Unity agent moving because Biomata generated a decision.**

After that:

- multi-agent demo scene
- village simulation
- action handler expansion
- performance scaling
- Unreal / Godot adapters