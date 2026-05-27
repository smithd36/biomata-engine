// Biomata SDK — Host-Owned Sample
//
// Canonical example of the CreateAtRuntime ownership pattern.
//
// The backend (examples/host_owned/sim.yaml) starts with no agents.
// Unity spawns NPC prefabs, registers them with the backend on connect,
// and unregisters them on destroy. Unity is the source of truth for
// which agents exist.
//
// Setup
// ─────
// 1. Start the backend:
//      biomata-ws --config examples/host_owned/sim.yaml --port 8765
//
// 2. Build an NPC prefab:
//      • Add BiomataAgent component.
//      • Ownership Mode  → CreateAtRuntime
//      • Brain Class     → e.g. src.plugins.builtin.ollama.brain.OllamaLLMBrain
//      • Brain Config    → {"model": "qwen2.5:14b"}  (optional JSON)
//      • Auto Register   → ✓
//
// 3. Add BiomataSimulationBootstrapper to the scene.
//
// 4. Place this component on a persistent coordinator GameObject.
//    Assign the bootstrapper and npcPrefab in the Inspector.
//    Edit the agentSpawnData list to define which NPCs to spawn.
//
// What happens at runtime
// ───────────────────────
// • On connect: SpawnAgents() instantiates each prefab and configures
//   the BiomataAgent with a unique id, name, and position.
// • BiomataAgent.Start() sees the manager is connected → fires
//   RegisterAsync → backend creates the agent with the specified brain.
// • Each tick: observations flow from Unity → decisions flow back.
// • On destroy (scene unload, explicit Destroy): BiomataAgent.OnDestroy()
//   fires TryRemoveAsync → backend removes the agent.
//
// Key property: the backend is a stateless execution engine. Reloading
// the scene fully resets the agent roster. The backend never persists
// agent definitions between Unity sessions.

using System.Collections.Generic;
using Biomata.Integration;
using Biomata.SDK.Models;
using UnityEngine;
using UnityEngine.UI;

namespace Biomata.Samples
{
    [AddComponentMenu("Biomata/Samples/Host Owned Manager")]
    public class HostOwnedManager : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("Simulation")]
        [SerializeField] private BiomataSimulationBootstrapper bootstrapper;

        [Header("NPC Prefab")]
        [Tooltip("Prefab with BiomataAgent configured in CreateAtRuntime mode.")]
        [SerializeField] private GameObject npcPrefab;

        [Header("Spawn Data")]
        [Tooltip("One entry per NPC. Configure id, name, and spawn position.")]
        [SerializeField] private AgentSpawnData[] agentSpawnData =
        {
            new AgentSpawnData { agentId = "scout_001", displayName = "Scout",    position = new Vector3(-3f, 0f, 0f) },
            new AgentSpawnData { agentId = "guard_001", displayName = "Guard",    position = new Vector3( 0f, 0f, 0f) },
            new AgentSpawnData { agentId = "healer_001", displayName = "Healer",  position = new Vector3( 3f, 0f, 0f) },
        };

        [Header("UI (optional)")]
        [SerializeField] private Text statusText;
        [SerializeField] private Text agentListText;

        // ── Runtime state ─────────────────────────────────────────────────────

        private readonly List<BiomataAgent> _agents = new();
        private int _tickCount;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void Start()
        {
            if (bootstrapper == null)
            {
                Debug.LogError(
                    "[HostOwned] No BiomataSimulationBootstrapper assigned. " +
                    "Drag one into the Bootstrapper slot.", this);
                return;
            }

            bootstrapper.OnConnected    += HandleConnected;
            bootstrapper.OnDisconnected += HandleDisconnected;
            bootstrapper.OnTickComplete += HandleTickComplete;
            bootstrapper.OnTickError    += ex => SetStatus($"Tick error: {ex?.Message}");

            // If already connected by the time Start() runs, spawn immediately.
            if (bootstrapper.IsConnected)
                SpawnAgents();
            else
                SetStatus("Waiting for connection…");
        }

        private void OnDestroy()
        {
            if (bootstrapper == null) return;
            bootstrapper.OnConnected    -= HandleConnected;
            bootstrapper.OnDisconnected -= HandleDisconnected;
            bootstrapper.OnTickComplete -= HandleTickComplete;
            // Spawned agents unregister themselves via BiomataAgent.OnDestroy().
            // No extra cleanup needed here.
        }

        // ── Bootstrapper events ───────────────────────────────────────────────

        private void HandleConnected()
        {
            SpawnAgents();
        }

        private void HandleDisconnected()
        {
            // Spawned agents are still alive in the scene. Their IsRegistered stays true
            // locally. On reconnect they will attempt to re-register (CreateAtRuntime
            // with reconnect=false will fail with AGENT_EXISTS if the backend remembers
            // them — use reconnect=true on the bridge if the backend may survive the drop).
            SetStatus("Disconnected — agents paused");
            RefreshAgentList();
        }

        private void HandleTickComplete(TickResult result)
        {
            _tickCount = result.Tick;
            SetStatus($"Tick {_tickCount}  |  {_agents.Count} agent(s)  |  {result.Decisions?.Count ?? 0} decision(s)");
            RefreshAgentList();
        }

        // ── Agent spawning ────────────────────────────────────────────────────

        private void SpawnAgents()
        {
            if (npcPrefab == null)
            {
                Debug.LogError("[HostOwned] npcPrefab is not assigned. Assign a prefab with BiomataAgent.", this);
                return;
            }

            // Guard against double-spawn on rapid reconnect.
            if (_agents.Count > 0) return;

            foreach (var data in agentSpawnData)
                SpawnAgent(data);

            SetStatus($"Connected  |  {_agents.Count} agent(s) registering…");
            RefreshAgentList();
        }

        private void SpawnAgent(AgentSpawnData data)
        {
            var go = Instantiate(npcPrefab, data.position, Quaternion.identity);
            go.name = data.displayName;

            var agent = go.GetComponent<BiomataAgent>();
            if (agent == null)
            {
                Debug.LogError(
                    $"[HostOwned] npcPrefab '{npcPrefab.name}' has no BiomataAgent component.", this);
                Destroy(go);
                return;
            }

            // Override the prefab's Inspector values with spawn-time data.
            // ownershipMode and brainClass come from the prefab Inspector;
            // id, name, and position are set here per-spawn.
            agent.Configure(
                agentId:      data.agentId,
                displayName:  data.displayName,
                autoRegister: true,
                ownershipMode: AgentOwnershipMode.CreateAtRuntime);

            _agents.Add(agent);
            Debug.Log($"[HostOwned] Spawned '{data.displayName}' ({data.agentId}) at {data.position}");
        }

        // ── UI ────────────────────────────────────────────────────────────────

        private void SetStatus(string msg)
        {
            if (statusText != null) statusText.text = msg;
            else Debug.Log($"[HostOwned] {msg}");
        }

        private void RefreshAgentList()
        {
            if (agentListText == null) return;

            var sb = new System.Text.StringBuilder();
            foreach (var agent in _agents)
            {
                if (agent == null) continue;
                var reg    = agent.IsRegistered ? "registered" : "registering";
                var action = agent.LastDecision?.Action ?? "—";
                sb.AppendLine($"{agent.DisplayName}  [{reg}]  last: {action}");
            }
            agentListText.text = sb.Length > 0 ? sb.ToString() : "(no agents)";
        }

        // ── Data types ────────────────────────────────────────────────────────

        [System.Serializable]
        public class AgentSpawnData
        {
            [Tooltip("Must be unique within the simulation.")]
            public string  agentId;
            public string  displayName;
            public Vector3 position;
        }
    }
}
