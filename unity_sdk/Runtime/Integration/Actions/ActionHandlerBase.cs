using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Biomata.SDK.Models;
using UnityEngine;

namespace Biomata.Integration.Actions
{
    /// <summary>
    /// Base class for action handlers.
    ///
    /// Attach any number of concrete handlers to the same GameObject as
    /// <see cref="ActionExecutor"/>. On each tick the executor finds the first handler
    /// (in component order) whose <see cref="CanHandle"/> returns <c>true</c> and
    /// runs its <see cref="ExecuteCoroutine"/>.
    ///
    /// ── Implementing a custom handler ────────────────────────────────────────
    ///
    /// 1. Subclass and add <c>[AddComponentMenu("Biomata/Actions/YourName")]</c>.
    ///
    /// 2. Declare a <c>static readonly HashSet&lt;string&gt;</c> of the lowercase
    ///    action names your handler covers.  Override <see cref="CanHandle"/> to
    ///    return <c>true</c> when <paramref name="action"/> is in that set.
    ///
    /// 3. Override <see cref="ExecuteCoroutine"/>. Use <c>yield return null</c> to
    ///    advance one frame, <c>yield return new WaitForSeconds(t)</c> to delay,
    ///    or <c>yield return StartCoroutine(other)</c> to chain sub-coroutines.
    ///    The coroutine runs until it completes — the next action will not start
    ///    until the previous <see cref="ExecuteCoroutine"/> has finished.
    ///
    /// 4. Read parameters from <c>decision.Parameters</c> and engine commands
    ///    from <c>decision.EngineCommands</c> — both are
    ///    <c>Dictionary&lt;string, object&gt;</c> with JSON-decoded values.
    ///
    /// 5. Fire Unity events (audio, animation triggers, UI) from inside
    ///    <see cref="ExecuteCoroutine"/>; no further coordination is needed.
    ///
    /// ── Handler ordering ─────────────────────────────────────────────────────
    ///
    /// Handlers are evaluated in component order.  A handler earlier in the list
    /// shadows later ones for the same action strings.  Reorder components in the
    /// Inspector to change priority, or write non-overlapping <see cref="CanHandle"/>
    /// sets.
    ///
    /// See <see cref="MoveActionHandler"/>, <see cref="SpeakActionHandler"/>, and
    /// <see cref="InteractActionHandler"/> for reference implementations.
    /// </summary>
    public abstract class ActionHandlerBase : MonoBehaviour
    {
        /// <summary>
        /// The action names this handler covers. Used by the manifest validator
        /// (<see cref="Biomata.Editor.ActionManifestValidator"/>) and
        /// <see cref="ActionManifestLoader.ValidateCoverage"/> to check coverage
        /// without needing to instantiate the component.
        ///
        /// The default implementation reflects on a <c>static HandledActions</c> field
        /// (a <c>HashSet&lt;string&gt;</c>), which is the naming convention used by all
        /// built-in handlers (Move, Speak, Interact, Idle). Override this property in
        /// custom handlers to declare names explicitly without relying on the convention.
        /// </summary>
        public virtual IReadOnlyCollection<string> DeclaredActionNames
        {
            get
            {
                var field = GetType().GetField(
                    "HandledActions",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (field?.GetValue(null) is IEnumerable<string> names)
                    return new List<string>(names);
                return System.Array.Empty<string>();
            }
        }

        /// <summary>
        /// Return <c>true</c> if this handler can execute <paramref name="action"/>.
        /// Called by <see cref="ActionExecutor"/> in component order each tick.
        /// </summary>
        public abstract bool CanHandle(string action);

        /// <summary>
        /// Execute the action and yield until it is complete.
        /// Driven by <see cref="ActionExecutor"/> as a Unity coroutine.
        /// </summary>
        public abstract IEnumerator ExecuteCoroutine(AgentDecisionResult decision, UnityAgentBridge bridge);
    }
}
