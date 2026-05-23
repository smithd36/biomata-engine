# Biomata Simulation SDK — Unity Client

Unity C# client SDK for the [biomata-engine](../README.md) backend.

The SDK ships **two** transports and lets the consumer pick one via config:

| Transport | Default for | Why                                                                      |
|-----------|------------|---------------------------------------------------------------------------|
| **WebSocket** (default) | Unity 6 (game clients) | JSON over `System.Net.WebSockets.ClientWebSocket`. Compiles cleanly on Unity 6 without the `SocketsHttpHandler` reference-assembly issue. |
| **gRPC**    | Research / server-to-server | Protobuf over `Grpc.Net.Client`. Higher throughput, typed contracts. Available for power users. |

Both transports drive the same `SimulationSession` on the Python side — the
engine is transport-agnostic. The SDK's public API (`BiomataManager`,
`SimulationClient`, sub-clients) is unchanged between the two; only
`BiomataConfig.Transport` toggles the wire.

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
│  ├─ Transport/                    # pluggable transports behind ITransport
│  │  ├─ ITransport.cs
│  │  ├─ WebSocketTransport.cs      # default, Unity 6-friendly
│  │  ├─ GrpcTransport.cs           # opt-in via config
│  │  └─ JsonHelpers.cs
│  ├─ Core/                         # SimulationClient, BiomataConfig, ProtoUtils
│  ├─ Generated/                    # pre-built gRPC C# stubs (used by GrpcTransport only)
│  ├─ Integration/                  # MonoBehaviour glue
│  ├─ Models/                       # DTOs (AgentRegistration, TickResult, …)
│  ├─ Plugins/                      # vendored gRPC + Protobuf DLLs
│  └─ Unity/                        # BiomataManager singleton
├─ Editor/                          # Inspector overrides
├─ Samples~/SmokeTest/              # one-script smoke test
├─ Proto/                           # source of truth — simulation.proto
└─ Scripts/                         # developer-only: vendor.py
```

## Quick start

### 1. Start the Python backend (WebSocket)

```bash
pip install -e ".[websocket]"
biomata-ws --config examples/corporate/sim.yaml --port 8765
```

For gRPC instead:

```bash
pip install -e ".[grpc]"
biomata-grpc --config examples/corporate/sim.yaml --port 50051
```

### 2. Add `BiomataManager` to a scene

Attach the `BiomataManager` component to a persistent GameObject. Inspector
exposes **Transport**, **Host**, **Port**, **UseTls**, and connection
timeouts.

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

The same call site works identically whether `BiomataConfig.Transport` is
`WebSocket` or `Grpc`. Only the wire format changes.

### 4. Subscribe to events

```csharp
biomata.Client.Events.On("tick_end",         ev => Debug.Log($"tick {ev.Tick}"));
biomata.Client.Events.On("action_completed", ev => UpdateHUD(ev.AgentId, ev.Data.GetString("action")));
```

## Transport selection at runtime

```csharp
var client = new SimulationClient(new BiomataConfig
{
    Transport = TransportKind.WebSocket,   // or TransportKind.Grpc
    Host      = "localhost",
    Port      = 8765,                       // 50051 for gRPC
});
await client.ConnectAsync(destroyCancellationToken);
```

`SimulationClient.ActiveTransport` reports the current selection (useful for
logging / diagnostics).

## Smoke Test

The package ships a one-script smoke test sample that verifies the SDK
compiles in a fresh Unity 6 project and connects end-to-end to the Python
backend. Import it from **Package Manager → Biomata Simulation SDK → Samples →
Smoke Test → Import**, then attach `BiomataSmokeTest` to an empty GameObject
in a new scene. See `Samples~/SmokeTest/README.md` for full usage.

## Connection resilience

`EventStreamClient` raises `OnDisconnected` when the underlying stream drops.
Per-RPC failures throw `BiomataException` so call sites can handle them
explicitly. Reconnection policy lives in `BiomataConfig.Retry` — currently
consumed by gRPC; WebSocket reconnect is on the roadmap.

## Platform support

| Platform | Status |
|---|---|
| Editor (Win / macOS / Linux) | ✅ primary target |
| Standalone (Win / macOS / Linux) | ✅ Mono or IL2CPP |
| Android | ✅ via IL2CPP (link.xml protects against stripping) |
| iOS | ✅ via IL2CPP |
| WebGL | ❌ excluded — neither transport supports browser sockets without a JS shim |

## Maintenance — regenerating Generated/ and Plugins/

You only do this when:
- `Proto/simulation.proto` changes, or
- you bump pinned NuGet versions, or
- you want to rebuild the committed binaries from scratch.

```bash
cd unity_sdk
python Scripts/vendor.py
```

End users never run `vendor.py`. The outputs are committed to the repo so a
fresh `git clone` + UPM import yields a working SDK with no toolchain setup.

## Wire protocols

### WebSocket (JSON)

Three frame shapes — see `src/transport/websocket/protocol.py` for the
authoritative spec.

```
{"type":"req", "id":"<uuid>", "method":"<name>", "params":{...}}
{"type":"res", "id":"<uuid>", "ok":true,  "result":{...}}
{"type":"res", "id":"<uuid>", "ok":false, "error":"..."}
{"type":"evt", "event_type":"tick_end", "tick":3, "agent_id":"engine", "data":{...}}
```

Methods: `health_check`, `register_agent`, `remove_agent`, `send_observation`,
`tick`, `pause`, `resume`, `snapshot`, `restore`, `subscribe_events`,
`unsubscribe_events`.

### gRPC

See `src/transport/grpc/proto/simulation.proto`. The service surface mirrors
the WebSocket method list 1:1.
