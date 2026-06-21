using System;
using Biomata.Integration.Needs;
using UnityEngine;

namespace Biomata.Samples.Survival
{
    /// <summary>
    /// A place an agent walks to, spends time at, and gets an effect from — a bed, a fire,
    /// a workbench, a berry bush. The generalised form of the demo's <c>POIStation</c>.
    ///
    /// Pair with <see cref="UseStationActionHandler"/> on the agent: the handler walks the
    /// agent to <see cref="ApproachPosition"/>, waits <see cref="duration"/>, then applies
    /// <see cref="effects"/> to the agent's <see cref="NeedsComponent"/>.
    ///
    /// Tag the GameObject with <see cref="UseStationActionHandler"/>'s station tag
    /// (default <c>"BiomataPOI"</c>) so the handler can find it by name.
    /// </summary>
    [AddComponentMenu("Biomata/Samples/Station")]
    public class Station : MonoBehaviour
    {
        /// <summary>One need change applied when an agent finishes using the station.</summary>
        [Serializable]
        public struct NeedEffect
        {
            [Tooltip("Need key on the agent's NeedsComponent, e.g. 'hunger'.")]
            public string needKey;

            [Tooltip("Amount added to that need (negative to reduce, e.g. -40 to sate hunger).")]
            public float delta;
        }

        [Tooltip("Lookup name the brain refers to. Defaults to the GameObject name.")]
        [SerializeField] private string stationName = "";

        [Tooltip("Seconds the agent spends using the station before effects apply.")]
        [Min(0f)]
        [SerializeField] private float duration = 2f;

        [Tooltip("Need changes applied to the agent on completion.")]
        [SerializeField] private NeedEffect[] effects = Array.Empty<NeedEffect>();

        [Tooltip("Optional point the agent walks to. Defaults to this transform's position.")]
        [SerializeField] private Transform approachPoint;

        [Tooltip("When false the station is depleted/closed and the handler refuses to use it.")]
        [SerializeField] private bool isActive = true;

        [Tooltip("Deactivate after a single use (consumable, e.g. a picked berry bush).")]
        [SerializeField] private bool deactivateOnUse = false;

        /// <summary>Whether the station can currently be used.</summary>
        public bool IsActive => isActive;

        /// <summary>Seconds an agent must spend before effects apply.</summary>
        public float Duration => duration;

        /// <summary>Lookup key (lower-cased name) the handler matches against.</summary>
        public string Key => (string.IsNullOrEmpty(stationName) ? gameObject.name : stationName);

        /// <summary>Where the agent should stand to use the station.</summary>
        public Vector3 ApproachPosition => approachPoint != null ? approachPoint.position : transform.position;

        /// <summary>
        /// Apply this station's effects to <paramref name="needs"/>. Logs a warning for any
        /// effect whose need key is not present so misconfiguration surfaces instead of
        /// silently doing nothing. Deactivates the station afterward if configured.
        /// </summary>
        public void ApplyEffects(NeedsComponent needs)
        {
            foreach (var e in effects)
            {
                if (string.IsNullOrEmpty(e.needKey)) continue;
                if (!needs.Modify(e.needKey, e.delta))
                    Debug.LogWarning(
                        $"[Station] '{Key}': agent has no need '{e.needKey}' — effect ignored.", this);
            }

            if (deactivateOnUse) isActive = false;
        }
    }
}
