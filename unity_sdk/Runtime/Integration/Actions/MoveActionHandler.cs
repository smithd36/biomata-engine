using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Biomata.SDK.Models;
using UnityEngine;

namespace Biomata.Integration.Actions
{
    /// <summary>
    /// Moves the agent's Transform toward a target position each tick.
    ///
    /// Target is extracted from engine_commands in priority order:
    ///   1. <c>{ "type": "navigate", "x": …, "y": …, "z": … }</c> in EngineCommands
    ///   2. <c>target_x</c> / <c>target_z</c> keys in action Parameters
    ///   3. <c>destination</c> string in the navigate command, resolved to a tagged POI by name
    ///
    /// For path 3, tag POI GameObjects with <see cref="poiTag"/> (default: <c>"BiomataPOI"</c>).
    /// The cache is built in Awake — call <see cref="RefreshPOICache"/> if POIs spawn at runtime.
    ///
    /// Override <see cref="ExtractTarget"/> to drive a NavMeshAgent, Rigidbody, or
    /// animation root-motion system instead of raw Transform movement.
    /// </summary>
    [AddComponentMenu("Biomata/Actions/Move")]
    public class MoveActionHandler : ActionHandlerBase
    {
        [SerializeField] private float moveSpeed        = 3.5f;
        [SerializeField] private float rotateSpeed      = 360f;
        [SerializeField] private float arrivalThreshold = 0.15f;

        [Tooltip("Unity tag assigned to POI GameObjects. Must match POIObservationProvider.")]
        [SerializeField] private string poiTag = "BiomataPOI";

        private Dictionary<string, Transform> _poiCache;

        private void Awake() => RefreshPOICache();

        /// <summary>
        /// Re-scan the scene and rebuild the POI name→position cache.
        /// Call when POIs are spawned or destroyed at runtime.
        /// </summary>
        public void RefreshPOICache()
        {
            _poiCache = new Dictionary<string, Transform>();
            if (string.IsNullOrEmpty(poiTag)) return;
            try
            {
                foreach (var go in GameObject.FindGameObjectsWithTag(poiTag))
                    _poiCache[go.name.ToLowerInvariant()] = go.transform;
            }
            catch (UnityException)
            {
                Debug.LogWarning(
                    $"[MoveActionHandler] Tag '{poiTag}' does not exist. " +
                    "Add it in Edit → Project Settings → Tags & Layers.", this);
            }
        }

        /// <summary>Configure movement parameters at runtime (call immediately after AddComponent).</summary>
        public void Configure(float moveSpeed, float arrivalThreshold = 0.15f, float rotateSpeed = 360f)
        {
            this.moveSpeed        = moveSpeed;
            this.arrivalThreshold = arrivalThreshold;
            this.rotateSpeed      = rotateSpeed;
        }

        private static readonly HashSet<string> HandledActions = new HashSet<string>
        {
            "move", "walk", "navigate", "go", "travel", "follow", "flee",
        };

        public override bool CanHandle(string action) =>
            HandledActions.Contains(action?.ToLowerInvariant() ?? string.Empty);

        public override IEnumerator ExecuteCoroutine(AgentDecisionResult decision, UnityAgentBridge bridge)
        {
            var target = ExtractTarget(decision);
            if (target == null) yield break;

            yield return MoveTowards(bridge.transform, target.Value);
        }

        protected virtual IEnumerator MoveTowards(Transform t, Vector3 target)
        {
            while (Vector3.Distance(t.position, target) > arrivalThreshold)
            {
                var direction = target - t.position;
                direction.y = 0f;

                if (rotateSpeed > 0f && direction.sqrMagnitude > 0.0001f)
                {
                    var targetRot = Quaternion.LookRotation(direction.normalized);
                    t.rotation = Quaternion.RotateTowards(t.rotation, targetRot, rotateSpeed * Time.deltaTime);
                }

                t.position = Vector3.MoveTowards(t.position, target, moveSpeed * Time.deltaTime);
                yield return null;
            }
        }

        /// <summary>
        /// Extract the world-space destination from the decision.
        /// Returns <c>null</c> when no valid position is found (action is skipped).
        /// </summary>
        protected virtual Vector3? ExtractTarget(AgentDecisionResult decision)
        {
            // Path 1: explicit coordinates in the navigate engine command.
            foreach (var cmd in decision.EngineCommands)
            {
                if (!TryGetStr(cmd, "type", out var type) || type != "navigate") continue;
                if (TryGetFloat(cmd, "x", out var cx) && TryGetFloat(cmd, "z", out var cz))
                {
                    TryGetFloat(cmd, "y", out var cy);
                    return new Vector3(cx, cy, cz);
                }
            }

            // Path 2: explicit coordinates in action parameters.
            if (TryGetFloat(decision.Parameters, "target_x", out var px) &&
                TryGetFloat(decision.Parameters, "target_z", out var pz))
            {
                TryGetFloat(decision.Parameters, "target_y", out var py);
                return new Vector3(px, py, pz);
            }

            // Path 3: destination name resolved via POI cache.
            foreach (var cmd in decision.EngineCommands)
            {
                if (!TryGetStr(cmd, "type", out var type) || type != "navigate") continue;
                if (!TryGetStr(cmd, "destination", out var dest)) continue;

                var key = dest.ToLowerInvariant();
                if (_poiCache != null && _poiCache.TryGetValue(key, out var t))
                    return t.position;

                Debug.LogWarning(
                    $"[MoveActionHandler] '{gameObject.name}': destination '{dest}' not found " +
                    $"in POI cache (tag '{poiTag}'). Call RefreshPOICache() if the POI was " +
                    "spawned after Awake, or check the GameObject name matches the brain's output.",
                    this);
            }

            return null;
        }

        private static bool TryGetStr(Dictionary<string, object> d, string key, out string v)
        {
            v = null;
            return d.TryGetValue(key, out var raw) && (v = raw?.ToString()) != null;
        }

        private static bool TryGetFloat(Dictionary<string, object> d, string key, out float v)
        {
            v = 0f;
            return d.TryGetValue(key, out var raw)
                && float.TryParse(raw?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }
    }
}
