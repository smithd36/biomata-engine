# biomata-engine

A Python framework for running autonomous NPC / agent simulations that are driven by AI brains and exposed to game engines (Unity, Godot, browser) over a WebSocket protocol.

The engine handles the tick loop, scheduling, action dispatch, observation assembly, snapshot/restore, and event streaming. You bring the world, the brains, and the action handlers.

---

## Architecture overview

```
┌─────────────────────────────────────────────────────────┐
│                    Host (Unity / Godot / …)              │
│  BiomataManager → SimulationClient → WebSocketTransport  │
└───────────────────────────┬─────────────────────────────┘
                            │  JSON over WebSocket (port 8765)
┌───────────────────────────▼─────────────────────────────┐
│                   biomata-engine (Python)                 │
│                                                          │
│  WebSocketServer → SimulationSession → Simulation        │
│      ├── Scheduler  (simultaneous | sequential)          │
│      ├── ActionRegistry  (name → schema + handler)       │
│      ├── ObservationRegistry  (capability-filtered)      │
│      ├── EventBus  (tick_start/end, agent events, …)     │
│      ├── World  (HostedWorld | your own)                 │
│      └── Agent[]  → Brain.decide() per tick              │
└─────────────────────────────────────────────────────────┘
```

**Key design decisions:**

- **Protocol classes as interfaces.** `Brain`, `World`, `ActionHandler`, `Memory`, `ObservationProvider` are all Python `Protocol` classes. Plugins implement them without subclassing anything in core.
- **ExternalWorld inversion.** When a game engine is the authoritative physics/render server, `HostedWorld` inverts the data flow: the host *pushes* observations in; the engine *collects* typed commands out. Python never owns positions or mesh state.
- **Two ownership models.** The backend can declare all agents in YAML (*engine-owned*) or start empty and let Unity spawn them at runtime (*host-owned*). Both modes use the same tick protocol.
- **Transport-agnostic service layer.** `src/service/` is the stable boundary between the engine and any transport. The WebSocket server is one adapter; nothing in the engine imports from `src/transport/`.

---

## Repository structure

```
biomata-engine/
├── src/
│   ├── contracts/          # Protocol interfaces: Brain, World, ActionHandler,
│   │                       #   Memory, ObservationProvider, Snapshotable
│   ├── engine/             # Core: Simulation, Agent, AgentRuntime, ActionRegistry,
│   │                       #   ObservationRegistry, EventBus, Scheduler,
│   │                       #   ConversationInbox, SocialSystem, Snapshot
│   ├── config/             # YAML loader: ActionManifest, RoleManifest, SimLoader
│   ├── service/            # Transport boundary: SimulationSession, DTOs,
│   │                       #   EventStreamAdapter, TickMode
│   ├── transport/
│   │   └── websocket/      # WebSocket server (biomata-ws entry point)
│   ├── plugins/
│   │   ├── builtin/        # IdleBrain, OllamaLLMBrain, ReplayBrain,
│   │   │                   #   SimpleMemory, SimpleSocial, ReplayRecorder,
│   │   │                   #   built-in ObservationProviders
│   │   └── external/       # HostedWorld — ExternalWorld for game-engine integration
│   └── cli/                # biomata-engine CLI entry point
├── tests/                  # pytest suite (no Ollama / external services required)
├── simulation/
│   └── actions.yaml        # Canonical action manifest (source of truth for both
│                           #   Python ActionSchemas and Unity BiomataActions.json)
├── examples/
│   ├── engine_owned/       # sim.yaml: backend declares all agents; Unity binds visuals
│   └── host_owned/         # sim.yaml: backend starts empty; Unity registers agents
├── unity_sdk/              # C# client SDK — see unity_sdk/README.md
│   ├── Runtime/            # Clients, Transport, Core, Integration, Models, Unity
│   ├── Editor/             # Inspector overrides and validator tools
│   └── Samples~/           # SmokeTest, PatrolDemo, VisualDemo
├── docs/
│   └── websocket-protocol.md  # Wire-format specification (authoritative)
├── pyproject.toml
└── CONTRIBUTING.md
```

---

## Core components

### Engine (`src/engine/`)

| Component | Responsibility |
|---|---|
| `Simulation` | Top-level orchestrator. Owns tick loop, scheduling, snapshot/restore, events. Constructed via `Simulation.from_config(path)`. |
| `Agent` | Identity + mutable state (inventory, memory, state extensions). No cognition. |
| `AgentRuntime` | Per-tick: builds observation, calls `Brain.decide()`, dispatches result to handler. |
| `ActionRegistry` | Maps action names → `(ActionSchema, ActionHandler)`. Built from `ActionManifest`. |
| `ObservationRegistry` | Assembles per-agent observation dicts from registered `ObservationProvider` plugins, filtered by agent capabilities. |
| `EventBus` | Synchronous pub/sub: `TICK_START`, `TICK_END`, `AGENT_REGISTERED`, `AGENT_UNREGISTERED`, `AGENT_STEP_ERROR`, `BRAIN_DECIDED`. |
| `Scheduler` | `SimultaneousScheduler` (all agents step in parallel) or `SequentialScheduler` (one by one). |
| `ConversationInbox` | Per-agent inbox for social messages (`SocialEvent`) exchanged between agents during a tick. |
| `SocialSystem` | Weighted relationship graph between agents (stored in `WeightedGraphSocial`). Snapshotable. |

### Contracts (`src/contracts/`)

All are Python `Protocol` classes — implement them to build plugins.

| Contract | What it models |
|---|---|
| `Brain` | Cognition: `async decide(agent, observation, actions, context) → Intent` |
| `World` | World state: `observe()`, `apply()`, `tick()`, `metadata` |
| `ExternalWorld` | Game-engine-hosted world: adds `push_observation()`, `push_metadata()`, `collect_commands()` |
| `ActionHandler` | Executes one action: `execute(intent, agent, context) → ActionResult` |
| `Memory` | Per-agent memory: `store()`, `recall()` |
| `ObservationProvider` | Contributes one observation slice: `collect(agent_id, world, registry) → dict` |
| `Snapshotable` | Serialization: `serialize() → bytes`, `restore(bytes)` |

### Config (`src/config/`)

| Component | Responsibility |
|---|---|
| `ActionManifest` | Loads `simulation/actions.yaml`, builds `ActionSchema` objects, exports `BiomataActions.json` for Unity. |
| `RoleManifest` | Loads role definitions from `sim.yaml`, expands capabilities and brain config per agent. |
| `SimLoader` | Constructs a `Simulation` from a `sim.yaml` file — imports world, registry, and brain classes by dotted path. |

### Service layer (`src/service/`)

Transport-agnostic boundary between the engine and any wire protocol. Transports depend on `src/service/`; the engine never imports from transports.

| Component | Responsibility |
|---|---|
| `SimulationSession` | Wraps a `Simulation` with the public API: `tick()`, `register_agent()`, `remove_agent()`, `snapshot()`, `restore()`, `subscribe_events()`. |
| `EventStreamAdapter` | Bridges the internal `EventBus` to `ServiceEvent` handlers registered by transports. |
| `TickMode` | `HOST_DRIVEN` (client sends tick requests) or `AUTONOMOUS` (backend drives its own loop). |
| DTOs | `StepRequest`, `StepResponse`, `AgentObservationDTO`, `AgentDecisionDTO`, `ServiceEvent`, `SimulationStatus`. |

### WebSocket transport (`src/transport/websocket/`)

Implements the biomata WebSocket Protocol v1 (see `docs/websocket-protocol.md`). Started via `biomata-ws`.

### Plugins (`src/plugins/builtin/`)

| Plugin | Description |
|---|---|
| `idle_brain` | Returns a fixed `Intent` every tick. No I/O, no dependencies. Use for integration tests and placeholders. |
| `ollama` | `OllamaLLMBrain` — calls a local Ollama model. Observation-driven prompting; no hard-coded domain assumptions. |
| `replay_brain` | Replays a recorded session deterministically. Paired with `replay_recorder`. |
| `replay_recorder` | `JsonReplayRecorderSubscriber` — records decisions to JSONL for later replay. |
| `simple_memory` | `SimpleMemory` — flat string key/value store, serializable. |
| `simple_social` | `WeightedGraphSocial` — relationship graph between agents. |
| `observations` | Built-in observation providers (not Unity-specific): composable slices that populate agent perception dicts. |

### HostedWorld (`src/plugins/external/`)

`HostedWorld` implements `ExternalWorld`. The game engine pushes observations and metadata before each tick; after the tick, the engine collects structured commands to execute. This is the standard world for Unity integration.

---

## Installation

**Python 3.11 or later required.**

```bash
# Clone
git clone https://github.com/smithd36/biomata-engine.git
cd biomata-engine

# Core + WebSocket transport + dev tools
pip install -e ".[websocket,dev]"
```

Optional extras:

| Extra | Installs |
|---|---|
| `websocket` | `websockets>=12.0` — required for `biomata-ws` |
| `ollama` | `httpx>=0.27` — required for `OllamaLLMBrain` (already in core deps) |
| `dev` | `pytest`, `pytest-asyncio`, `mypy`, `websockets` |

---

## Running the server

```bash
# Engine-owned: agents declared in YAML, Unity binds visual shells
biomata-ws --config examples/engine_owned/sim.yaml --port 8765

# Host-owned: agents registered by Unity at runtime
biomata-ws --config examples/host_owned/sim.yaml --port 8765

# Autonomous mode: backend drives its own tick loop
biomata-ws --config examples/engine_owned/sim.yaml --port 8765 --mode autonomous

# All options
biomata-ws --config <path> --host 0.0.0.0 --port 8765 --mode host-driven --log-level DEBUG
```

**`--mode` values:**

- `host-driven` (default) — the client (Unity) sends `tick` requests. The backend waits.
- `autonomous` — the backend runs its own tick loop. Clients use `pause` / `resume`.

---

## Configuration: sim.yaml

Every simulation is described by a YAML file. Minimal structure:

```yaml
engine:
  ticks: 99999       # number of ticks before the sim stops
  seed: 42           # RNG seed for determinism
  scheduler: simultaneous   # or: sequential

world:
  class: src.plugins.external.world.HostedWorld

registry:
  class: src.plugins.builtin.ollama.registry.build_hosted_registry

roles:
  Guard:
    capabilities: [guard, patrol, authority]
    brain:
      provider: idle   # idle | ollama | or any dotted Python path
    observations:
      - position
      - nearby_agents

agents:            # omit for host-owned pattern
  - id: gate_guard_01
    name: Aldric
    role: Guard
```

The `provider` shorthand resolves to:
- `idle` → `src.plugins.builtin.idle_brain.brain.IdleBrain`
- `ollama` → `src.plugins.builtin.ollama.brain.OllamaLLMBrain`
- Any other value is treated as a dotted Python import path.

See `examples/engine_owned/sim.yaml` and `examples/host_owned/sim.yaml` for annotated full examples.

---

## Action manifest (`simulation/actions.yaml`)

The action manifest is the **single source of truth** for action names, parameters, required capabilities, and Unity engine command types. Both Python and Unity read it.

```yaml
version: "1"
actions:
  - name: move
    description: Move toward a destination or in a direction.
    parameters:
      destination: str
      speed: float?          # trailing ? = optional
    engine_command: navigate
    kind: host               # host | engine | hybrid
    required_capabilities:   # omit = all agents
      - mobile
```

After editing, regenerate the Unity JSON sidecar:

```bash
python -c "
from src.config.manifest import ActionManifest
ActionManifest.load('simulation/actions.yaml') \
  .export_json('unity_sdk/Runtime/Resources/BiomataActions.json')
"
```

---

## Ownership models

### Engine-owned

The backend declares all agents in `sim.yaml`. Unity attaches `BiomataAgent` components in **BindToExisting** mode — no registration RPC is sent. The simulation runs with or without Unity connected.

**Use when:** Agent roster is fixed at deploy time; backend is the source of truth; multiple Unity clients (spectators) need to bind the same agents.

### Host-owned

The `agents:` block is absent from `sim.yaml`. Unity owns the full lifecycle: it calls `RegisterAgent` on spawn and `RemoveAgent` on destroy. The backend is a pure execution engine.

**Use when:** Agent roster is dynamic; procedural spawning; level-load agent swaps; runtime brain hot-swap.

See `examples/engine_owned/sim.yaml` and `examples/host_owned/sim.yaml` for Unity setup instructions in the comments.

---

## Testing

```bash
# Run the full test suite
pytest tests/

# Run a specific file
pytest tests/test_snapshot.py -v

# Type checking
mypy src/
```

The test suite does **not** require Ollama or any external service. The five test files cover:

| File | Coverage |
|---|---|
| `test_external_world.py` | `HostedWorld`, multi-tick accumulation, command collection |
| `test_obs_registry_reliability.py` | `ObservationRegistry` provider reliability |
| `test_service.py` | `SimulationSession`, `EventStreamAdapter`, tick protocol |
| `test_snapshot.py` | Snapshot serialize/restore, RNG determinism, multi-tick restore |
| `test_websocket_transport.py` | WebSocket protocol round-trips |

---

## Unity SDK

The `unity_sdk/` directory is a Unity Package Manager package (`com.biomata.sdk`, version `0.5.0`). It provides the C# client that connects to `biomata-ws`.

**See [`unity_sdk/README.md`](unity_sdk/README.md) for full installation, API reference, and integration patterns.**

Quick links:
- Requires Unity 6000.0+, .NET Standard 2.1
- Transport: JSON over WebSocket (`System.Net.WebSockets.ClientWebSocket`)
- Dependency: `com.unity.nuget.newtonsoft-json` 3.2.1 (auto-resolved by UPM)
- Samples: SmokeTest, PatrolDemo, VisualDemo

---

## WebSocket protocol

See `docs/websocket-protocol.md` for the full wire-format specification.

Summary of message types:

| Direction | Type | Purpose |
|---|---|---|
| Server → Client | `hlo` | Handshake on connect |
| Client → Server | `req` | Method call |
| Server → Client | `res` | Method response (ok or error) |
| Server → Client | `evt` | Event stream frame |

Methods: `health_check`, `register_agent`, `remove_agent`, `send_observation`, `tick`, `pause`, `resume`, `snapshot`, `restore`, `subscribe_events`, `unsubscribe_events`.

---

## Writing plugins

All plugins implement Python `Protocol` classes — no subclassing required.

### Custom Brain

```python
from src.contracts.action import Intent
from src.contracts.brain import Brain, BrainContext
from src.contracts.world import AgentView

class RuleBasedBrain:
    async def decide(self, agent: AgentView, observation: dict,
                     actions: list, context: BrainContext) -> Intent:
        if observation.get("nearby_threat"):
            return Intent(action="alert", reasoning="threat detected")
        return Intent(action="patrol")
```

Reference the class in `sim.yaml`:

```yaml
brain:
  class: mypackage.brains.RuleBasedBrain
```

### Custom World

Implement `World` (and optionally `VisibilityWorld`, `SpatialWorld`, `PlaceableWorld`, `ExternalWorld`). See `src/contracts/world.py` for the full protocol surfaces and `src/plugins/external/world.py` (`HostedWorld`) for the external-world implementation.

### Custom ActionHandler

```python
from src.contracts.action import ActionHandler, Intent, ActionResult

class MyHandler:
    def can_handle(self, action_name: str) -> bool:
        return action_name == "my_action"

    async def execute(self, intent: Intent, agent, context) -> ActionResult:
        return ActionResult(success=True, message="done")
```

See `CONTRIBUTING.md` for the full plugin protocol table.

---

## Contribution guidelines

See [`CONTRIBUTING.md`](CONTRIBUTING.md) for the full guide. Quick summary:

1. `git clone` + `pip install -e ".[websocket,dev]"`
2. `pytest tests/` and `mypy src/` must both pass before opening a PR.
3. New brain/world/handler plugins are the highest-value contributions.
4. Tests must not require Ollama or any external service.
5. Apache 2.0 licensed.

**High-value contributions:** new Brain implementations, World adapters, protocol client implementations (Godot, Bevy, browser), ObservationProviders, example simulations.

---

## Troubleshooting

**`biomata-ws: command not found`**
Run `pip install -e ".[websocket]"` from the repo root. The entry point is registered in `pyproject.toml`.

**Connection refused on port 8765**
Make sure the server is running (`biomata-ws --config ...`). Default bind is `127.0.0.1` (loopback). For cross-machine access, use `--host 0.0.0.0`.

**Ollama errors / no LLM responses**
`OllamaLLMBrain` requires a running Ollama instance. Use `brain: provider: idle` to test without it.

**Unity can't find agents (BindToExisting mode)**
The `agentId` on each `BiomataAgent` component must exactly match the `id` field in `sim.yaml`. Check case and leading/trailing spaces.

**Non-deterministic simulation results**
Set an explicit `seed:` in `sim.yaml`. The engine injects its seeded `random.Random` instance into the world and scheduler.

**`mypy` errors in `src/`**
Run `mypy src/` and fix all errors before submitting. The project requires `>=3.11` — use `match`, `|` unions, and `tomllib` freely.

---

## Links

- [`unity_sdk/README.md`](unity_sdk/README.md) — Unity C# client SDK
- [`docs/websocket-protocol.md`](docs/websocket-protocol.md) — Wire protocol specification
- [`CONTRIBUTING.md`](CONTRIBUTING.md) — Contribution guide
- [`simulation/actions.yaml`](simulation/actions.yaml) — Canonical action manifest
- [`examples/engine_owned/sim.yaml`](examples/engine_owned/sim.yaml) — Engine-owned pattern example
- [`examples/host_owned/sim.yaml`](examples/host_owned/sim.yaml) — Host-owned pattern example

---

Apache 2.0 License
