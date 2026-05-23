using System;
using System.Collections;
using System.Collections.Generic;
using Biomata.SDK.Models;
using UnityEngine;

namespace Biomata.Integration
{
    /// <summary>
    /// Links a Unity GameObject to a backend simulation agent.
    ///
    /// Place on any NPC GameObject alongside <see cref="ObservationCollector"/> and
    /// <see cref="ActionExecutor"/>. The bridge auto-registers with the active
    /// <see cref="UnitySimulationManager"/> and handles the per-tick cycle:
    ///   Observation → Backend decision → Action execution
    ///
    /// Agent ID is stable across sessions; set it explicitly to match the YAML config,
    /// or leave empty to generate one from the GameObject name.
    /// </summary>
    [AddComponentMenu("Biomata/Agent Bridge")]
    [RequireComponent(typeof(ObservationCollector))]
    [RequireComponent(typeof(ActionExecutor))]
    public class UnityAgentBridge : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────────

        [Header("Identity")]
        [Tooltip("Unique agent ID. Leave empty to auto-generate from the GameObject name.")]
        [SerializeField] private string agentId;
        [Tooltip("Display name shown in logs and events. Defaults to the GameObject name.")]
        [SerializeField] private string agentName;

        [Header("Brain")]
        [Tooltip("Fully-qualified Python class path for the agent brain.")]
        [SerializeField] private string brainClass  = "src.plugins.builtin.replay_brain.brain.ReplayBrain";
        [SerializeField] private string memoryClass = "";

        [Header("Lifecycle")]
        [Tooltip("Automatically register with the backend when the manager connects.")]
        [SerializeField] private bool autoRegister = true;

        // ── Events ────────────────────────────────────────────────────────────────

        /// <summary>Fired on the main thread each time the backend sends a decision.</summary>
        public event Action<AgentDecisionResult> OnDecisionReceived;

        /// <summary>Fired just before an action handler coroutine starts.</summary>
        public event Action<string> OnActionStarted;

        /// <summary>Fired after the action handler coroutine completes.</summary>
        public event Action<string> OnActionCompleted;

        // ── Public state ──────────────────────────────────────────────────────────

        /// <summary>Stable agent identifier, set at Awake time.</summary>
        public string AgentId   => agentId;

        /// <summary>Display name used in logs and event payloads.</summary>
        public string AgentName => agentName;

        /// <summary>True once the backend has acknowledged the registration RPC.</summary>
        public bool IsRegistered { get; private set; }

        /// <summary>The most recent decision received from the backend, or <c>null</c>.</summary>
        public AgentDecisionResult LastDecision { get; private set; }

        // ── Private ───────────────────────────────────────────────────────────────

        private ObservationCollector    _collector;
        private ActionExecutor          _executor;
        private UnitySimulationManager  _manager;

        // ── Lifecycle ─────────────────────────────────────────────────────────────

        private void Awake()
        {
            if (string.IsNullOrEmpty(agentId))
                agentId = $"{gameObject.name}_{GetInstanceID():x8}";
            if (string.IsNullOrEmpty(agentName))
                agentName = gameObject.name;

            _collector = GetComponent<ObservationCollector>();
            _executor  = GetComponent<ActionExecutor>();
        }

        private void Start()
        {
            _manager = UnitySimulationManager.Instance ?? FindFirstObjectByType<UnitySimulationManager>();

            if (_manager == null)
            {
                Debug.LogWarning(
                    $"[Biomata] Agent '{agentId}': UnitySimulationManager not found. " +
                    "Add one to the scene.");
                return;
            }

            _manager.RegisterBridge(this);

            if (autoRegister)
            {
                if (_manager.IsConnected)
                    StartCoroutine(RegisterCoroutine());
                else
                    _manager.OnConnected += HandleManagerConnected;
            }
        }

        private void OnDestroy()
        {
            if (_manager == null) return;
            _manager.UnregisterBridge(this);
            _manager.OnConnected -= HandleManagerConnected;
        }

        private void HandleManagerConnected()
        {
            _manager.OnConnected -= HandleManagerConnected;
            StartCoroutine(RegisterCoroutine());
        }

        // ── Registration ──────────────────────────────────────────────────────────

        /// <summary>Manually trigger backend registration (no-op when already registered).</summary>
        public void Register()
        {
            if (!IsRegistered)
                StartCoroutine(RegisterCoroutine());
        }

        /// <summary>Remove this agent from the running simulation.</summary>
        public void Unregister() => StartCoroutine(UnregisterCoroutine());

        private IEnumerator RegisterCoroutine()
        {
            if (_manager?.Client == null) yield break;

            var reg = new AgentRegistration
            {
                AgentId     = agentId,
                AgentName   = agentName,
                BrainClass  = brainClass,
                MemoryClass = string.IsNullOrEmpty(memoryClass) ? null : memoryClass,
            };

            var task = _manager.Client.Agents.RegisterAsync(reg);
            while (!task.IsCompleted)
                yield return null;

            if (task.IsFaulted)
                Debug.LogWarning(
                    $"[Biomata] Registration failed for '{agentId}': " +
                    task.Exception?.GetBaseException().Message);
            else
            {
                IsRegistered = true;
                Debug.Log($"[Biomata] Registered: {agentId} ({agentName})");
            }
        }

        private IEnumerator UnregisterCoroutine()
        {
            if (_manager?.Client == null) yield break;

            var task = _manager.Client.Agents.TryRemoveAsync(agentId);
            while (!task.IsCompleted)
                yield return null;

            IsRegistered = false;
        }

        // ── Per-tick ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Build this agent's observation for the current tick.
        /// Called by <see cref="UnitySimulationManager"/> before issuing the tick RPC.
        /// </summary>
        public AgentObservationData BuildObservation()
        {
            return new AgentObservationData(
                agentId,
                _collector?.Collect() ?? new Dictionary<string, object>());
        }

        /// <summary>
        /// Apply a decision returned from the backend.
        /// Called by <see cref="UnitySimulationManager"/> after the tick RPC returns.
        /// </summary>
        public void ApplyDecision(AgentDecisionResult decision)
        {
            LastDecision = decision;
            OnDecisionReceived?.Invoke(decision);

            if (!decision.IsSuccess)
            {
                Debug.LogWarning($"[Biomata] Agent '{agentId}' step error: {decision.Error}");
                return;
            }

            OnActionStarted?.Invoke(decision.Action);
            StartCoroutine(ExecuteDecisionCoroutine(decision));
        }

        private IEnumerator ExecuteDecisionCoroutine(AgentDecisionResult decision)
        {
            if (_executor != null)
                yield return _executor.ExecuteCoroutine(decision, this);
            OnActionCompleted?.Invoke(decision.Action);
        }

        // ── Gizmos ────────────────────────────────────────────────────────────────

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = IsRegistered ? Color.green : new Color(1f, 0.85f, 0f);
            Gizmos.DrawWireSphere(transform.position, 0.6f);

#if UNITY_EDITOR
            if (LastDecision != null && !string.IsNullOrEmpty(LastDecision.Action))
            {
                UnityEditor.Handles.Label(
                    transform.position + Vector3.up * 2.2f,
                    $"{agentName}\n[{LastDecision.Action}]");
            }
#endif
        }
    }
}
