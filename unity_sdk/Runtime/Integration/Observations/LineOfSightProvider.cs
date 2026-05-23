using System.Collections.Generic;
using UnityEngine;

namespace Biomata.Integration.Observations
{
    /// <summary>
    /// Raycasts from this agent's eye position toward every registered agent and writes
    /// the IDs of those with an unobstructed line of sight to <c>"visible_agents"</c>.
    ///
    /// Reads from <see cref="UnitySimulationManager.RegisteredBridges"/> rather than
    /// <see cref="Object.FindObjectsOfType{T}"/> to avoid per-frame scene traversal.
    /// </summary>
    [AddComponentMenu("Biomata/Observations/Line Of Sight")]
    public class LineOfSightProvider : ObservationProviderBase
    {
        [SerializeField] private float   maxRange      = 20f;
        [SerializeField] private LayerMask blockingLayers;
        [SerializeField] private Vector3 eyeOffset     = new Vector3(0f, 1.7f, 0f);

        private void Reset() => blockingLayers = ~0;

        public override void Populate(Dictionary<string, object> observation)
        {
            var manager = UnitySimulationManager.Instance;
            if (manager == null) return;

            var origin  = transform.position + eyeOffset;
            var visible = new List<string>();
            var sqrMax  = maxRange * maxRange;

            foreach (var bridge in manager.RegisteredBridges)
            {
                if (bridge == null || bridge.transform == transform) continue;

                var target = bridge.transform.position + eyeOffset;
                var delta  = target - origin;
                if (delta.sqrMagnitude > sqrMax) continue;

                if (!Physics.Raycast(origin, delta.normalized, delta.magnitude, blockingLayers))
                    visible.Add(bridge.AgentId);
            }

            observation["visible_agents"] = visible;
        }
    }
}
