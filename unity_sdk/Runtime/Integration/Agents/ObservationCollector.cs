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
        private ObservationProviderBase[] _providers;
        private readonly Dictionary<string, object> _manual = new Dictionary<string, object>();
        private readonly List<Dictionary<string, object>> _pendingMessages = new();

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
        /// Queued messages are written as <c>incoming_messages</c> on the next
        /// <see cref="Collect"/> call and then cleared so they appear exactly once.
        /// Called by <see cref="Actions.SpeakActionHandler"/> when this agent is the target.
        /// </summary>
        public void DeliverMessage(string fromId, string fromName, string text)
        {
            _pendingMessages.Add(new Dictionary<string, object>
            {
                ["from"]      = fromId,
                ["from_name"] = fromName,
                ["text"]      = text,
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
                obs["incoming_messages"] = new List<Dictionary<string, object>>(_pendingMessages);
                _pendingMessages.Clear();
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
