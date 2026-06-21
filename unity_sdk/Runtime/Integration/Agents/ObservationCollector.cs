using System;
using System.Collections.Generic;
using Biomata.Integration.Observations;
using UnityEngine;

namespace Biomata.Integration
{
    /// <summary>
    /// Aggregates observation data into a single dictionary each tick.
    ///
    /// Discover providers: attach any number of <see cref="ObservationProviderBase"/>
    /// components to the same GameObject. Each provider populates one or more keys.
    /// Manual overrides can be injected via <see cref="SetData"/>.
    ///
    /// Call order within a tick: providers in component order, then manual overrides
    /// (so manual values take precedence over provider values for the same key).
    /// </summary>
    [AddComponentMenu("Biomata/Observation Collector")]
    public class ObservationCollector : MonoBehaviour
    {
        [Tooltip(
            "How many ticks an incoming message stays in 'incoming_messages' so the " +
            "recipient has a fair chance to react. 1 = single-tick (old behaviour).")]
        [Min(1)]
        [SerializeField] private int messageLifetimeTicks = 4;

        private ObservationProviderBase[] _providers;
        private readonly Dictionary<string, object> _manual = new Dictionary<string, object>();

        private sealed class PendingMessage
        {
            public Dictionary<string, object> Data;
            public int TicksLeft;
        }
        private readonly List<PendingMessage> _pendingMessages = new();

        private void Awake() => _providers = GetComponents<ObservationProviderBase>();

        // ── Manual data ───────────────────────────────────────────────────────────

        /// <summary>
        /// Inject a key-value pair that overrides or supplements provider output.
        /// The value persists across ticks until cleared with <see cref="ClearData"/>.
        /// All values must be JSON-serializable.
        /// </summary>
        public void SetData(string key, object value) => _manual[key] = value;

        /// <summary>Remove a key previously set with <see cref="SetData"/>.</summary>
        public bool ClearData(string key) => _manual.Remove(key);

        /// <summary>Remove all manually injected keys.</summary>
        public void ClearAllData() => _manual.Clear();

        /// <summary>
        /// Queue an incoming speech message from another agent.
        /// The message is written as <c>incoming_messages</c> for the next
        /// <see cref="messageLifetimeTicks"/> <see cref="Collect"/> calls (then dropped),
        /// so the recipient gets several decision cycles to notice and reply instead of
        /// a single tick.
        /// Called by <see cref="Actions.SpeakActionHandler"/> when this agent is the target.
        /// </summary>
        public void DeliverMessage(string fromId, string fromName, string text)
        {
            _pendingMessages.Add(new PendingMessage
            {
                Data = new Dictionary<string, object>
                {
                    [ObservationKeys.MsgFrom]     = fromId,
                    [ObservationKeys.MsgFromName] = fromName,
                    [ObservationKeys.MsgText]     = text,
                },
                TicksLeft = Mathf.Max(1, messageLifetimeTicks),
            });
        }

        // ── Collection ────────────────────────────────────────────────────────────

        /// <summary>
        /// Build the observation dictionary for the current tick.
        /// Called by <see cref="UnityAgentBridge.BuildObservation"/> before each tick.
        /// </summary>
        public Dictionary<string, object> Collect()
        {
            var obs = new Dictionary<string, object>();

            foreach (var provider in _providers)
            {
                if (provider == null || !provider.isActiveAndEnabled) continue;
                try
                {
                    provider.Populate(obs);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        $"[Biomata] ObservationProvider {provider.GetType().Name} threw: {ex.Message}");
                }
            }

            foreach (var kv in _manual)
                obs[kv.Key] = kv.Value;

            if (_pendingMessages.Count > 0)
            {
                var msgs = new List<Dictionary<string, object>>(_pendingMessages.Count);
                // Iterate backwards so we can drop expired messages in place.
                for (int i = _pendingMessages.Count - 1; i >= 0; i--)
                {
                    msgs.Add(_pendingMessages[i].Data);
                    if (--_pendingMessages[i].TicksLeft <= 0)
                        _pendingMessages.RemoveAt(i);
                }
                obs[ObservationKeys.IncomingMessages] = msgs;
            }

            return obs;
        }

        /// <summary>
        /// Re-scan for <see cref="ObservationProviderBase"/> components. Call after
        /// adding or removing providers at runtime; not needed when the set is fixed at Start.
        /// </summary>
        public void RefreshProviders() => _providers = GetComponents<ObservationProviderBase>();
    }
}
