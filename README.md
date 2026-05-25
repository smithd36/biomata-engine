# Biomata Engine

**Open-source runtime for autonomous agents in simulated worlds.**

[![Apache 2.0 License](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](LICENSE)
[![Python 3.11+](https://img.shields.io/badge/python-3.11%2B-blue.svg)](https://python.org)

Biomata gives you a pluggable simulation runtime, a documented WebSocket protocol, and a Unity SDK — so you can run autonomous NPCs, social experiments, and agent benchmarks without building the infrastructure from scratch.

No SaaS. No accounts. Self-hostable. Extensible.

---

## What is Biomata?

Biomata is a Python runtime that manages the execution loop for autonomous agents in simulated environments. It separates three concerns:

| Layer | What it does |
|---|---|
| **World** | Owns or proxies simulation state. Provides observations per agent. |
| **Brain** | Async cognition: receives observation + memory, returns an Intent. Can be LLM, rule-based, scripted, or an RL policy. |
| **Actions** | Maps Intent → ActionResult with state mutations and optional engine commands. |

The engine handles cognition loops, memory, scheduling, and the event bus. Plug in your own World, Brain, and ActionHandlers via Python Protocol classes. Connect any game engine via the open WebSocket protocol.

---

## Quickstart — 2 minutes, no Ollama required

```bash
git clone https://github.com/smithd36/biomata-engine.git
cd biomata-engine
pip install -e .
python -m src.cli.main run examples/patrol/sim.yaml
```

You should see two agents patrolling waypoints in the terminal — autonomous behavior, deterministic, no external dependencies.

**To run the Docker quickstart instead:**

```bash
docker compose up
```

This starts the engine, a deterministic village simulation, and the WebSocket server. No Ollama, no API keys, no Unity required.

---

## Examples

| Example | Agents | Brain | Requires |
|---|---|---|---|
| `patrol/` | 2 | Deterministic waypoint | Nothing |
| `village/` | 10 | Hybrid (IdleBrain + optional LLM) | Nothing (Ollama optional) |
| `corporate/` | 5 | OllamaLLMBrain | Ollama |
| `medieval/` | 5 | OllamaLLMBrain | Ollama |

Start with `patrol/` — it runs without any external dependencies and demonstrates the core cognition loop in under 2 minutes.

---

## Architecture

```
┌──────────────────────────────────────────────────────────────────┐
│                      Biomata Engine v0.5.0                       │
├──────────────────────────────────────────────────────────────────┤
│  ┌─────────────────────────────────────────────────────────┐    │
│  │              TRANSPORT LAYER (WebSocket)                 │    │
│  │    WebSocketServer → ConnectionHandler → Protocol v1    │    │
│  └───────────────────────────┬─────────────────────────────┘    │
│                              │ SimulationController              │
│  ┌───────────────────────────▼─────────────────────────────┐    │
│  │                   SERVICE LAYER                          │    │
│  │    SimulationSession  ·  EventStreamAdapter  ·  DTOs    │    │
│  └───────────────────────────┬─────────────────────────────┘    │
│                              │ Simulation API                    │
│  ┌───────────────────────────▼─────────────────────────────┐    │
│  │                    ENGINE CORE                           │    │
│  │  Simulation ─► Scheduler ─► AgentRuntime ─► Registry   │    │
│  │                         EventBus                         │    │
│  └─────────┬──────────────────────────────┬────────────────┘    │
│            │ World Protocol               │ Brain Protocol       │
│  ┌─────────▼──────────┐       ┌───────────▼──────────────┐     │
│  │    WORLD IMPLS     │       │      BRAIN IMPLS          │     │
│  │  MedievalWorld     │       │  OllamaLLMBrain           │     │
│  │  CorporateWorld    │       │  IdleBrain (deterministic) │     │
│  │  HostedWorld       │       │  ReplayBrain              │     │
│  └────────────────────┘       └───────────────────────────┘     │
└──────────────────────────────────────────────────────────────────┘
```

**Authority models:**

- **Local-authoritative**: Python owns world state. Run standalone from CLI.
- **Host-authoritative**: Game engine owns state. HostedWorld proxies observations. Engine runs cognition, returns commands.

**Tick modes:**

- **HOST_DRIVEN**: Client sends `tick` request. Synchronized to game loop (Unity FixedUpdate).
- **AUTONOMOUS**: Backend drives its own loop. Clients subscribe to events.

---

## Installation

```bash
# Core only (no WebSocket server)
pip install -e .

# Core + WebSocket transport
pip install -e ".[websocket]"

# Core + WebSocket + dev tools (pytest, mypy)
pip install -e ".[websocket,dev]"
```

---

## Unity SDK

The `unity_sdk/` directory contains a C# Unity 6 package.

1. Start the Python server: `biomata-ws --config your_sim.yaml --port 8765`
2. Import the package into your Unity project
3. Add `BiomataManager` to your scene
4. Register agents and start sending observations

See the [Unity SDK docs](https://biomata.dev/docs/unity-sdk) for the full integration guide.

---

## WebSocket Protocol

The WebSocket protocol is fully documented and engine-agnostic. Unity implements it. You can implement it for any engine (Godot, Unreal, Bevy, a browser client).

Protocol spec: [`docs/websocket-protocol.md`](docs/websocket-protocol.md)  
Protocol docs: [biomata.dev/docs/transport](https://biomata.dev/docs/transport)

---

## Repository Structure

```
biomata-engine/
  src/
    contracts/          World, Brain, Memory, Action, State, Social protocols
    engine/             Simulation, AgentRuntime, Scheduler, EventBus
    service/            SimulationSession, DTOs, EventStreamAdapter
    transport/
      websocket/        WebSocketServer, ConnectionHandler, Protocol v1
    plugins/
      builtin/          OllamaLLMBrain, IdleBrain, SimpleMemory, WeightedGraphSocial
      external/         HostedWorld
    cli/                Command-line entry points
  examples/
    patrol/             2-agent waypoint patrol — no Ollama required
    village/            10-NPC village with hybrid cognition
    corporate/          Org-graph social simulation (5 agents)
    medieval/           Grid world with vitals and seasons (5 agents)
  unity_sdk/            C# Unity 6 package
  docs/                 websocket-protocol.md (authoritative wire spec)
  tests/                pytest test suite
```

---

## Who is this for?

**Indie and technical game developers** — autonomous NPCs without building the backend runtime yourself. Drop the Unity SDK into your project and your characters start reasoning in minutes.

**AI engineers** — a structured agent runtime for experimentation. Swap brains, world models, and action sets via config. Drive ticks over WebSocket from any client. Replay deterministically.

**Researchers** — reproducible simulation environments with pluggable cognition. Seeded RNG. Deterministic schedulers. LLM or rule-based brains as isolated variables.

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for how to run tests, write a plugin, and submit a PR.

Issues and PRs welcome. This is an early-stage open-source project — community feedback shapes the roadmap.

---

## License

Apache 2.0. See [LICENSE](LICENSE).

Built by [Origin Foundry](https://originfoundry.dev).
