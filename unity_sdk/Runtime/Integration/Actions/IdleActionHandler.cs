using System.Collections;
using System.Collections.Generic;
using Biomata.SDK.Models;
using UnityEngine;

namespace Biomata.Integration.Actions
{
    /// <summary>
    /// Handles idle/wait actions — the agent does nothing for one tick duration.
    ///
    /// Add this component alongside MoveActionHandler, SpeakActionHandler, and
    /// InteractActionHandler when using build_hosted_registry on the backend so
    /// all four registered action names have a matching Unity handler.
    /// </summary>
    [AddComponentMenu("Biomata/Actions/Idle")]
    public class IdleActionHandler : ActionHandlerBase
    {
        [SerializeField] private float idleDuration = 1f;

        private static readonly HashSet<string> HandledActions = new HashSet<string>
        {
            "idle", "wait", "rest", "pause",
        };

        public override bool CanHandle(string action) =>
            HandledActions.Contains(action?.ToLowerInvariant() ?? string.Empty);

        public override IEnumerator ExecuteCoroutine(AgentDecisionResult decision, UnityAgentBridge bridge)
        {
            yield return new WaitForSeconds(idleDuration);
        }
    }
}
