using System.Collections.Generic;
using UnityEngine;

namespace Biomata.Integration.Observations
{
    /// <summary>
    /// Uses <see cref="Physics.OverlapSphere"/> to find nearby agents and writes their
    /// IDs to <c>"nearby_agents"</c> in the observation. Duplicates are removed (a
    /// single agent may own multiple colliders).
    /// </summary>
    [AddComponentMenu("Biomata/Observations/Nearby Actors")]
    public class NearbyActorsProvider : ObservationProviderBase
    {
        [SerializeField] private float radius = 10f;
        [SerializeField] private LayerMask actorLayer;

        private void Reset() => actorLayer = ~0;

        public override void Populate(Dictionary<string, object> observation)
        {
            var hits   = Physics.OverlapSphere(transform.position, radius, actorLayer);
            var result = new List<string>();
            var seen   = new HashSet<int>();

            foreach (var col in hits)
            {
                var bridge = col.GetComponentInParent<UnityAgentBridge>();
                if (bridge == null || bridge.transform == transform) continue;

                var instanceId = bridge.gameObject.GetInstanceID();
                if (seen.Add(instanceId))
                    result.Add(bridge.AgentId);
            }

            observation["nearby_agents"] = result;
        }
    }
}
