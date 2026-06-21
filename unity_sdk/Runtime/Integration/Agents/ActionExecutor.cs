using System.Collections;
using System.Collections.Generic;
using System.Text;
using Biomata.Integration.Actions;
using Biomata.SDK.Models;
using UnityEngine;

namespace Biomata.Integration
{
    /// <summary>
    /// Dispatches a backend decision to the appropriate <see cref="ActionHandlerBase"/>
    /// on the same GameObject.
    ///
    /// Handlers are checked in component order; the first one whose
    /// <see cref="ActionHandlerBase.CanHandle"/> returns <c>true</c> is executed.
    /// Add <see cref="MoveActionHandler"/>, <see cref="InteractActionHandler"/>,
    /// <see cref="SpeakActionHandler"/>, or custom subclasses to extend coverage.
    ///
    /// When no handler matches, a structured warning is logged:
    ///   • Action name and description (from BiomataActions.json, if available)
    ///   • Agent ID and display name
    ///   • Names of handlers that ARE present on the GameObject
    ///   • A specific fix instruction
    /// </summary>
    [AddComponentMenu("Biomata/Action Executor")]
    public class ActionExecutor : MonoBehaviour
    {
        private ActionHandlerBase[] _handlers;

        /// <summary>The handler currently executing an action, or <c>null</c> when idle.</summary>
        private ActionHandlerBase _running;

        private void Awake() => _handlers = GetComponents<ActionHandlerBase>();

        /// <summary>
        /// Coroutine that runs until the matching handler's execution completes.
        /// Logs a structured warning and yields immediately when no handler matches.
        /// Driven by <see cref="UnityAgentBridge"/>.
        ///
        /// The handler runs inline (<c>yield return handler.ExecuteCoroutine(...)</c>,
        /// not a detached <c>StartCoroutine</c>) so the whole chain is a single
        /// coroutine the bridge can stop atomically when a new decision interrupts it.
        /// </summary>
        public IEnumerator ExecuteCoroutine(AgentDecisionResult decision, UnityAgentBridge bridge)
        {
            var action = decision.Action ?? string.Empty;

            foreach (var handler in _handlers)
            {
                if (handler == null || !handler.isActiveAndEnabled) continue;
                if (!handler.CanHandle(action)) continue;

                _running = handler;
                try
                {
                    yield return handler.ExecuteCoroutine(decision, bridge);
                }
                finally
                {
                    // Clear only if still ours — a re-entrant start may have replaced it.
                    if (_running == handler) _running = null;
                }
                yield break;
            }

            LogMissingHandler(action, bridge);
        }

        /// <summary>
        /// If an action is running and its handler can re-target (<see cref="ActionHandlerBase.CanRetarget"/>)
        /// and also covers the new decision's action, update it in place and return
        /// <c>true</c>. Returns <c>false</c> when the running action must instead be
        /// cancelled and replaced (call <see cref="CancelRunning"/> then start anew).
        /// </summary>
        public bool TryRetarget(AgentDecisionResult decision, UnityAgentBridge bridge)
        {
            if (_running == null || !_running.isActiveAndEnabled) return false;
            var action = decision.Action ?? string.Empty;
            if (!_running.CanRetarget || !_running.CanHandle(action)) return false;

            _running.Retarget(decision, bridge);
            return true;
        }

        /// <summary>
        /// Notify the running handler that its action is being cancelled so it can halt
        /// side effects (see <see cref="ActionHandlerBase.OnInterrupted"/>). Call after
        /// the bridge stops the action coroutine.
        /// </summary>
        public void CancelRunning(UnityAgentBridge bridge)
        {
            if (_running == null) return;
            _running.OnInterrupted(bridge);
            _running = null;
        }

        /// <summary>Re-scan for handler components after runtime add/remove.</summary>
        public void RefreshHandlers() => _handlers = GetComponents<ActionHandlerBase>();

        // ── Diagnostics ───────────────────────────────────────────────────────────

        private void LogMissingHandler(string action, UnityAgentBridge bridge)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[Biomata] No handler for action '{action}'");

            // Agent identity
            if (bridge != null)
                sb.AppendLine($"  Agent:       {bridge.AgentId}  (\"{bridge.AgentName}\")");

            // Description from manifest (if loaded)
            var manifest = ActionManifestLoader.Load();
            if (manifest?.actions != null)
            {
                var entry = System.Array.Find(manifest.actions, a => a.name == action);
                if (entry != null)
                    sb.AppendLine($"  Description: {entry.description}");
            }

            // Handlers present on this GameObject
            var presentNames = new List<string>();
            if (_handlers != null)
                foreach (var h in _handlers)
                    if (h != null && h.isActiveAndEnabled)
                        presentNames.Add(h.GetType().Name);

            sb.AppendLine(presentNames.Count > 0
                ? $"  Handlers:    {string.Join(", ", presentNames)}"
                : "  Handlers:    (none on this GameObject)");

            // Fix instruction
            sb.Append(
                $"  Fix:         Add a component that extends ActionHandlerBase " +
                $"and returns true for CanHandle(\"{action}\").");

            Debug.LogWarning(sb.ToString().TrimEnd(), bridge);
        }
    }
}
