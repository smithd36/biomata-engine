using System;
using System.Collections.Generic;
using Biomata.Integration;
using UnityEngine;

namespace Biomata.Integration.Observations
{
    /// <summary>
    /// Reports nearby dynamic world objects to the backend brain each tick.
    ///
    /// Covers any category of tagged, stateful world object: food sources, items,
    /// resource nodes, interactables, etc. Use one instance per category, each
    /// with its own tag and observation key.
    ///
    /// Mirrors <see cref="POIObservationProvider"/> exactly — tag-based discovery,
    /// cached transform list, per-tick distance filter, nearest-first sorting —
    /// with the addition of <see cref="BiomataObjectData"/> for dynamic metadata
    /// and active/inactive filtering.
    ///
    /// ── Setup ─────────────────────────────────────────────────────────────────
    ///
    /// 1. In <b>Edit → Project Settings → Tags &amp; Layers</b>, add a tag
    ///    (e.g. <c>"BiomataFood"</c>, <c>"BiomataItem"</c>).
    /// 2. Tag each world object GameObject with that tag.
    /// 3. Optionally attach <see cref="BiomataObjectData"/> to supply type and
    ///    static/dynamic metadata. Objects without it are still reported (id, x, z).
    /// 4. Add this component to your NPC prefab alongside
    ///    <see cref="ObservationCollector"/>. Set <b>Object Tag</b> and
    ///    <b>Observation Key</b> to match the category.
    /// 5. Call <see cref="Refresh"/> when objects of this category spawn or despawn
    ///    at runtime (the transform cache is not updated automatically).
    ///
    /// ── Observation keys written ───────────────────────────────────────────────
    ///
    /// <list type="bullet">
    ///   <item><c>{key}</c>         — <c>List&lt;Dictionary&gt;</c> of nearby entries, nearest-first.</item>
    ///   <item><c>{key}_count</c>   — <c>int</c> number of active objects within radius.</item>
    ///   <item><c>{key}_nearest</c> — <c>string</c> name of the nearest active object (omitted when none).</item>
    /// </list>
    ///
    /// Each entry dictionary contains:
    /// <list type="bullet">
    ///   <item><c>"id"</c>       — GameObject name.</item>
    ///   <item><c>"x"</c>        — world X position.</item>
    ///   <item><c>"z"</c>        — world Z position.</item>
    ///   <item><c>"distance"</c> — world-space distance (when <see cref="includeDistances"/> is true).</item>
    ///   <item><c>"type"</c>     — <see cref="BiomataObjectData.ObjectType"/> (when component is present).</item>
    ///   <item>+ any keys from <see cref="BiomataObjectData.GetObservationProperties"/>.</item>
    /// </list>
    ///
    /// ── Example: food sources ─────────────────────────────────────────────────
    ///
    ///   Tag:             "BiomataFood"
    ///   Observation Key: "nearby_food"
    ///   Component:       FoodObjectData : BiomataObjectData  (overrides GetObservationProperties
    ///                    to include "amount" and sets IsActive = false when depleted)
    ///
    /// Python observation per tick:
    /// <code>
    ///   nearby_food:         [{ id:"Apple_01", type:"food", x:3.5, z:1.2, distance:4.1, amount:5.0 }]
    ///   nearby_food_count:   1
    ///   nearby_food_nearest: "Apple_01"
    /// </code>
    /// </summary>
    [AddComponentMenu("Biomata/Observations/Nearby Objects")]
    public class NearbyObjectsObservationProvider : ObservationProviderBase
    {
        [Tooltip(
            "Unity tag assigned to world objects of this category. " +
            "Create the tag in Edit → Project Settings → Tags & Layers first. " +
            "Examples: 'BiomataFood', 'BiomataItem', 'BiomataResource'.")]
        [SerializeField] private string objectTag = "BiomataObject";

        [Tooltip("Maximum world-space radius to include objects.")]
        [Min(0f)]
        [SerializeField] private float detectionRadius = 15f;

        [Tooltip("Maximum number of entries written to the observation list.")]
        [Min(1)]
        [SerializeField] private int maxResults = 8;

        [Tooltip(
            "Base key used for observation output. " +
            "Produces '{key}', '{key}_count', and '{key}_nearest'. " +
            "Examples: 'nearby_food', 'nearby_items', 'nearby_resources'.")]
        [SerializeField] private string observationKey = "nearby_objects";

        [Tooltip("Include a 'distance' field in each observation entry.")]
        [SerializeField] private bool includeDistances = true;

        // Cached at Awake. Call Refresh() when objects spawn or despawn.
        private (Transform t, BiomataObjectData data)[] _objects = Array.Empty<(Transform, BiomataObjectData)>();

        // Reused per-tick to avoid per-call allocations.
        private readonly List<(float sqrDist, Transform t, BiomataObjectData data)> _candidates = new();

        private void Awake() => Refresh();

        /// <summary>
        /// Re-scan the scene for GameObjects matching <see cref="objectTag"/>.
        /// Call when objects of this category are spawned or destroyed at runtime.
        /// </summary>
        public void Refresh()
        {
            if (string.IsNullOrEmpty(objectTag))
            {
                _objects = Array.Empty<(Transform, BiomataObjectData)>();
                return;
            }

            try
            {
                var gos = GameObject.FindGameObjectsWithTag(objectTag);
                _objects = new (Transform, BiomataObjectData)[gos.Length];
                for (int i = 0; i < gos.Length; i++)
                    _objects[i] = (gos[i].transform, gos[i].GetComponent<BiomataObjectData>());
            }
            catch (UnityException)
            {
                _objects = Array.Empty<(Transform, BiomataObjectData)>();
                Debug.LogWarning(
                    $"[NearbyObjectsObservationProvider] Tag '{objectTag}' does not exist. " +
                    "Add it in Edit → Project Settings → Tags & Layers.", this);
            }
        }

        public override void Populate(Dictionary<string, object> observation)
        {
            _candidates.Clear();
            float sqrRadius = detectionRadius * detectionRadius;
            var   agentPos  = transform.position;

            foreach (var (t, data) in _objects)
            {
                if (t == null) continue;
                if (!t.gameObject.activeInHierarchy) continue;   // skip despawned / depleted (SetActive(false)) objects
                if (data != null && !data.IsActive) continue;   // skip data-flagged depleted / inactive

                float sqrDist = (t.position - agentPos).sqrMagnitude;
                if (sqrDist <= sqrRadius)
                    _candidates.Add((sqrDist, t, data));
            }

            // Nearest-first — most relevant object at index 0.
            _candidates.Sort(static (a, b) => a.sqrDist.CompareTo(b.sqrDist));

            int count   = Mathf.Min(_candidates.Count, maxResults);
            var results = new List<object>(count);

            for (int i = 0; i < count; i++)
            {
                var (sqrDist, t, data) = _candidates[i];

                var entry = new Dictionary<string, object>
                {
                    ["id"] = t.name,
                    ["x"]  = (double)t.position.x,
                    ["z"]  = (double)t.position.z,
                };

                if (includeDistances)
                    entry["distance"] = (double)Mathf.Sqrt(sqrDist);

                if (data != null)
                {
                    entry["type"] = data.ObjectType;

                    // Merge dynamic/static properties from the data component.
                    // Written after the base keys so a subclass can override "x"/"z"
                    // if needed, but in practice should use distinct key names.
                    var props = data.GetObservationProperties();
                    foreach (var kv in props)
                        entry[kv.Key] = kv.Value;
                }

                results.Add(entry);
            }

            observation[observationKey]            = results;
            observation[observationKey + "_count"] = count;
            if (count > 0)
                observation[observationKey + "_nearest"] = _candidates[0].t.name;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.2f, 0.9f, 0.4f, 0.2f);
            Gizmos.DrawWireSphere(transform.position, detectionRadius);
        }
    }
}
