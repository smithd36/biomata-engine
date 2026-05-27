using System;
using System.Collections.Generic;
using UnityEngine;

namespace Biomata.Integration.Observations
{
    /// <summary>
    /// Reports nearby Points of Interest (POIs) to the backend brain each tick.
    ///
    /// POIs are any GameObjects with a matching Unity tag. The provider caches the
    /// tag scan at startup; call <see cref="Refresh"/> if POIs appear or disappear
    /// at runtime.
    ///
    /// Setup:
    /// <list type="number">
    ///   <item>In <b>Edit → Project Settings → Tags &amp; Layers</b>, add a tag
    ///         (default: <c>"BiomataPOI"</c>).</item>
    ///   <item>Assign that tag to every GameObject you want the agent to know about.</item>
    ///   <item>Set the <b>POI Tag</b> field in the Inspector to match.</item>
    /// </list>
    ///
    /// Observation keys written (under the configurable <see cref="observationKey"/> prefix):
    /// <list type="bullet">
    ///   <item><c>{key}</c>           — <c>List&lt;Dictionary&gt;</c> of nearby POI entries, nearest-first.</item>
    ///   <item><c>{key}_count</c>     — <c>int</c> number of POIs within radius.</item>
    ///   <item><c>{key}_nearest</c>   — <c>string</c> name of the nearest POI (omitted when none).</item>
    /// </list>
    ///
    /// Each POI entry dictionary contains:
    /// <list type="bullet">
    ///   <item><c>"id"</c>       — GameObject name.</item>
    ///   <item><c>"x"</c>        — world X position.</item>
    ///   <item><c>"z"</c>        — world Z position.</item>
    ///   <item><c>"distance"</c> — world-space distance (only when <see cref="includeDistances"/> is true).</item>
    /// </list>
    /// </summary>
    [AddComponentMenu("Biomata/Observations/Points of Interest")]
    public class POIObservationProvider : ObservationProviderBase
    {
        [Tooltip(
            "Unity tag assigned to POI GameObjects. " +
            "Create the tag in Edit → Project Settings → Tags & Layers first.")]
        [SerializeField] private string poiTag = "BiomataPOI";

        [Tooltip("Maximum world-space radius to include POIs.")]
        [Min(0f)]
        [SerializeField] private float detectionRadius = 20f;

        [Tooltip("Maximum number of POI entries written to the observation.")]
        [Min(1)]
        [SerializeField] private int maxResults = 5;

        [Tooltip(
            "Base key used for observation output. " +
            "Produces '{key}', '{key}_count', and '{key}_nearest'.")]
        [SerializeField] private string observationKey = "nearby_pois";

        [Tooltip("Include a 'distance' field in each POI entry dictionary.")]
        [SerializeField] private bool includeDistances = true;

        private Transform[] _pois = Array.Empty<Transform>();

        // Reused per-tick to avoid per-call allocations.
        private readonly List<(float sqrDist, Transform poi)> _candidates = new();

        private void Awake() => Refresh();

        /// <summary>
        /// Re-scan the scene for GameObjects matching <see cref="poiTag"/>.
        /// Call when POIs are spawned or destroyed at runtime.
        /// </summary>
        public void Refresh()
        {
            if (string.IsNullOrEmpty(poiTag))
            {
                _pois = Array.Empty<Transform>();
                return;
            }

            try
            {
                var gos = GameObject.FindGameObjectsWithTag(poiTag);
                _pois = new Transform[gos.Length];
                for (int i = 0; i < gos.Length; i++)
                    _pois[i] = gos[i].transform;
            }
            catch (UnityException)
            {
                _pois = Array.Empty<Transform>();
                Debug.LogWarning(
                    $"[POIObservationProvider] Tag '{poiTag}' does not exist. " +
                    "Add it in Edit → Project Settings → Tags & Layers.", this);
            }
        }

        public override void Populate(Dictionary<string, object> observation)
        {
            _candidates.Clear();
            float sqrRadius = detectionRadius * detectionRadius;
            var   pos       = transform.position;

            foreach (var poi in _pois)
            {
                if (poi == null || poi == transform) continue;
                float sqrDist = (poi.position - pos).sqrMagnitude;
                if (sqrDist <= sqrRadius)
                    _candidates.Add((sqrDist, poi));
            }

            // Sort nearest-first so the most relevant POI appears at index 0.
            _candidates.Sort(static (a, b) => a.sqrDist.CompareTo(b.sqrDist));

            int count   = Mathf.Min(_candidates.Count, maxResults);
            var results = new List<object>(count);

            for (int i = 0; i < count; i++)
            {
                var (sqrDist, poi) = _candidates[i];
                var entry = new Dictionary<string, object>
                {
                    ["id"] = poi.name,
                    ["x"]  = (double)poi.position.x,
                    ["z"]  = (double)poi.position.z,
                };
                if (includeDistances)
                    entry["distance"] = (double)Mathf.Sqrt(sqrDist);
                results.Add(entry);
            }

            observation[observationKey]              = results;
            observation[observationKey + "_count"]   = count;
            if (count > 0)
                observation[observationKey + "_nearest"] = _candidates[0].poi.name;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.75f, 0.1f, 0.25f);
            Gizmos.DrawWireSphere(transform.position, detectionRadius);
        }
    }
}
