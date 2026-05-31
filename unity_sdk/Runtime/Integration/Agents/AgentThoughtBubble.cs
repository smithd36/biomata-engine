using Biomata.SDK.Models;
using TMPro;
using UnityEngine;

namespace Biomata.Integration
{
    /// <summary>
    /// Displays a running text summary of the agent's latest decision in a World-Space UI bubble.
    ///
    /// Attach to the root of a "ThoughtBubble" child created by
    /// <c>BiomataThoughtBubbleTools</c> (Biomata → Tools → Add Thought Bubbles To Agents).
    ///
    /// The component auto-wires to the nearest parent <see cref="BiomataAgent"/> in OnEnable
    /// and subscribes to its <see cref="BiomataAgent.OnDecisionReceived"/> event. Each tick,
    /// <see cref="OutcomeText"/> is shown if non-empty, otherwise the raw action name.
    ///
    /// Call <see cref="SetText"/> directly to override the display from your own code.
    /// </summary>
    [AddComponentMenu("Biomata/UI/Agent Thought Bubble")]
    public class AgentThoughtBubble : MonoBehaviour
    {
        [SerializeField] private TMP_Text label;

        private BiomataAgent _agent;

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>
        /// Set the displayed text directly.
        /// No-op if the <c>TMP_Text</c> reference has not been wired.
        /// </summary>
        public void SetText(string text)
        {
            if (label != null) label.text = text;
        }

        /// <summary>
        /// Wire the <c>TMP_Text</c> label reference from an editor tool or procedural setup.
        /// Equivalent to assigning the field in the Inspector.
        /// </summary>
        public void WireLabel(TMP_Text tmp) => label = tmp;

        // ── Lifecycle ─────────────────────────────────────────────────────────────

        private void OnEnable()
        {
            _agent = GetComponentInParent<BiomataAgent>(includeInactive: true);
            if (_agent != null)
                _agent.OnDecisionReceived += HandleDecision;
        }

        private void OnDisable()
        {
            if (_agent != null)
                _agent.OnDecisionReceived -= HandleDecision;
        }

        // ── Private ───────────────────────────────────────────────────────────────

        private void HandleDecision(AgentDecisionResult decision)
        {
            string text;
            if (!string.IsNullOrEmpty(decision.OutcomeText))
                text = decision.OutcomeText;
            else if (!string.IsNullOrEmpty(decision.Action))
                text = decision.Action;
            else
                text = "Idle";

            SetText(text);
        }
    }
}
