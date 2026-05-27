// Biomata SDK — Production Integration Sample
//
// Demonstrates the intended production workflow:
//   • Pre-authored scene — environment and NPCs placed in the Editor
//   • BiomataAgent on each NPC — configured in Inspector
//   • BiomataSimulationBootstrapper on this GameObject — driven by a config asset
//   • This script — thin coordinator; no scene building, no agent management
//
// Setup
// ─────
// 1. Place this component on a persistent "Manager" GameObject.
// 2. Add BiomataSimulationBootstrapper to the same (or another) GameObject.
// 3. Drag the bootstrapper into the Bootstrapper slot below.
// 4. Optionally assign a BiomataSimulationConfig asset to the bootstrapper.
// 5. Add BiomataAgent to each NPC prefab and configure in the Inspector.
// 6. Press Play.
//
// See README.md in this folder for the full scene hierarchy and step-by-step guide.
//
// Backend:
//   biomata-ws --config <your-sim.yaml> --port 8765

using System.Collections.Generic;
using Biomata.Integration;
using Biomata.SDK.Models;
using UnityEngine;
using UnityEngine.UI;

namespace Biomata.Samples
{
    [AddComponentMenu("Biomata/Samples/Production Integration Manager")]
    public class ProductionIntegrationManager : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("Simulation")]
        [Tooltip("The BiomataSimulationBootstrapper that manages the backend connection. " +
                 "Can be on this GameObject or any other persistent object in the scene.")]
        [SerializeField] private BiomataSimulationBootstrapper bootstrapper;

        [Header("UI (optional)")]
        [Tooltip("Text component for connection / tick status. Leave empty to skip.")]
        [SerializeField] private Text statusText;

        [Tooltip("Text component that lists every registered agent and its last action. Leave empty to skip.")]
        [SerializeField] private Text agentListText;

        // ── Runtime state ─────────────────────────────────────────────────────

        private readonly List<BiomataAgent> _agents = new();
        private int   _tickCount;
        private float _lastTickMs;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void Start()
        {
            if (bootstrapper == null)
            {
                Debug.LogError(
                    "[ProductionIntegration] No BiomataSimulationBootstrapper assigned. " +
                    "Drag one into the Bootstrapper slot on ProductionIntegrationManager.");
                return;
            }

            bootstrapper.OnConnected    += HandleConnected;
            bootstrapper.OnDisconnected += HandleDisconnected;
            bootstrapper.OnTickComplete += HandleTickComplete;
            bootstrapper.OnTickError    += ex => SetStatus($"Tick error: {ex?.Message}");

            // Discover every BiomataAgent in the scene — they registered themselves in Awake.
            _agents.AddRange(FindObjectsByType<BiomataAgent>(FindObjectsSortMode.None));
            Debug.Log($"[ProductionIntegration] Found {_agents.Count} BiomataAgent(s) in scene.");

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

        // ── Bootstrapper event handlers ───────────────────────────────────────

        private void HandleConnected()
        {
            SetStatus($"Connected  |  {_agents.Count} agent(s)  |  tick rate: {GetTickRateLabel()}");
            RefreshAgentList();
        }

        private void HandleDisconnected()
        {
            SetStatus("Disconnected — check backend and press Play again");
            RefreshAgentList();
        }

        private void HandleTickComplete(TickResult result)
        {
            _tickCount  = result.Tick;
            _lastTickMs = bootstrapper.LastTickDurationMs;
            SetStatus(
                $"Tick {_tickCount}  |  {result.Decisions?.Count ?? 0} decision(s)  |  {_lastTickMs:F0} ms");
            RefreshAgentList();
        }

        // ── UI helpers ────────────────────────────────────────────────────────

        private void SetStatus(string msg)
        {
            if (statusText != null)
                statusText.text = msg;
            else
                Debug.Log($"[ProductionIntegration] {msg}");
        }

        private void RefreshAgentList()
        {
            if (agentListText == null) return;

            var sb = new System.Text.StringBuilder();
            foreach (var agent in _agents)
            {
                if (agent == null) continue;
                var d      = agent.LastDecision;
                string action  = d != null ? d.Action      : "waiting";
                string outcome = d != null ? Truncate(d.OutcomeText ?? "", 48) : "";
                sb.AppendLine($"{agent.DisplayName}  [{action}]  {outcome}");
            }
            agentListText.text = sb.Length > 0 ? sb.ToString() : "(no agents)";
        }

        private string GetTickRateLabel()
        {
            if (bootstrapper.Config != null)
                return $"{bootstrapper.Config.tickRate:F1}/s (config asset)";
            return "see bootstrapper";
        }

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s[..max] + "…";
    }
}
