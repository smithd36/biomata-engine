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
            foreach (var cmd in decision.EngineCommands)
            {
                if (!TryGetStr(cmd, "type", out var type) || type != "navigate") continue;
                if (TryGetFloat(cmd, "x", out var cx) && TryGetFloat(cmd, "z", out var cz))
                {
                    TryGetFloat(cmd, "y", out var cy);
                    return new Vector3(cx, cy, cz);
                }
            }

            if (TryGetFloat(decision.Parameters, "target_x", out var px) &&
                TryGetFloat(decision.Parameters, "target_z", out var pz))
            {
                TryGetFloat(decision.Parameters, "target_y", out var py);
                return new Vector3(px, py, pz);
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
