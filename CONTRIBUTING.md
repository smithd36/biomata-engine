# Contributing to Biomata Engine

Biomata is an early-stage open-source project. Community contributions are welcome — whether that's a bug report, a new brain implementation, a world adapter, or docs improvements.

---

## Getting Started

```bash
git clone https://github.com/smithd36/biomata-engine.git
cd biomata-engine
pip install -e ".[websocket,dev]"
```

Run the test suite to confirm everything works:

```bash
pytest tests/
```

Run type checking:

```bash
mypy src/
```

---

## What to Contribute

### High-value contributions

- **New Brain implementations** — rule-based, RL, scripted, or alternative LLM backends. See `src/plugins/builtin/idle_brain/` for a minimal example.
- **New World implementations** — any simulation domain (grid, graph, continuous space). See `examples/medieval/` for a spatial world and `examples/corporate/` for a graph world.
- **New ActionHandlers** — extend the action vocabulary for existing worlds.
- **Protocol client implementations** — a Godot client, Bevy client, browser WebSocket client, or other engine adapters that implement Protocol v1.
- **Observation providers** — pluggable observation slices via `ObservationRegistry`. See `src/plugins/builtin/` for examples.
- **Example simulations** — self-contained, documented scenarios that demonstrate a specific use case.
- **Bug reports** — especially around the WebSocket protocol, edge cases in the scheduler, or memory serialization.

### What not to contribute (yet)

- SaaS integrations, hosted services, billing
- UI/visual tooling (planned for a later layer)
- Anything requiring Ollama as a hard dependency for core functionality

---

## Plugin Architecture

Biomata uses Python Protocol classes as interfaces. To write a plugin, implement the relevant protocol:

| What you're building | Protocol to implement | Example |
|---|---|---|
| Custom brain | `src/contracts/brain.py::Brain` | `src/plugins/builtin/idle_brain/` |
| Custom world | `src/contracts/world.py::World` | `examples/medieval/sim/world.py` |
| Custom action handler | `src/contracts/action.py::ActionHandler` | `examples/patrol/sim/registry.py` |
| Custom memory | `src/contracts/memory.py::Memory` | `src/plugins/builtin/simple_memory/` |
| Custom observation provider | `src/contracts/observation.py::ObservationProvider` | `src/plugins/builtin/` |

---

## Writing Tests

Tests live in `tests/`. We use `pytest` with `pytest-asyncio` for async test cases.

- Unit tests for individual contracts and plugins go in `tests/unit/`
- Integration tests that run a full simulation tick go in `tests/integration/`
- Tests must not require Ollama or any external service

```python
import asyncio
import pytest
from src.engine.simulation import Simulation

def test_patrol_runs_deterministically():
    sim = Simulation.from_config("examples/patrol/sim.yaml")
    result1 = asyncio.run(sim.run())
    sim2 = Simulation.from_config("examples/patrol/sim.yaml")
    result2 = asyncio.run(sim2.run())
    assert result1 == result2
```

---

## Submitting a Pull Request

1. Fork the repo and create a branch: `git checkout -b feature/my-brain`
2. Make your changes. Add tests if adding new behavior.
3. Run `pytest tests/` and `mypy src/` — both must pass.
4. Open a PR with a clear description of what it does and why.

PRs that include a working example or test case are much easier to merge.

---

## Reporting Issues

Open an issue on GitHub. Include:

- What you were trying to do
- What happened instead
- The YAML config if relevant
- Python version and OS

---

## Code Style

- Python 3.11+ features are fine (`match`, `tomllib`, `|` unions)
- Type annotations on all public interfaces
- `mypy src/` must pass with no errors
- No comments explaining what the code does — names should do that
- Comments only for non-obvious constraints or workarounds

---

## Questions

Open a GitHub Discussion for questions that aren't bug reports.

---

Apache 2.0 licensed. By contributing, you agree your contributions are licensed under the same terms.
