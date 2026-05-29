# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Install (core + WebSocket transport + dev tools)
pip install -e ".[websocket,dev]"

# Run tests
pytest tests/

# Run a single test
pytest tests/test_snapshot.py::test_restore_mutates_existing_agents

# Type checking (must pass with no errors)
mypy src/

# Run a simulation from CLI
python -m src.cli.main run examples/engine_owned/sim.yaml

# Start the WebSocket server
biomata-ws --config your_sim.yaml --port 8765
# or:
python -m src.transport.websocket.__main__ --config your_sim.yaml --port 8765
```

Tests use `pytest-asyncio` with `asyncio_mode = "auto"` — async test functions work without `@pytest.mark.asyncio`.

Tests must not require Ollama or any external service.

## Architecture

The engine separates three concerns: **World** (owns/proxies simulation state), **Brain** (async cognition → Intent), and **Actions** (Intent → ActionResult + mutations). Everything else is infrastructure.

### Dependency direction (strictly enforced)

```
Transport → Service → Engine Core → Contracts
                                  ← Plugins implement Contracts
```

Engine core (`src/engine/`) has zero imports from service, transport, or plugins. Contracts (`src/contracts/`) define Python `Protocol` classes. Plugins implement them via structural subtyping — no inheritance.

### Layer map

| Layer | Package | Key classes |
|---|---|---|
| Transport | `src/transport/websocket/` | `WebSocketServer`, `ConnectionHandler`, `Protocol v1` |
| Service | `src/service/` | `SimulationSession`, `EventStreamAdapter`, DTOs |
| Engine core | `src/engine/` | `Simulation`, `AgentRuntime`, `Scheduler`, `EventBus`, `ActionRegistry`, `ObservationRegistry` |
| Contracts | `src/contracts/` | `Brain`, `World`, `ActionHandler`, `Memory`, `ObservationProvider`, `StateExtension`, `SocialSystem` |
| Plugins | `src/plugins/builtin/` | `OllamaLLMBrain`, `IdleBrain`, `SimpleMemory`, `WeightedGraphSocial` |
| External plugins | `src/plugins/external/` | `HostedWorld` |
| Config | `src/config/` | `loader.py` (dynamic import), `manifest.py`, `roles.py` |

### Tick execution (hot path)

Per agent, each tick: `StateExtension.tick()` → `ObservationRegistry.collect()` → `world.observe()` (world wins on key conflicts) → engine injects `agent_id/inventory/state_ext` (always wins) → `Brain.decide()` → `ActionRegistry.validate_intent()` → `ActionHandler.execute()` → engine applies `state_mutations` and inventory deltas → `world.apply()` → `Memory.store()` → `EventBus.emit(ACTION_COMPLETED)`.

`SimultaneousScheduler` (default) runs all `Brain.decide()` calls concurrently via `asyncio.gather`. Action results are applied in completion order — this is a known intra-tick ordering tradeoff, not a bug.

### AgentView immutability invariant

`AgentRuntime` never passes a mutable `Agent` to brains, handlers, or world. It wraps in `AgentView` — an immutable snapshot valid for one step. Handlers wanting to mutate state return `ActionResult.state_mutations`; the runtime applies those after the handler returns. Never store an `AgentView` reference across ticks.

### EventBus

`EventBus.emit()` is synchronous and calls subscribers in registration order. If a subscriber raises, it propagates into `AgentRuntime.step()` and can abort the tick. Built-in subscribers (`SocialEffectSubscriber`, `EventLogSubscriber`) do not wrap exceptions. The social graph is updated **only** via `SocialEffectSubscriber` listening on `ACTION_COMPLETED` — handlers must emit `side_effects` in `ActionResult`, not call `social.update()` directly.

### Ownership modes (Unity integration)

Two patterns, mutually exclusive per-agent and mixable within a simulation:

- **Engine-owned** (`BindToExisting`): agents declared in YAML, Unity `BiomataAgent` binds to pre-existing backend agents with no registration RPC.
- **Host-owned** (`CreateAtRuntime`): backend starts empty, Unity calls `RegisterAsync`/`UnregisterAsync` to drive agent lifecycle.

### Tick modes

- **`HOST_DRIVEN`**: client sends `tick` RPC; each call drives one tick synchronously. Unity pushes observations in `StepRequest.agent_observations`.
- **`AUTONOMOUS`**: backend loops `run_tick()` as an asyncio task; client uses `send_observation` RPC. Mixing RPC ticks into a running AUTONOMOUS session is allowed but there is no locking — don't rely on ordering.

### Configuration

`class:` values in YAML are fully-qualified Python dotted paths, dynamically imported via `importlib` at server startup. Constructor kwargs are passed as `**config`. There is no sandboxing — loaded classes run with full process permissions.

### Snapshots

Snapshots use pickle + ephemeral per-process HMAC-SHA256 signing. They are not portable between processes or server restarts, and not stable across class renames. They capture: RNG state, all agent state (memory, state_ext, brain if serializable), world (if `Snapshotable`), social system (if `Snapshotable`), scheduler. They do not capture: pending engine_commands, in-flight brain calls, WebSocket connections.

## Extension points

Implement these protocols to add capabilities; register via YAML `class:` or `ActionRegistry.register()` / `ObservationRegistry.register()`:

| Protocol | File | Minimal example |
|---|---|---|
| `Brain` | `src/contracts/brain.py` | `src/plugins/builtin/idle_brain/` |
| `World` | `src/contracts/world.py` | `src/plugins/external/world.py` |
| `ActionHandler` | `src/contracts/action.py` | Any file in `examples/` |
| `Memory` | `src/contracts/memory.py` | `src/plugins/builtin/simple_memory/` |
| `ObservationProvider` | `src/contracts/observation.py` | `src/plugins/builtin/observations/` |
| `StateExtension` | `src/contracts/state.py` | — |
| `SocialSystem` | `src/contracts/social.py` | `src/plugins/builtin/simple_social/` |

Optional world capability protocols (`SpatialWorld`, `VisibilityWorld`, `PlaceableWorld`, `ExternalWorld`) are checked via `isinstance()` using structural subtyping — implement the method signatures, no inheritance needed.

## Code style

- Python 3.11+ features are fine (`match`, `|` unions, etc.)
- Type annotations required on all public interfaces
- `mypy src/` must pass with no errors
- Comments only for non-obvious constraints or workarounds — names do the explaining
