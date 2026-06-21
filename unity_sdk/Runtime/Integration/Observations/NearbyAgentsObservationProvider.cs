using System.Collections.Generic;
using UnityEngine;

namespace Biomata.Integration.Observations
{
    /// <summary>
    /// Reports nearby registered agents by reading from
    /// <see cref="UnitySimulationManager.RegisteredBridges"/> — no physics colliders required.
    ///
    /// Complements the physics-based <see cref="NearbyActorsProvider"/>:
    ///   • Use <see cref="NearbyActorsProvider"/> when agents have colliders and you need
    ///     per-frame physics-accurate results (e.g. crowds with tight grouping).
    ///   • Use this provider when colliders are absent, when you want registry-only
    ///     discovery, or when you need the richer output keys below.
    ///
    /// Observation keys written:
    /// <list type="bullet">
    ///   <item><c>nearby_agents</c>       — <c>List&lt;string&gt;</c> of agent IDs, nearest-first, capped at <see cref="maxAgents"/>.</item>
    ///   <item><c>nearby_agent_count</c>  — <c>int</c> count of agents within radius.</item>
    ///   <item><c>nearest_agent_id</c>    — <c>string</c> ID of the closest agent (omitted when none).</item>
    ///   <item><c>nearest_agent_distance</c> — <c>double</c> world-space distance to the closest agent (only when <see cref="includeDistances"/> is true).</item>
    /// </list>
    /// </summary>
    [AddComponentMenu("Biomata/Observations/Nearby Agents (Registry)")]
    public class NearbyAgentsObservationProvider : ObservationProviderBase
    {
        [Tooltip("Maximum world-space radius to include agents.")]
        [Min(0f)]
        [SerializeField] private float radius = 10f;

        [Tooltip("Maximum number of agent IDs to include in the nearby_agents list.")]
        [Min(1)]
        [SerializeField] private int maxAgents = 6;

        [Tooltip(
            "When true, also writes nearest_agent_distance to the observation. " +
            "Adds a Sqrt per tick — disable if distance is unused by the brain.")]
        [SerializeField] private bool includeDistances = false;

        // Reused per-tick to avoid allocations.
        private readonly List<(float sqrDist, string agentId, string agentName)> _candidates = new();

        public override void Populate(Dictionary<string, object> observation)
        {
            var manager = UnitySimulationManager.Instance;
            if (manager == null) return;

            _candidates.Clear();
            float sqrRadius = radius * radius;
            var   pos       = transform.position;

            foreach (var bridge in manager.RegisteredBridges)
            {
                if (bridge == null || bridge.transform == transform) continue;
                float sqrDist = (bridge.transform.position - pos).sqrMagnitude;
                if (sqrDist <= sqrRadius)
                    _candidates.Add((sqrDist, bridge.AgentId, bridge.AgentName));
            }

            // Sort nearest-first so the brain sees the most relevant agents at the front.
            _candidates.Sort(static (a, b) => a.sqrDist.CompareTo(b.sqrDist));

            int count   = Mathf.Min(_candidates.Count, maxAgents);
            var entries = new List<Dictionary<string, object>>(count);
            for (int i = 0; i < count; i++)
            {
                var entry = new Dictionary<string, object>
                {
                    [ObservationKeys.EntryId]   = _candidates[i].agentId,
                    [ObservationKeys.EntryName] = _candidates[i].agentName,
                };
                if (includeDistances && i == 0)
                    entry[ObservationKeys.EntryDistance] = (double)Mathf.Sqrt(_candidates[i].sqrDist);
                entries.Add(entry);
            }

            observation[ObservationKeys.NearbyAgents]     = entries;
            observation[ObservationKeys.NearbyAgentCount] = count;

            if (count > 0)
            {
                observation[ObservationKeys.NearestAgentId] = _candidates[0].agentId;
                if (includeDistances)
                    observation[ObservationKeys.NearestAgentDistance] = (double)Mathf.Sqrt(_candidates[0].sqrDist);
            }
        }

        public override IReadOnlyCollection<string> DeclaredObservationKeys => new[]
        {
            ObservationKeys.NearbyAgents, ObservationKeys.NearbyAgentCount,
            ObservationKeys.NearestAgentId, ObservationKeys.NearestAgentDistance,
        };

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.25f);
            Gizmos.DrawWireSphere(transform.position, radius);
        }
    }
}
