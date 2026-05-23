# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

**biomata-engine** is a framework for autonomous agent simulations. It decouples the simulation engine from domain-specific logic (world models, agent brains, action handlers) through well-defined contracts (Protocol-based interfaces). The architecture emphasizes pluggability and extensibility while maintaining deterministic, reproducible simulations.

**Current Version**: 0.5.0 (pre-alpha CLI)

## High-Level Architecture

### Core Abstraction: Contracts (src/contracts/)

The engine uses Protocol-based contracts rather than inheritance. Implementations must satisfy these structural types:

- **World** (contracts/world.py): Minimal protocol requiring observe(), apply(), tick(), current_tick, metadata. Worlds are domain-agnostic. Optional capability protocols:
  - **SpatialWorld**: are_adjacent(id1, id2) for adjacency-aware handlers
  - **VisibilityWorld**: get_nearby_agents(agent_id) for observation building
  - **PlaceableWorld**: place_agent(agent_id, **kwargs) for YAML position fields

- **Brain** (contracts/brain.py): Async decide() returns Intent. Owns personality, prompts, LLM calls.

- **Memory** (contracts/memory.py): Per-agent episodic memory. store(), recall(), serialize(), restore(). Default: SimpleMemory.

- **AgentStateExtension** (contracts/state.py): Optional per-agent state. Engine calls tick() and apply_mutations().

- **SocialSystem** (contracts/social.py): Optional inter-agent relationships. Default: WeightedGraphSocial.

- **ActionHandler** & **ActionSchema**: Pure functions returning ActionResult with mutations as data.

### Simulation Runtime (src/engine/)

1. **Simulation**: Top-level orchestrator. from_config(yaml) loads from YAML. run() executes ticks.

2. **AgentRuntime**: Per-agent step logic: ticks state, builds observation, calls brain.decide(), dispatches through registry, applies mutations, stores memory, emits events.

3. **Scheduler**: Controls concurrency.
   - **SimultaneousScheduler** (default): All agents decide concurrently
   - **SequentialScheduler**: One-by-one, deterministic. Set via engine.scheduler: sequential

4. **ActionRegistry**: Maps action names to (schema, handler). Users register handlers.

5. **EventBus**: Synchronous pub/sub. Standard events: TICK_START, TICK_END, ACTION_COMPLETED, ACTION_FAILED, AGENT_STEP_ERROR, BRAIN_DECIDED, SOCIAL_UPDATED.

### Agent Model (src/engine/agent.py)

Agent is identity + runtime state only:
- id, name: Static identity
- brain, memory: Injected
- inventory: Dict of items
- state_ext: Optional domain-specific vitals

Agent does NOT own: personality, prompts, LLM calls, action semantics.

### Config System (src/config/)

YAML-driven simulation: Components declared as class: module.ClassName with kwargs. Dynamic imports via importlib. Pydantic validation (optional).

### Plugins (src/plugins/builtin/)

- OllamaLLMBrain: Local Ollama model with personality, prompts
- IdleBrain: No-op brain; useful as a test baseline or placeholder
- SimpleMemory: Rolling deque
- WeightedGraphSocial: Directed graph with +/- 1.0 weights
- ReplayBrain: Deterministic replay from a recording
- ReplayRecorder: Records agent decisions for later ReplayBrain playback

### External-World Plugin (src/plugins/external/)

- HostedWorld: World implementation for externally-authoritative state. The host pushes observations and metadata in; the engine pushes engine_commands out. Satisfies World, WorldContext, and ExternalWorld protocols via duck-typing.

### Service Layer (src/service/)

Transport-agnostic boundary between the core engine and any external client (WebSocket, gRPC, Unity, Unreal, test harness). Core engine never imports from here.

- **dto.py**: All data transfer objects — `StepRequest`, `StepResponse`, `AgentObservationDTO`, `AgentDecisionDTO`, `ServiceEvent`, `SimulationStatus`.
- **interfaces.py**: `SessionState` enum, `EventHandler` alias, `SimulationController` Protocol.
- **events.py**: `EventStreamAdapter` — bridges engine `EventBus` → `ServiceEvent` handlers. Single wildcard listener on the bus; fans out to per-subscription handlers by event type.
- **session.py**: `SimulationSession` (implements `SimulationController`), `SessionError`, `create_session()` factory.

## Running Simulations

    python -m src.cli.main run examples/medieval/sim.yaml
    python -m src.cli.main run examples/corporate/sim.yaml

## Service Layer Integration Pattern

For transport adapters (WebSocket handlers, gRPC servicers, Unity bridges) — depend on `SimulationSession`, never on `Simulation` directly:

    from src.service import create_session, StepRequest, AgentObservationDTO
    from src.plugins.external.world import HostedWorld

    world   = HostedWorld()
    sim     = Simulation(agents=[...], world=world, registry=registry)
    session = create_session(sim)

    # Subscribe to events before first tick:
    session.subscribe("tick_end", lambda ev: print(ev.tick))

    # Per-tick:
    req  = StepRequest(
        agent_observations=[AgentObservationDTO("agent_001", {"location": "market"})],
        world_metadata={"weather": "clear"},
    )
    resp = await session.step(req)
    cmds = resp.engine_commands()          # relay to host

    # Lifecycle:
    session.pause()
    session.resume()
    session.shutdown()

    # Snapshot through the session:
    snap = session.snapshot()
    session.restore(snap)

## External-World Integration Pattern

For host-driven simulations (game engines, physics servers):

    from src.plugins.external.world import HostedWorld
    from src.engine.simulation import Simulation

    world = HostedWorld()
    sim   = Simulation(agents=[...], world=world, registry=registry)

    # Each tick — push state from host, run cognition, collect commands:
    world.push_metadata({"weather": "clear"})
    world.push_observation("agent_001", {"location": "market", "nearby_agents": [...]})
    summary = await sim.run_tick()
    commands = summary.engine_commands()   # relay back to host

`TickSummary` (returned by `run_tick()`) exposes `agent_results` (Intent + ActionResult per agent) and `engine_commands()`. `HostedWorld.collect_commands()` is an equivalent accessor from the world side. Both can be used simultaneously.

Action handlers signal host-side effects via `engine_commands` in `ActionResult`:

    return ActionResult(
        success=True,
        outcome_text="moving north",
        state_mutations={"energy_delta": -5},        # applied by Python engine
        engine_commands=[{"type": "navigate", "direction": "north"}],  # relayed to host
    )

## Development Workflow

### Installation

    pip install -e ".[dev]"

### Tests

    pytest tests/
    pytest -k test_name
    pytest --tb=short

### Type Checking

    mypy src/

### Run Simulation (Python)

    from src.engine.simulation import Simulation
    sim = Simulation.from_config("examples/medieval/sim.yaml")
    import asyncio
    asyncio.run(sim.run())

## Examples & Domains

### Medieval (examples/medieval/)

5x5 grid with seasons, weather. MedievalVitals (health, hunger, energy). 10 actions. build_medieval_registry().

### Corporate (examples/corporate/)

Org graph (no grid). EmployeeVitals (stress, influence, reputation). 9 actions. Pure handler pattern. build_corporate_registry().

## Key Design Patterns

### 0. World Authority Model

Two modes coexist:
- **Local-authoritative** (medieval, corporate examples): Python World owns state. `world.observe()` computes from internal grid/graph. `world.apply()` mutates local data structures.
- **Host-authoritative** (HostedWorld): External system owns state. `world.observe()` returns pushed observations. `world.apply()` collects `engine_commands` for relay. Python owns only cognition.

The ExternalWorld Protocol (`src/contracts/world.py`) marks a world as host-authoritative. Check with `isinstance(world, ExternalWorld)`.

### 1. Pure Handlers

Correct pattern (corporate): Return ActionResult with state_mutations dict. World applies in apply(). Medieval still mutates world directly (known deviation).

### 2. Event-Driven Observation

- World.observe(agent_id) returns flat dict
- Engine injects: nearby_agents, agent_id, agent_name, inventory, state_ext, state_str, state_advice
- Brain receives: agent snapshot, observation, actions, context (tick, memory, metadata)

### 3. Intent → Dispatch → Result

1. Brain.decide() returns Intent(action, target, parameters, reasoning)
2. Registry.dispatch(intent, agent_view, world_context) returns ActionResult
3. Engine applies mutations
4. World applies cross-agent effects
5. Events emitted

### 4. Determinism

Seeded RNG: SimulationConfig(seed=42). SequentialScheduler + seeded LLM replay = fully reproducible. ReplayBrain for verification.

### 5. Memory & State

- Memory.store(tick, observation, intent, outcome)
- Memory.recall(n) for brain context
- Memory.serialize()/restore() for checkpoints
- No auto-checkpoint; user code manages snapshots

## Extension Points

### New World Type: Implement World protocol + optional capabilities. Reference in YAML: world: {class: ...}

### New Brain: Implement Brain protocol (async decide). Can emit BRAIN_DECIDED events.

### New Action: Implement ActionHandler. Register with ActionSchema. Return ActionResult with mutations.

### New State Extension: Implement AgentStateExtension. Mutations keyed by string.

## WebSocket Transport

JSON-over-WebSocket server for Unity/browser/game-engine clients. Default transport for Unity 6.

    # Install transport deps
    pip install -e ".[websocket]"

    # Start server from YAML config
    biomata-ws --config examples/corporate/sim.yaml --port 8765
    # or
    python -m src.transport.websocket --config sim.yaml --port 8765

Three JSON frame shapes (see `src/transport/websocket/protocol.py` for the authoritative spec):

    {"type":"req", "id":"<uuid>", "method":"<name>", "params":{...}}
    {"type":"res", "id":"<uuid>", "ok":true,  "result":{...}}
    {"type":"evt", "event_type":"tick_end", "tick":3, "agent_id":"engine", "data":{...}}

Methods mirror gRPC 1:1: `health_check`, `register_agent`, `remove_agent`, `send_observation`, `tick`, `pause`, `resume`, `snapshot`, `restore`, `subscribe_events`, `unsubscribe_events`.

JSON was chosen over protobuf-over-WS because LLM brain latency dominates at 100–500 NPCs/30 Hz; JSON stays curl-able and browser-debuggable.

## gRPC Transport

Async gRPC server for Unity/Unreal integration:

    # Install transport deps
    pip install -e ".[grpc]"

    # Start server from YAML config
    biomata-grpc --config examples/corporate/sim.yaml --port 50051
    # or
    python -m src.transport.grpc --config sim.yaml --port 50051

    # Programmatic usage
    from src.transport.grpc import GrpcServer
    server = GrpcServer.from_simulation(sim, port=50051)
    await server.start()
    await server.wait_for_termination()

RPCs: HealthCheck, RegisterAgent, RemoveAgent, SendObservation, TickSimulation, PauseSimulation, ResumeSimulation, Snapshot, Restore, StreamEvents (server-side streaming).

Proto file: `src/transport/grpc/proto/simulation.proto`. C# namespace: `Biomata.Proto` (import from Unity).

To regenerate stubs after editing the proto:

    python src/transport/grpc/generate.py

Transport isolation: `src/transport/` never imports from `src/engine/` directly — only from `src/service/`. The servicer layer is the only boundary crosser.

## Unity SDK (unity_sdk/)

A C# package for Unity 6 that supports both WebSocket (default) and gRPC transports. The public API is identical regardless of transport; only `BiomataConfig.Transport` changes.

**Requirements**: Unity 6000.0+, .NET Standard 2.1, Mono or IL2CPP.

**Installation**: Add to `Packages/manifest.json`:

    "com.biomata.sdk": "file:../path/to/biomata-engine/unity_sdk"

The SDK declares `com.unity.nuget.newtonsoft-json` as a UPM dependency; Unity pulls it automatically.

**Quick start**:

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

**Transport selection**:

    var client = new SimulationClient(new BiomataConfig
    {
        Transport = TransportKind.WebSocket,  // or TransportKind.Grpc
        Host = "localhost",
        Port = 8765,
    });

**Platform support**: Editor + Standalone (Win/macOS/Linux), Android, iOS. WebGL excluded (no browser-socket support without a JS shim).

**Regenerating stubs** (only needed when `simulation.proto` changes or NuGet versions bump):

    cd unity_sdk && python Scripts/vendor.py

The outputs are committed; end users never run `vendor.py`.

**Smoke test**: Import via Package Manager → Biomata Simulation SDK → Samples → Smoke Test, attach `BiomataSmokeTest` to an empty GameObject.

## Repository Structure

    biomata-engine/
    src/
      __init__.py, cli/main.py, contracts/, engine/, config/, plugins/builtin/
      service/           — transport-agnostic session layer
      transport/
        grpc/            — gRPC transport (servicer, server, generated stubs)
        websocket/       — WebSocket transport (handler, server, protocol)
    examples/
      medieval/sim.yaml, medieval/sim/, corporate/sim.yaml, corporate/sim/
    unity_sdk/           — C# Unity package (WebSocket + gRPC transports, MonoBehaviour glue)
    memory/ (gitignored), pyproject.toml, .gitignore

## Gotchas & Open Issues

1. Medieval handlers mutate world directly; corporate uses pure mutations. Corporate is target pattern.
2. Medieval handlers also access `context.get_world_data()["_grid"]` directly — these won't work with HostedWorld unless the host pushes equivalent data via `push_metadata()`.
3. Ollama brain requires local Ollama (http://localhost:11434).
4. No built-in memory/checkpoint management.
5. No built-in visualization.
6. Async event subscribers not supported (bus is sync).
7. No logging framework beyond EventBus.
8. WebSocket reconnect on drop is not yet implemented in the Unity SDK (gRPC retry is configured via `BiomataConfig.Retry`).

## Important Files

- src/engine/simulation.py: Tick loop; `run_tick()` returns `TickSummary`
- src/engine/agent_runtime.py: Per-agent step orchestration
- src/contracts/action.py: Intent parsing, `ActionResult` (incl. `engine_commands`)
- src/contracts/world.py: World, WorldContext, optional capability protocols, `ExternalWorld`
- src/contracts/snapshot.py: `Snapshotable` protocol, `SimulationSnapshot`, `SnapshotError`, file helpers
- src/plugins/external/world.py: `HostedWorld` — reference external-world implementation
- src/service/interfaces.py: `SimulationController` protocol, `SessionState`
- src/service/session.py: `SimulationSession`, `create_session()`
- src/transport/websocket/protocol.py: Authoritative WebSocket wire-format spec
- src/transport/websocket/handler.py: Per-connection request dispatch
- src/transport/grpc/proto/simulation.proto: Authoritative gRPC service definition
- examples/*/handlers.py: Reference handlers
- src/config/loader.py: YAML to Simulation
- src/plugins/builtin/ollama/brain.py: Reference Brain
- unity_sdk/README.md: Unity SDK quickstart and wire protocol docs
- tests/test_external_world.py: External-world integration tests
- tests/test_snapshot.py: Snapshot/restore integration tests
- tests/test_service.py: Service layer tests
- tests/test_websocket_transport.py: WebSocket transport integration tests
- tests/test_grpc_transport.py: gRPC transport integration tests
