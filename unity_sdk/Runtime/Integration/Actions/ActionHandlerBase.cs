using System.Collections;
using Biomata.SDK.Models;
using UnityEngine;

namespace Biomata.Integration.Actions
{
    /// <summary>
    /// Base class for action handlers. Add multiple concrete handlers to the same
    /// GameObject as <see cref="ActionExecutor"/>. The executor calls the first handler
    /// whose <see cref="CanHandle"/> returns <c>true</c>.
    ///
    /// Subclass to add custom action types (NavMesh movement, animation state machines,
    /// audio cues, particle effects, etc.) without modifying the SDK.
    /// </summary>
    public abstract class ActionHandlerBase : MonoBehaviour
    {
        /// <summary>
        /// Return <c>true</c> if this handler is capable of executing
        /// <paramref name="action"/>. Case-sensitivity is up to the subclass.
        /// </summary>
        public abstract bool CanHandle(string action);

        /// <summary>
        /// Execute the action. Yield until the action animation/effect is complete.
        /// The coroutine is driven by <see cref="ActionExecutor"/>.
        /// </summary>
        public abstract IEnumerator ExecuteCoroutine(AgentDecisionResult decision, UnityAgentBridge bridge);
    }
}
