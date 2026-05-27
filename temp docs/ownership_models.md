# Agent Ownership Models

How to decide who controls the agent lifecycle — the backend or Unity.

---

## The core question

Every agent in Biomata has a lifecycle: it is created, participates in ticks, and is eventually removed. Two systems can own that lifecycle:

- **The engine** — `sim.yaml` declares agents before the first tick. Unity binds visual shells.
- **The host** — Unity spawns prefabs and registers agents over the WebSocket. The backend starts empty.

Both models use the same tick pipeline. The difference is entirely in who decides which agents exist and when.

---

## Engine-Owned (BindToExisting)

```
sim.yaml defines agents
    ↓
Backend starts ticking (with or without Unity)
    ↓
Unity connects → BiomataAgent.MarkBoundToExisting()
    ↓
Visual shell receives decisions each tick
```

### What the sim.yaml looks like

```yaml
# examples/engine_owned/sim.yaml
agents:
  - id: gate_guard_left
    name: Aldric
    capabilities: [patrol, authority]
    brain:
      class: src.plugins.builtin.idle_brain.brain.IdleBrain

  - id: gate_guard_right
    name: Berna
    capabilities: [patrol, authority]
    brain:
      class: src.plugins.builtin.idle_brain.brain.IdleBrain

  - id: market_merchant
    name: Silas
    capabilities: [trade, social]
    brain:
      class: src.plugins.builtin.idle_brain.brain.IdleBrain
```

### What the Unity Inspector looks like

On each NPC prefab:

```
[Ownership]
  Mode:     BindToExisting
  ℹ agent is pre-declared on the backend; no registration RPC is sent.

[Identity]
  Agent ID:  gate_guard_left   ← must match sim.yaml id exactly
  Name:      Aldric

[Debug]
  Auto Bind: ✓
```

### What the coordinator script does

```csharp
// EngineOwnedManager.cs
private void Start()
{
    _agents.AddRange(FindObjectsByType<BiomataAgent>(FindObjectsSortMode.None));
    bootstrapper.OnConnected    += HandleConnected;
    bootstrapper.OnTickComplete += HandleTickComplete;
}

private void HandleConnected()
{
    // Nothing. Agents bind themselves.
    // No register_agent RPC is sent.
}
```

The coordinator has nothing to register — agents bind themselves when the manager connects.

### When to use it

| Signal | Engine-Owned is the right choice |
|---|---|
| Your NPC roster is fixed at ship time | ✓ |
| The simulation should run with or without a Unity client | ✓ |
| Multiple Unity instances (spectators, tools) observe the same simulation | ✓ |
| You want to swap the brain without Unity knowing | ✓ |
| Agents need complex YAML-side config (personality, backstory, many goals) | ✓ |
| The village / social demo pattern | ✓ |

### Constraints

- Agent IDs in the Unity Inspector **must match** `sim.yaml` exactly. A typo means observations go to the wrong agent.
- Brain class and config cannot be changed from Unity — they live in the YAML.
- Agent count is fixed at startup. Adding agents at runtime requires modifying the YAML and restarting the backend.
- If Unity never connects, the simulation still runs — agents tick using whatever observations the world provides.

---

## Host-Owned (CreateAtRuntime)

```
Backend starts with no agents
    ↓
Unity connects → agent.register RPC for each prefab
    ↓
Backend creates agent (imports brain, initializes memory)
    ↓
Agent participates in ticks
    ↓
Prefab destroyed → agent.unregister RPC
    ↓
Backend removes agent
```

### What the sim.yaml looks like

```yaml
# examples/host_owned/sim.yaml
engine:
  ticks: 99999
  seed:  1
  scheduler: simultaneous

world:
  class: src.plugins.external.world.HostedWorld

# No agents block. Unity registers them at runtime.
```

### What the Unity Inspector looks like

On each NPC prefab:

```
[Ownership]
  Mode:     CreateAtRuntime
  ℹ Unity owns this agent. Registered on connect, unregistered on destroy.

[Identity]
  Agent ID:  scout_001
  Name:      Scout

[Role]
  Role:         patrol
  Capabilities: patrol

[Brain]
  Brain Class:  src.plugins.builtin.ollama.brain.OllamaLLMBrain
  Brain Config: {"model": "qwen2.5:14b"}

[Debug]
  Auto Register: ✓
```

### What the coordinator script does

```csharp
// HostOwnedManager.cs
private void HandleConnected()
{
    SpawnAgents();  // Instantiate prefabs and configure BiomataAgent
}

private void SpawnAgent(AgentSpawnData data)
{
    var go    = Instantiate(npcPrefab, data.position, Quaternion.identity);
    var agent = go.GetComponent<BiomataAgent>();

    agent.Configure(
        agentId:       data.agentId,
        displayName:   data.displayName,
        autoRegister:  true,
        ownershipMode: AgentOwnershipMode.CreateAtRuntime);
    // BiomataAgent.Start() fires RegisterAsync → backend creates the agent.
}
```

Unregistration is automatic. When `Destroy(go)` is called, `BiomataAgent.OnDestroy()` fires `TryRemoveAsync`.

### When to use it

| Signal | Host-Owned is the right choice |
|---|---|
| The NPC roster changes at runtime (level loads, procedural spawning) | ✓ |
| Different game modes have different agent sets | ✓ |
| Brain class comes from player choice, save data, or a prefab asset | ✓ |
| You want to hot-swap a brain: destroy the agent, spawn a new one with a different brain class | ✓ |
| Unity is the canonical record of which NPCs exist | ✓ |
| The backend should be stateless between Unity sessions | ✓ |

### Constraints

- The backend **cannot tick an agent** until Unity has registered it. If Unity disconnects mid-tick, agents stop receiving new decisions. A `reconnect=true` re-registration on reconnect resumes them.
- Brain class must be a valid Python dotted path reachable by the backend process. A typo produces `IMPORT_ERROR (-4)`.
- Constructing a brain (e.g. loading an LLM model) happens synchronously inside `RegisterAsync`. Large brains (models with long init) delay the response.
- The backend does not persist agents between sessions. Every Unity play session starts fresh — no memory carryover unless the brain's memory implementation persists to disk.

---

## Side-by-side comparison

| Aspect | Engine-Owned | Host-Owned |
|---|---|---|
| **Agents defined in** | `sim.yaml` | Unity Inspector / code |
| **Registration RPC** | None | `agent.register` on connect |
| **Unregistration RPC** | None | `agent.unregister` on destroy |
| **Backend without Unity** | Runs normally | No agents to tick |
| **Agent count at runtime** | Fixed (restart to change) | Dynamic |
| **Brain config lives in** | YAML | Unity Inspector |
| **Multiple Unity clients** | All bind the same agents | Each client owns its agents |
| **ID collision risk** | Low (one canonical YAML) | High (must coordinate IDs across clients) |
| **Reconnect behavior** | Rebind with no side effects | Re-register; use `reconnect=true` to avoid `AGENT_EXISTS` |
| **`BiomataAgent.OwnershipMode`** | `BindToExisting` | `CreateAtRuntime` |
| **Protocol RPCs used** | None (only observations) | `agent.register`, `agent.unregister` |
| **Canonical example** | `examples/engine_owned/` | `examples/host_owned/` |
| **Unity sample** | `EngineOwned/EngineOwnedManager.cs` | `HostOwned/HostOwnedManager.cs` |

---

## Mixing both models

The models are not mutually exclusive. A scene can contain both `BindToExisting` and `CreateAtRuntime` agents simultaneously:

```
sim.yaml defines:          Unity defines:
  village_guard_001          player_summoned_companion_001
  village_innkeeper_001      player_summoned_companion_002
  (static, always there)     (spawned from inventory, unique per session)
```

The `BindToExisting` agents tick from startup. The `CreateAtRuntime` agents join when the player summons them and leave when they are dismissed.

The backend receives observations from all registered agents regardless of how they were registered. The `agent.list` RPC returns both groups in the same list, differentiated only by `metadata`.

---

## Choosing a model checklist

Answer these questions about your project:

1. **Does the simulation have value without Unity?**
   - Yes → Engine-Owned. The simulation is the product; Unity is a viewer.
   - No → Host-Owned. Unity is the product; the backend is infrastructure.

2. **Can the full agent roster be known at ship time?**
   - Yes → Engine-Owned. Lock it in `sim.yaml`.
   - No → Host-Owned. Let Unity declare it at runtime.

3. **Do different scenes need different agents?**
   - Yes → Host-Owned. Each scene registers its own agents on load.
   - No → Engine-Owned. One YAML serves all scenes.

4. **Do you need hot-swap of brain implementations?**
   - Yes → Host-Owned. Destroy the agent, spawn a new one with a different `BrainClass`.
   - No → Either. Engine-Owned brain swaps require a backend restart.

5. **Will multiple Unity clients observe the same agents?**
   - Yes → Engine-Owned. All clients `BindToExisting`; no duplicate registration.
   - No → Either.

---

## Implementation reference

| Concept | File |
|---|---|
| Ownership enum | `unity_sdk/Runtime/Integration/Agents/AgentOwnershipMode.cs` |
| BiomataAgent mode handling | `unity_sdk/Runtime/Integration/Agents/BiomataAgent.cs` |
| MarkBoundToExisting() | `unity_sdk/Runtime/Integration/Agents/UnityAgentBridge.cs` |
| Engine-Owned sim.yaml | `examples/engine_owned/sim.yaml` |
| Engine-Owned Unity sample | `unity_sdk/samples/EngineOwned/EngineOwnedManager.cs` |
| Host-Owned sim.yaml | `examples/host_owned/sim.yaml` |
| Host-Owned Unity sample | `unity_sdk/samples/HostOwned/HostOwnedManager.cs` |
| Protocol: agent.register | `docs/transport_runtime_agents.md` |
| Backend: registration lifecycle | `docs/runtime_agent_lifecycle.md` |
