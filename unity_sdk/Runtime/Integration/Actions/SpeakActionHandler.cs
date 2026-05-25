using System;
using System.Collections;
using System.Collections.Generic;
using Biomata.SDK.Models;
using UnityEngine;

namespace Biomata.Integration.Actions
{
    /// <summary>
    /// Handles speech actions. Extracts the speech text from the decision and fires
    /// <see cref="OnSpeak"/> so the game can drive a dialogue UI, audio source, or
    /// subtitle system.
    ///
    /// <see cref="EventVisualizer"/> renders on-screen speech bubbles automatically
    /// when this component is present on the same agent.
    ///
    /// Text is extracted in order: Parameters["text"] → Parameters["message"] → OutcomeText.
    /// </summary>
    [AddComponentMenu("Biomata/Actions/Speak")]
    public class SpeakActionHandler : ActionHandlerBase
    {
        [SerializeField] private float speechDuration  = 4f;
        [SerializeField] private bool  logToConsole    = false;

        /// <summary>Configure speech handler parameters at runtime (call immediately after AddComponent).</summary>
        public void Configure(bool logToConsole = false, float speechDuration = 4f)
        {
            this.logToConsole   = logToConsole;
            this.speechDuration = speechDuration;
        }

        private static readonly HashSet<string> HandledActions = new HashSet<string>
        {
            "speak", "say", "talk", "announce", "greet", "shout", "whisper", "declare",
        };

        /// <summary>
        /// Raised on the main thread when speech begins.
        /// Args: (agentId, speechText).
        /// </summary>
        public event Action<string, string> OnSpeak;

        /// <summary>The current speech text, or <c>null</c> when the agent is silent.</summary>
        public string CurrentSpeech { get; private set; }

        /// <summary>True while the speech coroutine is running.</summary>
        public bool IsSpeaking { get; private set; }

        public override bool CanHandle(string action) =>
            HandledActions.Contains(action?.ToLowerInvariant() ?? string.Empty);

        public override IEnumerator ExecuteCoroutine(AgentDecisionResult decision, UnityAgentBridge bridge)
        {
            var text = ExtractText(decision);
            if (string.IsNullOrEmpty(text)) yield break;

            CurrentSpeech = text;
            IsSpeaking    = true;

            if (logToConsole)
                Debug.Log($"[{bridge.AgentName}]: \"{text}\"");

            OnSpeak?.Invoke(bridge.AgentId, text);

            yield return new WaitForSeconds(speechDuration);

            IsSpeaking    = false;
            CurrentSpeech = null;
        }

        private static string ExtractText(AgentDecisionResult decision)
        {
            if (decision.Parameters.TryGetValue("text", out var t) && t != null)
                return t.ToString();
            if (decision.Parameters.TryGetValue("message", out var m) && m != null)
                return m.ToString();
            return string.IsNullOrEmpty(decision.OutcomeText) ? null : decision.OutcomeText;
        }
    }
}
