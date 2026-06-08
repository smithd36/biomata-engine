# Graph Report - unity_sdk  (2026-06-07)

## Corpus Check
- Corpus is ~31,616 words - fits in a single context window. You may not need a graph.

## Summary
- 866 nodes · 1243 edges · 53 communities (45 shown, 8 thin omitted)
- Extraction: 99% EXTRACTED · 1% INFERRED · 0% AMBIGUOUS · INFERRED: 9 edges (avg confidence: 0.88)
- Token cost: 0 input · 0 output

## Community Hubs (Navigation)
- [[_COMMUNITY_WebSocket Transport Layer|WebSocket Transport Layer]]
- [[_COMMUNITY_Action Handler Base System|Action Handler Base System]]
- [[_COMMUNITY_BiomataManager & Editor|BiomataManager & Editor]]
- [[_COMMUNITY_Action Definitions Library|Action Definitions Library]]
- [[_COMMUNITY_Move Action Handler|Move Action Handler]]
- [[_COMMUNITY_SDK Config & Bootstrap|SDK Config & Bootstrap]]
- [[_COMMUNITY_Idle Action Handler|Idle Action Handler]]
- [[_COMMUNITY_Unity Simulation Manager|Unity Simulation Manager]]
- [[_COMMUNITY_Transport & Agent Registration|Transport & Agent Registration]]
- [[_COMMUNITY_BiomataManager Core|BiomataManager Core]]
- [[_COMMUNITY_Unity Agent Bridge|Unity Agent Bridge]]
- [[_COMMUNITY_Event Visualizer|Event Visualizer]]
- [[_COMMUNITY_Agent Prefab & Thought Bubble|Agent Prefab & Thought Bubble]]
- [[_COMMUNITY_Agent Bridge Binding|Agent Bridge Binding]]
- [[_COMMUNITY_Host Owned Agent Manager|Host Owned Agent Manager]]
- [[_COMMUNITY_Event Stream Client|Event Stream Client]]
- [[_COMMUNITY_gRPC Platform Utilities|gRPC Platform Utilities]]
- [[_COMMUNITY_Snapshot Client|Snapshot Client]]
- [[_COMMUNITY_Simulation Client|Simulation Client]]
- [[_COMMUNITY_Package Metadata|Package Metadata]]
- [[_COMMUNITY_Module 20|Module 20]]
- [[_COMMUNITY_Module 21|Module 21]]
- [[_COMMUNITY_Module 22|Module 22]]
- [[_COMMUNITY_Module 23|Module 23]]
- [[_COMMUNITY_Module 24|Module 24]]
- [[_COMMUNITY_Module 25|Module 25]]
- [[_COMMUNITY_Module 26|Module 26]]
- [[_COMMUNITY_Module 27|Module 27]]
- [[_COMMUNITY_Module 28|Module 28]]
- [[_COMMUNITY_Module 29|Module 29]]
- [[_COMMUNITY_Module 30|Module 30]]
- [[_COMMUNITY_Module 31|Module 31]]
- [[_COMMUNITY_Module 32|Module 32]]
- [[_COMMUNITY_Module 33|Module 33]]
- [[_COMMUNITY_Module 34|Module 34]]
- [[_COMMUNITY_Module 35|Module 35]]
- [[_COMMUNITY_Module 36|Module 36]]
- [[_COMMUNITY_Module 37|Module 37]]
- [[_COMMUNITY_Module 38|Module 38]]
- [[_COMMUNITY_Module 39|Module 39]]
- [[_COMMUNITY_Module 40|Module 40]]
- [[_COMMUNITY_Module 41|Module 41]]
- [[_COMMUNITY_Module 42|Module 42]]
- [[_COMMUNITY_Module 43|Module 43]]
- [[_COMMUNITY_Module 44|Module 44]]
- [[_COMMUNITY_Module 45|Module 45]]
- [[_COMMUNITY_Module 46|Module 46]]
- [[_COMMUNITY_Module 47|Module 47]]
- [[_COMMUNITY_Module 48|Module 48]]
- [[_COMMUNITY_Module 49|Module 49]]
- [[_COMMUNITY_Module 50|Module 50]]
- [[_COMMUNITY_Module 51|Module 51]]
- [[_COMMUNITY_Module 52|Module 52]]

## God Nodes (most connected - your core abstractions)
1. `WebSocketTransport` - 36 edges
2. `UnitySimulationManager` - 29 edges
3. `BiomataSimulationBootstrapper` - 28 edges
4. `UnityAgentBridge` - 22 edges
5. `EventVisualizer` - 22 edges
6. `MoveActionHandler` - 19 edges
7. `Task` - 19 edges
8. `BiomataManager` - 18 edges
9. `BiomataAgentEditor` - 17 edges
10. `EventStreamClient` - 17 edges

## Surprising Connections (you probably didn't know these)
- `OllamaLLMBrain Plugin` --implements--> `Brain Contract (src/contracts/brain.py)`  [INFERRED]
  examples/host_owned/sim.yaml → CONTRIBUTING.md
- `HostedWorld Plugin` --implements--> `World Contract (src/contracts/world.py)`  [INFERRED]
  examples/engine_owned/sim.yaml → CONTRIBUTING.md
- `Engine-Owned sim.yaml` --references--> `IdleBrain Plugin`  [EXTRACTED]
  examples/engine_owned/sim.yaml → CONTRIBUTING.md
- `Engine-Owned Ownership Pattern` --semantically_similar_to--> `Host-Owned Ownership Pattern`  [INFERRED] [semantically similar]
  examples/engine_owned/sim.yaml → examples/host_owned/sim.yaml
- `BindToExisting Ownership Mode` --semantically_similar_to--> `CreateAtRuntime Ownership Mode`  [INFERRED] [semantically similar]
  examples/engine_owned/sim.yaml → examples/host_owned/sim.yaml

## Hyperedges (group relationships)
- **Engine-Owned Agent Lifecycle Workflow** — engine_owned_pattern, bind_to_existing_mode, engine_owned_manager, biomata_agent [EXTRACTED 1.00]
- **Host-Owned Agent Lifecycle Workflow** — host_owned_pattern, create_at_runtime_mode, host_owned_manager, biomata_agent [EXTRACTED 1.00]
- **Action Validation Pipeline** — actions_yaml, action_manifest_class, biomata_actions_json [EXTRACTED 1.00]

## Communities (53 total, 8 thin omitted)

### Community 0 - "WebSocket Transport Layer"
Cohesion: 0.08
Nodes (28): AgentId, ClientWebSocket, IReadOnlyList, ITransport, Msg, AgentDecisionResult, AgentObservationData, AgentRegistration (+20 more)

### Community 1 - "Action Handler Base System"
Cohesion: 0.05
Nodes (27): ActionHandlerBase, Biomata.Integration.Actions, ActionExecutor, Biomata.Integration, AgentThoughtBubble, Biomata.Integration, AnchorEntry, Biomata.Integration (+19 more)

### Community 2 - "BiomataManager & Editor"
Cohesion: 0.07
Nodes (19): BiomataManager, Editor, Biomata.SDK.Editor, BiomataManagerEditor, GUIStyle, Task, ActionHandlerBase, AgentOwnershipMode (+11 more)

### Community 3 - "Action Definitions Library"
Cohesion: 0.08
Nodes (39): alert Action, detain Action, follow Action, greet Action, idle Action, interact Action, ActionManifest Python Class, move Action (+31 more)

### Community 4 - "Move Action Handler"
Cohesion: 0.09
Nodes (22): Biomata.Integration.Actions, MoveActionHandler, Biomata.Integration.Actions, NavMeshMoveActionHandler, BiomataPOIData, NavMeshAgent, AgentDecisionResult, Dictionary (+14 more)

### Community 5 - "SDK Config & Bootstrap"
Cohesion: 0.09
Nodes (12): BiomataSimulationConfig, Biomata.SDK, BiomataException, bool, Exception, float, IEnumerator, int (+4 more)

### Community 6 - "Idle Action Handler"
Cohesion: 0.07
Nodes (23): ActionHandlerBase, Biomata.Integration.Actions, IdleActionHandler, Biomata.Integration.Actions, InteractActionHandler, Biomata.Integration.Actions, SpeakActionHandler, AgentDecisionResult (+15 more)

### Community 7 - "Unity Simulation Manager"
Cohesion: 0.09
Nodes (14): AgentObservationData, bool, CancellationTokenSource, Dictionary, float, IEnumerator, int, List (+6 more)

### Community 8 - "Transport & Agent Registration"
Cohesion: 0.15
Nodes (12): AgentObservationData, AgentRegistration, CancellationToken, Dictionary, HealthStatus, IEnumerable, RolesData, SnapshotData (+4 more)

### Community 9 - "BiomataManager Core"
Cohesion: 0.13
Nodes (14): AgentObservationData, BiomataConfig, bool, CancellationToken, Dictionary, float, IEnumerable, int (+6 more)

### Community 10 - "Unity Agent Bridge"
Cohesion: 0.10
Nodes (11): Biomata.Integration, UnityAgentBridge, ObservationCollector, ActionExecutor, AgentDecisionResult, AgentObservationData, bool, Dictionary (+3 more)

### Community 11 - "Event Visualizer"
Cohesion: 0.10
Nodes (11): Biomata.Integration, EventVisualizer, KeyCode, bool, GUIStyle, int, List, SimulationEvent (+3 more)

### Community 12 - "Agent Prefab & Thought Bubble"
Cohesion: 0.16
Nodes (12): Action<float>, BiomataAgent, float, GameObject, List, MenuItem, string, Transform (+4 more)

### Community 13 - "Agent Bridge Binding"
Cohesion: 0.12
Nodes (11): Biomata.Integration, BiomataAgent, CheckDuplicateIdInEditor(), OnValidate(), AgentOwnershipMode, bool, Dictionary, JObject (+3 more)

### Community 14 - "Host Owned Agent Manager"
Cohesion: 0.14
Nodes (12): AgentSpawnData, AgentSpawnData, Biomata.Samples, HostOwnedManager, BiomataSimulationBootstrapper, GameObject, int, List (+4 more)

### Community 15 - "Event Stream Client"
Cohesion: 0.15
Nodes (11): Biomata.SDK.Clients, EventStreamClient, Action, CancellationToken, CancellationTokenSource, Dictionary, ITransport, object (+3 more)

### Community 16 - "gRPC Platform Utilities"
Cohesion: 0.22
Nodes (21): bytes, Path, download(), extract_dll(), fetch_grpc_tools(), host_grpc_tools_platform(), log(), main() (+13 more)

### Community 17 - "Snapshot Client"
Cohesion: 0.27
Nodes (6): Biomata.SDK.Clients, SnapshotClient, CancellationToken, ITransport, SnapshotData, Task

### Community 18 - "Simulation Client"
Cohesion: 0.18
Nodes (11): Biomata.SDK, SimulationClient, IAsyncDisposable, IDisposable, BiomataConfig, CancellationToken, ConnectionState, ITransport (+3 more)

### Community 19 - "Package Metadata"
Cohesion: 0.12
Nodes (16): author, email, name, url, changelogUrl, dependencies, com.unity.nuget.newtonsoft-json, description (+8 more)

### Community 20 - "Module 20"
Cohesion: 0.21
Nodes (7): Biomata.Samples, EngineOwnedManager, BiomataSimulationBootstrapper, int, List, Text, TickResult

### Community 21 - "Module 21"
Cohesion: 0.15
Nodes (9): Biomata.Integration.Observations, POIObservationProvider, bool, Dictionary, float, int, List, string (+1 more)

### Community 22 - "Module 22"
Cohesion: 0.17
Nodes (5): Biomata.Integration, ObservationCollector, Dictionary, List, ObservationProviderBase

### Community 23 - "Module 23"
Cohesion: 0.21
Nodes (9): Biomata.SDK.Clients, TickClient, AgentObservationData, CancellationToken, Dictionary, IEnumerable, ITransport, Task (+1 more)

### Community 24 - "Module 24"
Cohesion: 0.23
Nodes (6): RoleEntry, bool, RolesData, string, Biomata.Integration.Simulation, RoleManifestLoader

### Community 25 - "Module 25"
Cohesion: 0.23
Nodes (7): Dictionary, JArray, JObject, JToken, List, Biomata.SDK.Transport, JsonHelpers

### Community 26 - "Module 26"
Cohesion: 0.24
Nodes (8): Biomata.SDK.Clients, ObservationClient, AgentObservationData, CancellationToken, Dictionary, IEnumerable, ITransport, Task

### Community 27 - "Module 27"
Cohesion: 0.31
Nodes (6): AgentClient, Biomata.SDK.Clients, AgentRegistration, CancellationToken, ITransport, Task

### Community 28 - "Module 28"
Cohesion: 0.24
Nodes (6): Biomata.SDK, MainThreadDispatcher, Action, int, RuntimeInitializeOnLoadMethod, SynchronizationContext

### Community 29 - "Module 29"
Cohesion: 0.27
Nodes (6): Dictionary, IEnumerable, MenuItem, ActionManifestValidator, Biomata.Editor, Type

### Community 30 - "Module 30"
Cohesion: 0.22
Nodes (7): agent, BiomataAgent, List, MenuItem, Biomata.Editor, RoleManifestValidator, source

### Community 31 - "Module 31"
Cohesion: 0.27
Nodes (4): Action, SerializedProperty, Biomata.Integration.Editor, BiomataSimulationBootstrapperEditor

### Community 32 - "Module 32"
Cohesion: 0.33
Nodes (6): ActionManifestLoader, Biomata.Integration.Actions, ManifestActionEntry, ManifestData, ActionExecutor, string

### Community 33 - "Module 33"
Cohesion: 0.20
Nodes (7): Biomata.Integration.Observations, NearbyAgentsObservationProvider, bool, Dictionary, float, int, List

### Community 34 - "Module 34"
Cohesion: 0.33
Nodes (6): Biomata.SDK.Clients, HealthClient, CancellationToken, HealthStatus, ITransport, Task

### Community 35 - "Module 35"
Cohesion: 0.28
Nodes (8): Biomata.SDK, BiomataConfig, RetryConfig, RetryConfig, bool, float, int, string

### Community 36 - "Module 36"
Cohesion: 0.22
Nodes (6): Biomata.Integration.Observations, LineOfSightProvider, Dictionary, float, LayerMask, Vector3

### Community 37 - "Module 37"
Cohesion: 0.25
Nodes (6): Biomata.SDK.Clients, RolesClient, CancellationToken, ITransport, RolesData, Task

### Community 38 - "Module 38"
Cohesion: 0.25
Nodes (5): Biomata.Integration.Observations, NearbyActorsProvider, Dictionary, float, LayerMask

### Community 39 - "Module 39"
Cohesion: 0.25
Nodes (6): ObservationProviderBase, Biomata.Integration.Observations, TimeObservationProvider, bool, Dictionary, float

### Community 40 - "Module 40"
Cohesion: 0.25
Nodes (5): Biomata.Integration.Observations, TransformObservationProvider, Rigidbody, bool, Dictionary

### Community 41 - "Module 41"
Cohesion: 0.25
Nodes (7): bool, float, int, string, ScriptableObject, Biomata.Integration, BiomataSimulationConfig

### Community 42 - "Module 42"
Cohesion: 0.29
Nodes (4): Biomata.SDK.Models, TickResult, AgentDecisionResult, Dictionary

### Community 43 - "Module 43"
Cohesion: 0.33
Nodes (3): Biomata.SDK.Models, HealthStatus, SnapshotData

### Community 44 - "Module 44"
Cohesion: 0.60
Nodes (4): Biomata.SDK.Models, RoleEntry, RolesData, string

## Knowledge Gaps
- **287 isolated node(s):** `name`, `version`, `displayName`, `description`, `unity` (+282 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **8 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `BiomataSimulationBootstrapper` connect `SDK Config & Bootstrap` to `Action Handler Base System`?**
  _High betweenness centrality (0.023) - this node is a cross-community bridge._
- **Why does `UnitySimulationManager` connect `Unity Simulation Manager` to `Action Handler Base System`?**
  _High betweenness centrality (0.022) - this node is a cross-community bridge._
- **Why does `BiomataManager` connect `BiomataManager Core` to `Action Handler Base System`?**
  _High betweenness centrality (0.017) - this node is a cross-community bridge._
- **What connects `name`, `version`, `displayName` to the rest of the system?**
  _298 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `WebSocket Transport Layer` be split into smaller, more focused modules?**
  _Cohesion score 0.08455625436757512 - nodes in this community are weakly interconnected._
- **Should `Action Handler Base System` be split into smaller, more focused modules?**
  _Cohesion score 0.04927536231884058 - nodes in this community are weakly interconnected._
- **Should `BiomataManager & Editor` be split into smaller, more focused modules?**
  _Cohesion score 0.06866002214839424 - nodes in this community are weakly interconnected._