using System;
using System.Collections;
using System.Collections.Generic;
using Biomata.SDK.Models;
using UnityEngine;

namespace Biomata.Integration.Actions
{
    /// <summary>
    /// Handles pick-up, use, and give actions by firing <see cref="OnInteract"/> and
    /// waiting for <see cref="interactionDuration"/> seconds.
    ///
    /// Hook <see cref="OnInteract"/> to drive an Animator trigger, play a sound,
    /// or emit a game event — the handler itself is animation-system agnostic.
    /// </summary>
    [AddComponentMenu("Biomata/Actions/Interact")]
    public class InteractActionHandler : ActionHandlerBase
    {
        [SerializeField] private float interactionDuration = 0.8f;

        private static readonly HashSet<string> HandledActions = new HashSet<string>
        {
            "interact", "use", "pickup", "pick_up", "give", "drop", "examine", "open", "close",
        };

        /// <summary>
        /// Raised on the main thread the moment an interaction begins.
        /// The parameter is the full decision so the subscriber can read parameters
        /// (e.g. <c>decision.Parameters["target_id"]</c>).
        /// </summary>
        public event Action<AgentDecisionResult> OnInteract;

        public override bool CanHandle(string action) =>
            HandledActions.Contains(action?.ToLowerInvariant() ?? string.Empty);

        public override IEnumerator ExecuteCoroutine(AgentDecisionResult decision, UnityAgentBridge bridge)
        {
            OnInteract?.Invoke(decision);
            yield return new WaitForSeconds(interactionDuration);
        }
    }
}
