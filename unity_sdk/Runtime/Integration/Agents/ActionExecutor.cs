using System.Collections;
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
    /// </summary>
    [AddComponentMenu("Biomata/Action Executor")]
    public class ActionExecutor : MonoBehaviour
    {
        private ActionHandlerBase[] _handlers;

        private void Awake() => _handlers = GetComponents<ActionHandlerBase>();

        /// <summary>
        /// Coroutine that runs until the matching handler's execution completes.
        /// Logs a warning and yields immediately when no handler matches.
        /// Driven by <see cref="UnityAgentBridge"/>.
        /// </summary>
        public IEnumerator ExecuteCoroutine(AgentDecisionResult decision, UnityAgentBridge bridge)
        {
            var action = decision.Action ?? string.Empty;

            foreach (var handler in _handlers)
            {
                if (handler == null || !handler.isActiveAndEnabled) continue;
                if (!handler.CanHandle(action)) continue;

                yield return StartCoroutine(handler.ExecuteCoroutine(decision, bridge));
                yield break;
            }

            Debug.Log(
                $"[Biomata] No handler for action '{action}' on agent '{bridge.AgentId}'. " +
                "Add a matching ActionHandlerBase component to the agent GameObject.");
        }

        /// <summary>Re-scan for handler components after runtime add/remove.</summary>
        public void RefreshHandlers() => _handlers = GetComponents<ActionHandlerBase>();
    }
}
