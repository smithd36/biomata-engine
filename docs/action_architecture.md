# Action Architecture

Biomata's action system is fully user-defined. The engine core contains zero
domain-specific actions. This document explains how actions are declared,
classified, filtered per agent, and surfaced to LLMs.

---

## Core types

### `ActionKind`

```python
class ActionKind(str, Enum):
    HOST   = "host"    # Unity/renderer executes; Python packages engine_commands only
    ENGINE = "engine"  # Python executes; may mutate world state, inventory, social graph
    HYBRID = "hybrid"  # both Python processing AND host commands (default)
```

`ActionKind` is informational metadata — it tells the LLM (and human readers)
who is responsible for executing the action. It does not change dispatch logic.

### `ActionSchema`

```python
@dataclass
class ActionSchema:
    name:              str
    description:       str
    parameters_schema: dict[str, Any]  = field(default_factory=dict)
    kind:              ActionKind       = ActionKind.HYBRID
    tags:              frozenset[str]   = field(default_factory=frozenset)
    examples:          list[dict]       = field(default_factory=list)
```

`tags` enables per-agent capability filtering (see below).  
`examples` are injected into the LLM prompt as a concrete usage sample.

---

## Registering actions

```python
from src.contracts.action import ActionKind, ActionSchema
from src.engine.registry import ActionRegistry

registry = ActionRegistry()

# A HOST action — Unity/renderer moves the character
registry.register(
    ActionSchema(
        "navigate",
        "Move to a world-space XZ position.",
        {"target_x": "float", "target_z": "float"},
        kind=ActionKind.HOST,
        examples=[{"action": "navigate", "parameters": {"target_x": 14.0, "target_z": 0.0}}],
    ),
    NavigateHandler(),
)

# An ENGINE action — Python computes the outcome
registry.register(
    ActionSchema("idle", "Stand still and wait.", kind=ActionKind.ENGINE),
    IdleHandler(),
)
```

### HOST actions and `engine_commands`

A `HOST` action's handler should return an `ActionResult` with `engine_commands`
populated. The WebSocket transport forwards `engine_commands` to the host (Unity)
as part of the tick response. The host is responsible for executing them.

```python
class NavigateHandler:
    def execute(self, agent, intent, context) -> ActionResult:
        p = intent.parameters or {}
        tx, tz = float(p.get("target_x", 0)), float(p.get("target_z", 0))
        return ActionResult(
            success=True,
            outcome_text=f"navigate to ({tx:.1f}, {tz:.1f})",
            engine_commands=[{"type": "navigate", "x": tx, "y": 0.0, "z": tz}],
        )
```

`engine_commands` are opaque to the engine — any JSON-serialisable dict is valid.
Define the shape in your handler and mirror it in your Unity `ActionHandlerBase`.

---

## Per-agent capability filtering

By default every agent sees every registered action. Use `tags` + `capabilities`
to restrict which actions are offered to specific agents.

### Schema side — `tags`

```python
ActionSchema(
    "call_reinforcements",
    "Radio for backup from the garrison.",
    kind=ActionKind.HOST,
    tags=frozenset({"guard", "military"}),
)
```

An untagged schema (`tags == frozenset()`) is **universal** — always visible.  
A tagged schema is visible only to agents whose `capabilities` intersect its tags.

### Agent side — `capabilities`

In Python:

```python
agent = Agent(
    id="guard_001",
    name="Aldric",
    brain=...,
    memory=...,
    capabilities=frozenset({"guard", "military"}),
)
```

In `sim.yaml`:

```yaml
agents:
  - id: guard_001
    name: Aldric
    capabilities: [guard, military]
    brain:
      class: examples.patrol.sim.brain.WaypointBrain
      ...
```

### Registry side — `schemas_for()`

`AgentRuntime` calls `registry.schemas_for(agent.capabilities)` instead of
`registry.schemas()`. The rules:

| Schema tags | Agent capabilities | Visible? |
|-------------|-------------------|---------|
| `{}`        | anything           | yes (universal) |
| `{"guard"}` | `{"guard"}`        | yes |
| `{"guard"}` | `{}`               | no |
| `{"guard", "military"}` | `{"guard"}` | yes (intersection) |

---

## LLM prompt integration

`ActionSchema.prompt_block()` renders a single schema entry for the system
prompt. The `kind` label is appended for `HOST` and `ENGINE` actions so the LLM
understands who executes the result. An `examples[0]` entry is shown as a
concrete JSON sample.

Example output:

```
AVAILABLE ACTIONS (use exactly these names):
  navigate: Move to a world-space XZ position.  [host]
    params: {"target_x":"float","target_z":"float"}
    example: {"action":"navigate","parameters":{"target_x":0.0,"target_z":0.0}}
  idle: Stand still and wait.  [engine]
  speak: Say something aloud (short phrase, under 15 words).  [host]
    params: {"message":"string — what you say"}
    example: {"action":"speak","parameters":{"message":"Fresh goods today!"}}
```

`ActionRegistry.actions_prompt_section()` renders all registered actions.
`OllamaLLMBrain` and any other LLM brain should call this (or equivalent) when
building the system prompt.

---

## Backwards compatibility

- All `ActionSchema` fields beyond `name` and `description` have defaults.
  Existing registries compile without changes.
- `registry.schemas()` still returns all schemas — existing code that calls it
  directly is unaffected.
- `schemas_for(frozenset())` returns all untagged schemas, which is the common
  case for simulations that don't use capability filtering.
- `engine_commands` semantics are unchanged.

---

## Example: adding a domain-specific action

**1. Write the handler** (`examples/myworld/sim/handlers.py`):

```python
from src.contracts.action import ActionResult

class CastSpellHandler:
    def execute(self, agent, intent, context) -> ActionResult:
        spell = (intent.parameters or {}).get("spell", "fireball")
        return ActionResult(
            success=True,
            outcome_text=f"cast {spell}",
            engine_commands=[{"type": "cast_spell", "spell": spell}],
        )
```

**2. Register it** (`examples/myworld/sim/registry.py`):

```python
from src.contracts.action import ActionKind, ActionSchema
from src.engine.registry import ActionRegistry

def build_myworld_registry() -> ActionRegistry:
    r = ActionRegistry()
    r.register(
        ActionSchema(
            "cast_spell",
            "Cast a magical spell at your current location.",
            {"spell": "fireball|heal|shield"},
            kind=ActionKind.HOST,
            tags=frozenset({"mage"}),
            examples=[{"action": "cast_spell", "parameters": {"spell": "fireball"}}],
        ),
        CastSpellHandler(),
    )
    return r
```

**3. Grant capability in sim.yaml**:

```yaml
agents:
  - id: mage_001
    name: Seraphel
    capabilities: [mage]
    brain:
      class: src.plugins.builtin.ollama.brain.OllamaLLMBrain
      ...
```

Only `mage_001` will see `cast_spell` in their decision prompt.

**4. Handle it in Unity** — add a `CastSpellHandler : ActionHandlerBase` on the
NPC's `ActionExecutor` that reads `engine_commands[0]["spell"]` and plays the
appropriate particle effect.
