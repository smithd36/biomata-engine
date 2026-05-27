# Phase 4 — Ownership Model Examples: Implementation Report

## Summary

Created two canonical examples and a decision-guide document that teach the difference between engine-owned and host-owned agent lifecycle. Each example is a complete, runnable artifact: a backend sim.yaml plus a Unity coordinator script. The documentation is structured as a practical decision guide, not just a reference.

---

## Deliverables

| File | Purpose |
|---|---|
| `examples/engine_owned/sim.yaml` | Engine-Owned backend config: 3 agents, no LLM required |
| `examples/host_owned/sim.yaml` | Host-Owned backend config: empty agents block |
| `unity_sdk/samples/EngineOwned/EngineOwnedManager.cs` | Unity coordinator for engine-owned pattern |
| `unity_sdk/samples/HostOwned/HostOwnedManager.cs` | Unity coordinator for host-owned pattern, including runtime spawning |
| `docs/ownership_models.md` | Decision guide, side-by-side comparison, tradeoff checklist |

---

## Design decisions

### Two distinct example paths, not variations of one

Both examples are runnable end-to-end without modification to the other. A developer starting from scratch can follow one path entirely and never need to read the other. This is intentional: mixing the concepts early creates confusion.

The examples share no code except the SDK components they both use (`BiomataAgent`, `BiomataSimulationBootstrapper`). This makes the ownership distinction visible — you can diff the two coordinators and immediately see what changes.

### Engine-Owned sim.yaml uses IdleBrain (no Ollama dependency)

The engine-owned example uses `IdleBrain` so it runs with zero external dependencies. This lets a developer run it in under 30 seconds. A YAML comment explicitly says "swap IdleBrain for OllamaLLMBrain to enable dialogue — no Unity changes needed." This demonstrates a core engine-owned property: upgrading the brain is a YAML-only change.

### Host-Owned sim.yaml has no agents block

The sim.yaml deliberately omits the `agents:` key entirely (not an empty list). This communicates intent — the backend genuinely starts with nothing. A comment explains the `registry:` key is also absent and why (dynamic import at registration time handles brain discovery).

### EngineOwnedManager validates ownershipMode at Start()

The coordinator logs a warning if any discovered `BiomataAgent` has `ownershipMode != BindToExisting`. This catches a common mistake — placing a `CreateAtRuntime` agent in a scene that expects engine ownership. The validation costs nothing at runtime and eliminates a class of hard-to-debug protocol errors.

### HostOwnedManager uses Configure() instead of Inspector coupling

The spawner calls `agent.Configure(agentId, displayName, autoRegister, ownershipMode)` after `Instantiate`. Brain class and brain config stay on the prefab's Inspector; only the per-spawn fields (id, name, position) are set at spawn time. This follows the SDK's Configure-before-Start contract and avoids reflection.

The `AgentSpawnData` inner class is serializable so the spawn list is editable in the Inspector. A designer can add NPCs by adding rows — no code changes.

### Reconnect behavior documented but not abstracted

The disconnect handling in `HostOwnedManager.HandleDisconnected()` contains a comment about `reconnect=true` but does not implement it. Reconnect strategy is project-specific (do you clear agents and respawn? re-register with reconnect=true? show a loading screen?). Baking a specific strategy into the example would make it wrong for most projects. The comment directs developers to `docs/transport_runtime_agents.md` instead.

### Ownership docs structured as a decision guide

`docs/ownership_models.md` is organized around the decision, not the implementation:
1. The core question first
2. Engine-Owned: what it looks like in YAML + Inspector + code, then when to use it
3. Host-Owned: same structure
4. Side-by-side table (the quick reference)
5. Mixing models (the advanced case)
6. A five-question checklist for new projects

The implementation reference is at the end — developers reaching the doc for the first time read the decision logic before the technical index.

---

## File-by-file notes

### `examples/engine_owned/sim.yaml`

Three agents: two guards (`gate_guard_left`, `gate_guard_right`) and a merchant (`market_merchant`). All use `IdleBrain`. Agent IDs follow the convention `<role>_<discriminator>` to illustrate that IDs are arbitrary strings that must match Unity exactly. A YAML comment points to the Unity setup steps.

### `examples/host_owned/sim.yaml`

Engine + world block only. No `agents:`, no `registry:`, no `social:`. The comment on `registry:` explains why it is absent (not omitted by mistake). This file is the minimum viable backend for a host-owned integration.

### `unity_sdk/samples/EngineOwned/EngineOwnedManager.cs`

~130 lines. Discovers all `BiomataAgent` components in Start(), validates their mode, wires bootstrapper events, and updates a UI text pair. `HandleConnected()` has no agent-management code at all — that is the key pedagogical point, made explicit in a comment.

### `unity_sdk/samples/HostOwned/HostOwnedManager.cs`

~165 lines. Serializable `AgentSpawnData[]` array drives spawning. `SpawnAgents()` is guarded against double-spawn on reconnect. `SpawnAgent()` instantiates the prefab, gets the `BiomataAgent`, calls `Configure()`, and adds to the agent list. Unregistration is delegated entirely to `BiomataAgent.OnDestroy()`.

### `docs/ownership_models.md`

~280 lines. Contains inline code showing the YAML, Inspector state, and C# coordinator for each model. Side-by-side table has 12 rows covering the dimensions that matter most to a new integrator. The five-question checklist at the end is designed to be copied into a team's architecture decision record.

---

## Preserved behavior

- No existing files were modified.
- VillageLifeDemo, PatrolDemo, ProductionIntegration, and all other samples are unchanged.
- The examples reference `IdleBrain` and `HostedWorld` which already exist in the codebase. No new Python code is required to run `engine_owned/sim.yaml`.
