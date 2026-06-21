using System.Collections.Generic;
using Biomata.Integration.Observations;
using UnityEngine;

namespace Biomata.Integration.Needs
{
    /// <summary>
    /// Publishes every <see cref="Need"/> on the sibling <see cref="NeedsComponent"/> to the agent
    /// observation each tick. For a need with key <c>hunger</c> it writes:
    /// <list type="bullet">
    ///   <item><c>hunger</c> — current value (double).</item>
    ///   <item><c>hunger_max</c> — max value (double).</item>
    ///   <item><c>hunger_threshold</c> — threshold value (double).</item>
    ///   <item><c>hunger_critical</c> — whether the need has crossed its threshold (bool).</item>
    /// </list>
    /// This replaces the per-drive observation providers (HungerObservationProvider, …) with one
    /// component that follows the need list.
    /// </summary>
    [AddComponentMenu("Biomata/Observations/Needs")]
    [RequireComponent(typeof(NeedsComponent))]
    public class NeedsObservationProvider : ObservationProviderBase
    {
        private NeedsComponent _needs;

        private void Awake() => _needs = GetComponent<NeedsComponent>();

        public override void Populate(Dictionary<string, object> observation)
        {
            var needs = _needs.Needs;
            for (int i = 0; i < needs.Count; i++)
            {
                var n = needs[i];
                if (string.IsNullOrEmpty(n.key)) continue;
                observation[n.key]                                = (double)n.value;
                observation[n.key + ObservationKeys.MaxSuffix]       = (double)n.max;
                observation[n.key + ObservationKeys.ThresholdSuffix] = (double)n.threshold;
                observation[n.key + ObservationKeys.CriticalSuffix]  = n.IsCritical;
            }
        }
    }
}
