# Biomata Simulation SDK — Unity Client

Unity C# client SDK for the [biomata-engine](../README.md) backend.

The SDK uses **JSON over WebSocket** as its transport — text frames over
`System.Net.WebSockets.ClientWebSocket`. This works on every platform Unity 6
targets and needs no code generation. See `docs/websocket-protocol.md` for the
authoritative wire-format spec.

## Requirements

| Requirement | Minimum |
|---|---|
| Unity | **6000.0** (Unity 6.x — primary target is 6.4) |
| Scripting Backend | Mono **or** IL2CPP |
| API Compatibility Level | .NET Standard 2.1 |
| Python backend | biomata-engine 0.5.0 |

## Installation

In your Unity project, edit `Packages/manifest.json` and add:

```json
{
  "dependencies": {
    "com.biomata.sdk": "file:../path/to/biomata-engine/unity_sdk",
    "...": "..."
  }
}
```

Or use **Window → Package Manager → + → Add package from disk…** and select
`unity_sdk/package.json`.

The SDK declares `com.unity.nuget.newtonsoft-json` as a UPM dependency; Unity
pulls it automatically from the default registry.

## What ships in the package

```
unity_sdk/
├─ package.json
├─ Runtime/
│  ├─ BiomataSDK.asmdef
│  ├─ Clients/                      # transport-agnostic sub-clients
│  │  ├─ HealthClient.cs
│  │  ├─ AgentClient.cs
│  │  ├─ ObservationClient.cs
│  │  ├─ TickClient.cs
│  │  ├─ EventStreamClient.cs
│  │  └─ SnapshotClient.cs
│  ├─ Transport/                    # ITransport contract + WebSocketTransport
│  │  ├─ ITransport.cs
│  │  ├─ WebSocketTransport.cs
│  │  └─ JsonHelpers.cs
│  ├─ Core/                         # SimulationClient, BiomataConfig, helpers
│  ├─ Integration/                  # MonoBehaviour glue
│  ├─ Models/                       # DTOs (AgentRegistration, TickResult, …)
│  └─ Unity/                        # BiomataManager singleton
├─ Editor/                          # Inspector overrides
├─ Samples~/SmokeTest/              # one-script smoke test
├─ Samples~/PatrolDemo/             # two NPC capsules driven by WaypointBrain
└─ Samples~/VisualDemo/             # LLM pipeline + 20-agent orchestration demo
```

## Quick start

### 1. Start the Python backend

```bash
pip install -e ".[websocket]"
biomata-ws --config examples/corporate/sim.yaml --port 8765
```

### 2. Add `BiomataManager` to a scene

Attach the `BiomataManager` component to a persistent GameObject. The Inspector
exposes **Host**, **Port**, **UseTls**, and connection timeouts.

### 3. Register an agent and tick

```csharp
using Biomata.SDK;
using Biomata.SDK.Unity;
using Biomata.SDK.Models;

var biomata = BiomataManager.Instance;

await biomata.Client.Agents.RegisterAsync(new AgentRegistration
{
    AgentId    = "npc_guard_01",
    AgentName  = "Guard",
    BrainClass = "src.plugins.builtin.idle_brain.brain.IdleBrain",
});

var observations = new List<AgentObservationData>
{
    new AgentObservationData("npc_guard_01",
        new Dictionary<string, object> { ["location"] = "gatehouse" })
};

TickResult result = await biomata.Client.Ticks.TickAsync(observations);
foreach (var decision in result.Decisions)
    ApplyDecision(decision);
```

### 4. Subscribe to events

```csharp
biomata.Client.Events.On("tick_end",         ev => Debug.Log($"tick {ev.Tick}"));
biomata.Client.Events.On("action_completed", ev => UpdateHUD(ev.AgentId, ev.Data.GetString("action")));
```

## Smoke Test

The package ships a one-script smoke test sample that verifies the SDK
compiles in a fresh Unity 6 project and connects end-to-end to the Python
backend. Import it from **Package Manager → Biomata Simulation SDK → Samples →
Smoke Test → Import**, then attach `BiomataSmokeTest` to an empty GameObject
in a new scene. See `Samples~/SmokeTest/README.md` for full usage.

## Connection resilience

`EventStreamClient` raises `OnDisconnected` when the underlying stream drops.
Per-call failures throw `BiomataException` so call sites can handle them
explicitly. Reconnection policy lives in `BiomataConfig.Retry`. Automatic
WebSocket reconnect is on the roadmap.

## Platform support

| Platform | Status |
|---|---|
| Editor (Win / macOS / Linux) | ✅ primary target |
| Standalone (Win / macOS / Linux) | ✅ Mono or IL2CPP |
| Android | ✅ via IL2CPP (link.xml protects against stripping) |
| iOS | ✅ via IL2CPP |
| WebGL | ❌ excluded — `System.Net.WebSockets.ClientWebSocket` is not available in browser WebGL without a JS shim |

## Wire protocol

Transport is JSON over WebSocket — see `docs/websocket-protocol.md` for the
full spec. Summary of frame shapes:

```
Server → Client (on connect):
  {"type":"hlo", "v":1, "server":"biomata-engine", "session_id":"<uuid>", "capabilities":[...]}

Client → Server:
  {"type":"req", "v":1, "id":"<uuid>", "method":"<name>", "params":{...}}

Server → Client (response):
  {"type":"res", "v":1, "id":"<uuid>", "ok":true,  "result":{...}}
  {"type":"res", "v":1, "id":"<uuid>", "ok":false, "error":{"code":-32601,"name":"METHOD_NOT_FOUND","message":"..."}}

Server → Client (event stream):
  {"type":"evt", "v":1, "session_id":"<uuid>", "seq":42, "event_type":"tick_end", "tick":5, "agent_id":"engine", "ts":"...", "data":{}}
```

Methods: `health_check`, `register_agent`, `remove_agent`, `send_observation`,
`tick`, `pause`, `resume`, `snapshot`, `restore`, `subscribe_events`,
`unsubscribe_events`.
