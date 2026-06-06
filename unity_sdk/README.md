# Biomata Simulation SDK — Unity Client

Unity C# client SDK for the [biomata-engine](../README.md) backend.

The SDK uses **JSON over WebSocket** as its transport — text frames over `System.Net.WebSockets.ClientWebSocket`. This works on every platform Unity 6 targets and needs no code generation. See `docs/websocket-protocol.md` for the authoritative wire-format spec.

---

## Requirements

| Requirement | Minimum |
|---|---|
| Unity | **6000.0** (Unity 6.x — primary target is 6.4) |
| Scripting Backend | Mono **or** IL2CPP |
| API Compatibility Level | .NET Standard 2.1 |
| Python backend | biomata-engine 0.5.0 |

---

## Installation

### Option A — Local path reference (development)

Edit `Packages/manifest.json` in your Unity project:

```json
{
  "dependencies": {
    "com.biomata.sdk": "file:../path/to/biomata-engine/unity_sdk",
    "com.unity.nuget.newtonsoft-json": "3.2.1"
  }
}
```

Replace the path with the actual relative (or absolute) path from your Unity project to the `unity_sdk/` directory.

### Option B — Package Manager UI

**Window → Package Manager → + → Add package from disk…** and select `unity_sdk/package.json`.

The SDK declares `com.unity.nuget.newtonsoft-json 3.2.1` as a UPM dependency. Unity resolves it automatically from the default registry.

---

## Package structure

```
unity_sdk/
├── package.json                     # UPM manifest (name: com.biomata.sdk, version: 0.5.0)
├── Runtime/
│   ├── BiomataSDK.asmdef            # assembly definition
│   ├── Clients/                     # transport-agnostic sub-clients
│   │   ├── HealthClient.cs          # health_check
│   │   ├── AgentClient.cs           # register_agent, remove_agent
│   │   ├── ObservationClient.cs     # send_observation
│   │   ├── TickClient.cs            # tick, pause, resume
│   │   ├── EventStreamClient.cs     # subscribe_events, unsubscribe_events
│   │   └── SnapshotClient.cs        # snapshot, restore
│   ├── Transport/                   # ITransport contract + WebSocket implementation
│   │   ├── ITransport.cs
│   │   ├── WebSocketTransport.cs
│   │   └── JsonHelpers.cs
│   ├── Core/                        # SimulationClient, BiomataConfig, helpers
│   ├── Integration/                 # MonoBehaviour glue (BiomataAgent)
│   ├── Models/                      # DTOs: AgentRegistration, TickResult, AgentDecisionData, …
│   └── Unity/                       # BiomataManager singleton
├── Editor/                          # Custom Inspector overrides and validator tools
├── Samples~/
│   ├── SmokeTest/                   # Minimal connect + health check + register scene
│   ├── PatrolDemo/                  # Two NPC capsules driven by WaypointBrain (no LLM)
│   └── VisualDemo/                  # LLM pipeline + 20-agent orchestration demo
└── CHANGELOG.md
```

---

## Setup and initialization

### 1. Start the Python backend

```bash
# Engine-owned (agents declared in sim.yaml)
pip install -e ".[websocket]"
biomata-ws --config examples/engine_owned/sim.yaml --port 8765

# Host-owned (Unity registers agents at runtime)
biomata-ws --config examples/host_owned/sim.yaml --port 8765
```

### 2. Add `BiomataManager` to a scene

Create a persistent `GameObject` (e.g., tagged `DontDestroyOnLoad`) and attach the `BiomataManager` component. The Inspector exposes:

| Field | Default | Description |
|---|---|---|
| Host | `127.0.0.1` | Backend hostname or IP |
| Port | `8765` | Backend port |
| UseTls | `false` | Enable `wss://` transport |
| Retry | (config) | Reconnect policy (see `BiomataConfig.Retry`) |

`BiomataManager` is a singleton. Access it via `BiomataManager.Instance`.

### 3. Connect

`BiomataManager` connects automatically on `Awake`. You can also trigger it manually:

```csharp
await BiomataManager.Instance.ConnectAsync();
```

---

## Agent ownership models

### Engine-owned (BindToExisting)

The backend declares agents in `sim.yaml`. Unity binds visual MonoBehaviours to already-registered agents without sending a `register_agent` RPC.

```csharp
// BiomataAgent component: set AgentOwnershipMode = BindToExisting
// agentId must match the id in sim.yaml exactly (case-sensitive)
[SerializeField] private string agentId = "gate_guard_01";
[SerializeField] private AgentOwnershipMode mode = AgentOwnershipMode.BindToExisting;
```

The `BiomataAgent` component configures itself in the Inspector. Set **Ownership Mode** to **Bind To Existing** and enter the matching `id` from `sim.yaml`.

**Use when:** Agent roster is fixed; backend is the source of truth; multiple Unity clients spectate the same simulation.

### Host-owned (CreateAtRuntime)

Unity owns the full lifecycle: it registers the agent on spawn and unregisters on destroy.

```csharp
// BiomataAgent component: set AgentOwnershipMode = CreateAtRuntime
// BiomataAgent auto-calls register_agent on Start() and remove_agent on OnDestroy()
[SerializeField] private AgentOwnershipMode mode = AgentOwnershipMode.CreateAtRuntime;
[SerializeField] private string agentName = "Villager";
[SerializeField] private string role = "Villager";
```

Or call the API directly:

```csharp
await BiomataManager.Instance.Client.Agents.RegisterAsync(new AgentRegistration
{
    AgentId   = "npc_01",
    AgentName = "Mira",
    Role      = "Villager",
});
```

**Use when:** Procedural spawning; level-load swaps; dynamic roster; runtime brain hot-swap.

---

## Core API

### SimulationClient

`BiomataManager.Instance.Client` is a `SimulationClient` — a facade with five sub-clients:

```csharp
SimulationClient client = BiomataManager.Instance.Client;

client.Health       // HealthClient
client.Agents       // AgentClient
client.Observations // ObservationClient
client.Ticks        // TickClient
client.Events       // EventStreamClient
client.Snapshots    // SnapshotClient
```

### Ticking

In `host-driven` mode (default), Unity controls the tick rate:

```csharp
// Build observation payloads
var observations = new List<AgentObservationData>
{
    new AgentObservationData("gate_guard_01",
        new Dictionary<string, object>
        {
            ["location"]      = "north_gate",
            ["nearby_agents"] = new[] { "villager_01" },
            ["time_of_day"]   = 14,
        }),
};

// Tick — returns all agent decisions
TickResult result = await client.Ticks.TickAsync(observations);

foreach (AgentDecisionData decision in result.Decisions)
{
    switch (decision.Action)
    {
        case "move":     ApplyMove(decision);    break;
        case "speak":    ApplySpeak(decision);   break;
        case "patrol":   ApplyPatrol(decision);  break;
        case "idle":     /* do nothing */        break;
    }
}
```

In `autonomous` mode, the backend drives its own loop. Call `pause` / `resume` to control it:

```csharp
await client.Ticks.PauseAsync();
await client.Ticks.ResumeAsync();
```

### Agent registration

```csharp
// Register
await client.Agents.RegisterAsync(new AgentRegistration
{
    AgentId    = "npc_guard_01",
    AgentName  = "Aldric",
    Role       = "Guard",
    BrainClass = "src.plugins.builtin.idle_brain.brain.IdleBrain",  // optional override
});

// Unregister
await client.Agents.RemoveAsync("npc_guard_01");
```

### Observations (stand-alone)

Send observations outside of a tick (for pre-warm or ExternalWorld push pattern):

```csharp
await client.Observations.SendAsync("npc_guard_01",
    new Dictionary<string, object> { ["alert_level"] = 3 });
```

### Health check

```csharp
HealthResult health = await client.Health.CheckAsync();
Debug.Log($"Backend: {health.Status} v{health.Version}");
```

### Snapshot and restore

```csharp
// Take a snapshot
SnapshotData snap = await client.Snapshots.SnapshotAsync();
string snapId = snap.SnapshotId;

// ... run more ticks, something goes wrong ...

// Restore
await client.Snapshots.RestoreAsync(snapId);
```

---

## Event subscription

The `EventStreamClient` delivers real-time events pushed by the backend. Subscribe before the first tick:

```csharp
// Handler signature: Action<ServiceEvent>
client.Events.On("tick_end", ev =>
    Debug.Log($"Tick {ev.Tick} completed"));

client.Events.On("agent_registered", ev =>
    Debug.Log($"Agent joined: {ev.AgentId}"));

client.Events.On("agent_unregistered", ev =>
    Debug.Log($"Agent left: {ev.AgentId}"));

client.Events.On("action_completed", ev =>
{
    string action = ev.Data.GetString("action");
    UpdateHUD(ev.AgentId, action);
});

client.Events.On("agent_step_error", ev =>
    Debug.LogError($"Step error for {ev.AgentId}: {ev.Data.GetString("message")}"));

// Unsubscribe
client.Events.Off("tick_end");
```

`ServiceEvent` fields: `EventType`, `Tick`, `AgentId`, `SessionId`, `Seq`, `Timestamp`, `Data`.

---

## BiomataAgent component

`BiomataAgent` is a drag-attach `MonoBehaviour` that wires a Unity `GameObject` to one backend agent. It auto-manages the observation → tick → apply-decision loop and owns the agent lifecycle when in `CreateAtRuntime` mode.

Configure it in the Inspector:

| Field | Description |
|---|---|
| Agent Id | Must match `id` in `sim.yaml` (BindToExisting) or is the runtime ID you supply (CreateAtRuntime) |
| Agent Name | Display name (used for registration) |
| Role | Role key from `sim.yaml` (used for registration) |
| Ownership Mode | `BindToExisting` or `CreateAtRuntime` |
| Brain Config | Optional JSON string passed through to the backend brain constructor |

---

## Samples

Import samples via **Window → Package Manager → Biomata Simulation SDK → Samples → [Sample name] → Import**.

### Smoke Test

Minimal scene with **Connect**, **Health Check**, and **Register Agent** buttons plus a live event log. Verifies the SDK compiles and connects end-to-end. No sim.yaml needed.

Backend: none required for the compile check; `biomata-ws` with any config for the connection test.

### Patrol Demo

Two NPC capsules walk waypoint loops, driven entirely by a Python `WaypointBrain` over WebSocket. No LLM required — demonstrates the full tick pipeline with a deterministic brain.

```bash
biomata-ws --config examples/patrol/sim.yaml --port 8765
```

### Visual Demo

Two demos in one scene:
- **VisualValidationDemo** — one cube driven by `OllamaLLMBrain` end-to-end
- **MultiAgentOrchestrationDemo** — 20 white cubes, each driven by `OllamaLLMBrain`, proving concurrent multi-agent orchestration

```bash
biomata-ws --config examples/visual_demo/sim.yaml --port 8765
```

Requires a running Ollama instance (`ollama serve`).

---

## Wire protocol

Transport: JSON over WebSocket (text frames). See `docs/websocket-protocol.md` for the full spec.

Frame shapes:

```
Server → Client (on connect):
  {"type":"hlo","v":1,"server":"biomata-engine","session_id":"<uuid>","capabilities":[...]}

Client → Server (method call):
  {"type":"req","v":1,"id":"<uuid>","method":"<name>","params":{...}}

Server → Client (response — success):
  {"type":"res","v":1,"id":"<uuid>","ok":true,"result":{...}}

Server → Client (response — error):
  {"type":"res","v":1,"id":"<uuid>","ok":false,"error":{"code":-32601,"name":"METHOD_NOT_FOUND","message":"..."}}

Server → Client (event stream):
  {"type":"evt","v":1,"session_id":"<uuid>","seq":42,"event_type":"tick_end","tick":5,"agent_id":"engine","ts":"...","data":{}}
```

Methods: `health_check`, `register_agent`, `remove_agent`, `send_observation`, `tick`, `pause`, `resume`, `snapshot`, `restore`, `subscribe_events`, `unsubscribe_events`.

---

## Connection resilience

- `EventStreamClient` raises `OnDisconnected` when the underlying stream drops.
- Per-call failures throw `BiomataException` — catch it at call sites to handle gracefully.
- Reconnect policy is configured in `BiomataConfig.Retry`.
- Automatic WebSocket reconnect is on the roadmap.

---

## Platform support

| Platform | Status |
|---|---|
| Editor (Win / macOS / Linux) | ✅ primary target |
| Standalone (Win / macOS / Linux) | ✅ Mono or IL2CPP |
| Android | ✅ IL2CPP (`link.xml` protects against stripping) |
| iOS | ✅ IL2CPP |
| WebGL | ❌ `System.Net.WebSockets.ClientWebSocket` is not available in browser WebGL without a JS shim |

---

## Troubleshooting

**`BiomataManager.Instance` is null**
Ensure `BiomataManager` is attached to a `GameObject` that is not destroyed between scenes. Use `DontDestroyOnLoad` on its parent.

**Connection refused / timeout**
Check that `biomata-ws` is running and listening on the correct host/port. Default is `127.0.0.1:8765` (loopback only). For device testing (Android, iOS), start the backend with `--host 0.0.0.0` and set the `Host` field in `BiomataManager` to your machine's LAN IP.

**`BindToExisting` agent never receives decisions**
The `agentId` on the `BiomataAgent` component must exactly match the `id` field in `sim.yaml`. Check case, spaces, and underscores.

**Tick returns empty `Decisions` list**
The backend received the tick but no agents stepped. Verify that agents are registered (check the backend console log). If in `BindToExisting` mode, the simulation must be running before Unity connects.

**`BiomataException` on register with "agent already exists"**
The agent was already registered in a previous session and the sim was not restarted. Either remove the agent first or restart `biomata-ws`.

**IL2CPP stripping removes SDK types**
`BiomataSDK.asmdef` marks all types as preserved. If you hit missing-type errors on IL2CPP builds, add a `link.xml` entry for `Biomata.SDK`.

**Newtonsoft.Json version conflict**
The package requires `com.unity.nuget.newtonsoft-json 3.2.1`. If your project pins a different version, resolve it in `Packages/manifest.json`.

**WebGL not connecting**
`System.Net.WebSockets.ClientWebSocket` is unavailable in WebGL. You need a JavaScript WebSocket shim bridged to C# via `jslib`. This is not provided by the SDK; WebGL support is on the roadmap.

---

## Development and contribution

See [`CONTRIBUTING.md`](../CONTRIBUTING.md) for the full guide.

Quick summary for SDK work:
- All C# code lives in `unity_sdk/Runtime/` and `unity_sdk/Editor/`
- Assembly: `BiomataSDK.asmdef`
- No code generation — everything is hand-authored against the protocol spec
- Protocol changes must be reflected in the sub-client that owns that method
- New `ITransport` implementations must implement the full interface, including the `hlo` handshake
- Add samples under `Samples~/` following the pattern of `SmokeTest`
- Apache 2.0 License
