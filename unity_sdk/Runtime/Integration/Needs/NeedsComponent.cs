using System;
using System.Collections.Generic;
using UnityEngine;

namespace Biomata.Integration.Needs
{
    /// <summary>
    /// One data-defined drive/stat on an agent — hunger, energy, morale, suspicion, supply, etc.
    /// A clamped float that decays over time and reports whether it has crossed a threshold.
    ///
    /// Author needs as data (Inspector list on <see cref="NeedsComponent"/>) instead of writing a
    /// new MonoBehaviour per drive. <see cref="NeedsObservationProvider"/> publishes them to the brain;
    /// action handlers mutate them via <see cref="NeedsComponent.Modify"/>.
    /// </summary>
    [Serializable]
    public class Need
    {
        [Tooltip("Observation key, e.g. 'hunger'. Must be unique within a NeedsComponent.")]
        public string key;

        [Tooltip("Current value.")]
        public float value;

        public float min = 0f;
        public float max = 100f;

        [Tooltip("Amount subtracted from value per second. Use a negative value for a need that grows over time (e.g. hunger).")]
        public float decayPerSecond;

        [Tooltip("Value at which the need is considered 'critical' (see Act When Above).")]
        public float threshold;

        [Tooltip("Critical when value >= threshold (e.g. hunger). When off, critical when value <= threshold (e.g. energy).")]
        public bool actWhenAbove;

        /// <summary>True once the need has crossed its threshold in the configured direction.</summary>
        public bool IsCritical => actWhenAbove ? value >= threshold : value <= threshold;
    }

    /// <summary>
    /// Holds an agent's <see cref="Need"/> list and decays each one every frame.
    /// Replaces per-drive components (HungerComponent, EnergyComponent, …) with a single
    /// data-driven component. Pair with <see cref="NeedsObservationProvider"/> to expose them.
    /// </summary>
    [AddComponentMenu("Biomata/Needs")]
    public class NeedsComponent : MonoBehaviour
    {
        [SerializeField] private List<Need> needs = new List<Need>();

        /// <summary>The configured needs. Read-only enumeration for providers.</summary>
        public IReadOnlyList<Need> Needs => needs;

        private void Update()
        {
            float dt = Time.deltaTime;
            for (int i = 0; i < needs.Count; i++)
            {
                var n = needs[i];
                n.value = Mathf.Clamp(n.value - n.decayPerSecond * dt, n.min, n.max);
            }
        }

        /// <summary>Find a need by key, or null if absent.</summary>
        public Need Get(string key)
        {
            for (int i = 0; i < needs.Count; i++)
                if (needs[i].key == key) return needs[i];
            return null;
        }

        /// <summary>
        /// Add <paramref name="delta"/> to the named need (clamped). Returns false if the key is unknown
        /// so action handlers can detect a misconfigured need rather than silently no-op.
        /// </summary>
        public bool Modify(string key, float delta)
        {
            var n = Get(key);
            if (n == null) return false;
            n.value = Mathf.Clamp(n.value + delta, n.min, n.max);
            return true;
        }
    }
}
