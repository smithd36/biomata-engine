// Biomata SDK — Engine-Owned Sample
//
// Canonical example of the BindToExisting ownership pattern.
//
// The backend (examples/engine_owned/sim.yaml) declares every agent.
// Unity binds a visual shell to each one — no registration RPC is sent.
//
// Setup
// ─────
// 1. Start the backend:
//      biomata-ws --config examples/engine_owned/sim.yaml --port 8765
//
// 2. Place BiomataAgent on each NPC prefab. In the Inspector:
//      Ownership Mode  → BindToExisting
//      Agent ID        → must match the id in sim.yaml exactly
//      Auto Bind       → ✓ (bind automatically when manager connects)
//
// 3. Add BiomataSimulationBootstrapper to the scene (or use the config asset).
//
// 4. Place this component on a persistent coordinator GameObject and
//    drag the bootstrapper into the Bootstrapper slot.
//
// What happens at runtime
// ───────────────────────
// • BiomataAgent.Start() sees the manager is connected → calls
//   Bridge.MarkBoundToExisting() → IsRegistered = true.
// • No register_agent RPC is ever sent. The backend agent was already
//   defined in sim.yaml and is already ticking.
// • Observations flow out (position, nearby agents, etc.) each tick.
// • The backend brain returns decisions; action handlers execute them.
//
// Key property: the simulation keeps running even when Unity disconnects.
// Reconnecting clients rebind to the same agents with no side effects.

using System.Collections.Generic;
using Biomata.Integration;
using Biomata.SDK.Models;
using UnityEngine;
using UnityEngine.UI;

namespace Biomata.Samples
{
    [AddComponentMenu("Biomata/Samples/Engine Owned Manager")]
    public class EngineOwnedManager : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("Simulation")]
        [SerializeField] private BiomataSimulationBootstrapper bootstrapper;

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
                    "[EngineOwned] No BiomataSimulationBootstrapper assigned. " +
                    "Drag one into the Bootstrapper slot.", this);
                return;
            }

            // Discover all pre-placed BiomataAgent components.
            // In BindToExisting mode, each agent binds itself on connect —
            // this list is purely for display and event forwarding.
            _agents.AddRange(FindObjectsByType<BiomataAgent>(FindObjectsSortMode.None));

            // Validate that all discovered agents are in the correct mode.
            foreach (var agent in _agents)
            {
                if (agent.OwnershipMode != AgentOwnershipMode.BindToExisting)
                    Debug.LogWarning(
                        $"[EngineOwned] '{agent.name}' has ownershipMode = " +
                        $"{agent.OwnershipMode}. Expected BindToExisting in this pattern.", agent);
            }

            Debug.Log($"[EngineOwned] Found {_agents.Count} agent(s) to bind.");

            bootstrapper.OnConnected    += HandleConnected;
            bootstrapper.OnDisconnected += HandleDisconnected;
            bootstrapper.OnTickComplete += HandleTickComplete;
            bootstrapper.OnTickError    += ex => SetStatus($"Tick error: {ex?.Message}");

            SetStatus("Waiting for connection…");
            RefreshAgentList();
        }

        private void OnDestroy()
        {
            if (bootstrapper == null) return;
            bootstrapper.OnConnected    -= HandleConnected;
            bootstrapper.OnDisconnected -= HandleDisconnected;
            bootstrapper.OnTickComplete -= HandleTickComplete;
        }

        // ── Bootstrapper events ───────────────────────────────────────────────

        private void HandleConnected()
        {
            // Agents bind themselves in BiomataAgent.Start() / HandleManagerConnectedBind().
            // Nothing to do here — no registration RPCs, no agent management.
            SetStatus($"Connected  |  {_agents.Count} agent(s) binding…");
            RefreshAgentList();
        }

        private void HandleDisconnected()
        {
            // Agents remain registered on the backend. IsRegistered stays true locally —
            // the binding is still valid; observations will resume on reconnect.
            SetStatus("Disconnected — simulation still running on backend");
            RefreshAgentList();
        }

        private void HandleTickComplete(TickResult result)
        {
            _tickCount = result.Tick;
            SetStatus($"Tick {_tickCount}  |  {result.Decisions?.Count ?? 0} decision(s)");
            RefreshAgentList();
        }

        // ── UI ────────────────────────────────────────────────────────────────

        private void SetStatus(string msg)
        {
            if (statusText != null) statusText.text = msg;
            else Debug.Log($"[EngineOwned] {msg}");
        }

        private void RefreshAgentList()
        {
            if (agentListText == null) return;

            var sb = new System.Text.StringBuilder();
            foreach (var agent in _agents)
            {
                if (agent == null) continue;
                var bound  = agent.IsRegistered ? "bound" : "unbound";
                var action = agent.LastDecision?.Action ?? "—";
                sb.AppendLine($"{agent.DisplayName}  [{bound}]  last: {action}");
            }
            agentListText.text = sb.Length > 0 ? sb.ToString() : "(no agents found)";
        }
    }
}
