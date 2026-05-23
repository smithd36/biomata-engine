# Biomata SDK — Smoke Test

A one-script scene that verifies the SDK compiles into your Unity project and
talks to a running biomata-engine server. Use this before scaffolding the
full village demo to confirm Unity ↔ Python end-to-end is healthy.

## What it checks

1. The `BiomataSDK` assembly compiles into your project.
2. `BiomataManager` instantiates and is configurable from a scene.
3. `ConnectAsync` reaches the server on `localhost:8765` (WebSocket default).
4. `HealthCheck` round-trips a request/response.
5. `RegisterAgent` adds one agent (default brain: `IdleBrain`, zero dependencies).
6. The event stream delivers `tick_end` and `action_completed` events back to the client.

## Setup

### 1. Start the Python backend (WebSocket — recommended for Unity 6)

From the repo root:

```
pip install -e ".[websocket]"
biomata-ws --config examples/corporate/sim.yaml --port 8765
```

You should see `WebSocket server listening on ws://0.0.0.0:8765`.

> If you'd rather use gRPC for this smoke test, install `.[grpc]` instead,
> launch `biomata-grpc --port 50051`, and set **Transport = Grpc**,
> **Port = 50051** on the BiomataSmokeTest Inspector.

### 2. Add the SDK to a Unity 6 project

Open a Unity 6 project (6.4 / 6000.4.8f1 is the primary target). In
`Packages/manifest.json` add:

```json
"com.biomata.sdk": "file:../path/to/biomata-engine/unity_sdk"
```

Or use **Window → Package Manager → + → Add package from disk…** and pick
`unity_sdk/package.json`.

The SDK declares `com.unity.nuget.newtonsoft-json` as a dependency — Unity
will install it automatically from the registry.

### 3. Import this sample

In Package Manager, select **Biomata Simulation SDK**, open the **Samples** tab,
and click **Import** next to **Smoke Test**. The sample lands under
`Assets/Samples/Biomata Simulation SDK/<version>/Smoke Test/`.

### 4. Run

1. Open or create an empty scene.
2. Create an empty `GameObject` and add the `BiomataSmokeTest` component.
3. (Optional) Override **Transport / Host / Port / Test Agent Id** in the Inspector.
4. Press **Play**.

The component builds the entire UI at runtime — Canvas, EventSystem, buttons,
and event log — so the scene needs nothing else.

## Using the UI

| Button             | What it does                                                            |
|--------------------|-------------------------------------------------------------------------|
| **Connect**        | Opens the WebSocket and starts the event stream.                        |
| **Health Check**   | Calls `Health.CheckAsync()` and prints status, tick, and agent count.   |
| **Register Agent** | Registers `smoke_agent_001` with an `IdleBrain`.                        |

The event log shows every `tick_end` and `action_completed` event delivered by
the stream, plus connection / disconnection notices.

## Expected output (happy path)

```
[10:00:01] connecting…
[10:00:01] connected
[10:00:03] health: ok state=created tick=0 agents=2
[10:00:05] registered: smoke_agent_001 (SmokeBot)
[10:00:06] tick_end @ t1
[10:00:06] action @ t1: smoke_agent_001 → idle
```

If the backend was started with `examples/corporate/sim.yaml`, the existing
config agents (`agents=2`) appear in the health check.

## Troubleshooting

| Symptom                                                        | Likely cause / fix                                                                    |
|----------------------------------------------------------------|---------------------------------------------------------------------------------------|
| `connect failed: WebSocket connect to ws://localhost:8765 failed` | Python server not running, or wrong port. Start `biomata-ws --port 8765` first.       |
| `connect failed: Server did not become ready within 15s`       | Server up but health-check failing — check the Python log for binding errors.         |
| `register failed: import error: …IdleBrain…`                   | The brain class path doesn't resolve on the server. Confirm `pip install -e .` ran.   |
| Event log shows `tick_end` but no `action_completed`           | Sim is between ticks — register an agent, or trigger ticks from another client.       |
| Errors about `Newtonsoft.Json` missing                         | Package Manager didn't pull `com.unity.nuget.newtonsoft-json`. Reimport the SDK.      |

## Where to next

Once the smoke test connects end-to-end, the full village demo can rely on the
same `BiomataManager` instance. The smoke test never assumes anything about
scene structure or Unity-side observation providers, so it remains a useful
regression check when those layers grow.
