using System.Collections;
using System.Collections.Generic;
using Biomata.SDK.Models;
using UnityEngine;
using UnityEngine.AI;

namespace Biomata.Integration.Actions
{
    /// <summary>
    /// NavMesh-aware variant of <see cref="MoveActionHandler"/>.
    ///
    /// Resolves the destination with the inherited <c>ExtractTarget()</c> logic
    /// (explicit coords → action parameters → named POI), then hands it to a
    /// <see cref="NavMeshAgent"/> instead of moving the Transform directly.
    ///
    /// Rotation is driven manually (same Quaternion.RotateTowards approach as the
    /// base class) so that <see cref="NavMeshAgent.updateRotation"/> can be
    /// disabled — giving you full control over facing direction.
    ///
    /// ── Setup ────────────────────────────────────────────────────────────────
    ///
    /// 1. Add a <c>NavMeshAgent</c> component to the agent GameObject.
    /// 2. Bake a NavMesh for the scene (Window → AI → Navigation).
    /// 3. Replace <see cref="MoveActionHandler"/> with this component, <em>or</em>
    ///    place this component earlier in the component list so it shadows the base.
    /// 4. Tune <see cref="stoppingDistance"/> and <see cref="repathInterval"/>
    ///    in the Inspector.
    ///
    /// ── Repath interval ──────────────────────────────────────────────────────
    ///
    /// <see cref="repathInterval"/> controls how often <c>SetDestination</c> is
    /// re-issued while the agent is en route. This triggers NavMesh recalculation
    /// when the path is invalidated mid-movement (e.g. a door closes, dynamic
    /// obstacle moves into the path). It is <em>not</em> needed for the common
    /// case where the brain updates coordinates each tick; in that scenario each
    /// new <c>ExecuteCoroutine</c> call starts fresh with the latest position.
    /// </summary>
    [AddComponentMenu("Biomata/Actions/NavMesh Move")]
    [RequireComponent(typeof(NavMeshAgent))]
    public class NavMeshMoveActionHandler : MoveActionHandler
    {
        [Header("NavMesh")]
        [Tooltip("Arrival radius passed to NavMeshAgent.stoppingDistance each move.")]
        [Min(0f)]
        [SerializeField] private float stoppingDistance = 0.2f;

        [Tooltip("Seconds between re-issuing SetDestination to recover from path invalidation.")]
        [Min(0.05f)]
        [SerializeField] private float repathInterval = 0.5f;

        [Tooltip("Degrees per second for manual yaw rotation toward velocity. 0 = no rotation.")]
        [Min(0f)]
        [SerializeField] private float navRotateSpeed = 360f;

        // Lazy cache — avoids Awake ordering concerns with the parent's private Awake.
        private NavMeshAgent _navAgent;
        private NavMeshAgent NavAgent => _navAgent != null
            ? _navAgent
            : (_navAgent = GetComponent<NavMeshAgent>());

        // ── Public navigation API ─────────────────────────────────────────────

        /// <summary>
        /// Navigate to a world-space position, reusing all configured NavMesh settings.
        /// Call from other action handlers (e.g. EatActionHandler) to delegate movement
        /// without duplicating NavMesh logic.
        /// </summary>
        public IEnumerator NavigateTo(Vector3 destination, UnityAgentBridge bridge)
        {
            var agent = NavAgent;
            if (agent == null) yield break;

            agent.stoppingDistance = stoppingDistance;
            agent.updateRotation   = false;
            agent.SetDestination(destination);

            float repathTimer = 0f;
            while (true)
            {
                repathTimer += Time.deltaTime;
                if (repathTimer >= repathInterval)
                {
                    repathTimer = 0f;
                    agent.SetDestination(destination);
                }

                var flatVelocity = agent.velocity;
                flatVelocity.y = 0f;
                if (navRotateSpeed > 0f && flatVelocity.sqrMagnitude > 0.0001f)
                {
                    var targetRot = Quaternion.LookRotation(flatVelocity.normalized);
                    bridge.transform.rotation = Quaternion.RotateTowards(
                        bridge.transform.rotation, targetRot, navRotateSpeed * Time.deltaTime);
                }

                if (!agent.pathPending && agent.remainingDistance <= agent.stoppingDistance)
                    yield break;

                yield return null;
            }
        }

        // ── Execution ─────────────────────────────────────────────────────────

        public override IEnumerator ExecuteCoroutine(AgentDecisionResult decision, UnityAgentBridge bridge)
        {
            var agent = NavAgent;

            if (agent == null)
            {
                Debug.LogError(
                    $"[NavMeshMoveActionHandler] '{gameObject.name}' is missing a NavMeshAgent. " +
                    "Add the component or use MoveActionHandler for Transform-based movement.", this);
                yield break;
            }

            var target = ExtractTarget(decision);
            if (target == null) yield break;

            agent.stoppingDistance = stoppingDistance;
            agent.updateRotation   = false;  // manual rotation below

            agent.SetDestination(target.Value);

            float repathTimer = 0f;

            while (true)
            {
                // Re-issue the destination periodically to recover from path invalidation.
                repathTimer += Time.deltaTime;
                if (repathTimer >= repathInterval)
                {
                    repathTimer = 0f;
                    agent.SetDestination(target.Value);
                }

                // Manual yaw — rotate toward the agent's current velocity direction.
                var flatVelocity = agent.velocity;
                flatVelocity.y = 0f;
                if (navRotateSpeed > 0f && flatVelocity.sqrMagnitude > 0.0001f)
                {
                    var targetRot = Quaternion.LookRotation(flatVelocity.normalized);
                    bridge.transform.rotation = Quaternion.RotateTowards(
                        bridge.transform.rotation, targetRot, navRotateSpeed * Time.deltaTime);
                }

                // Arrival check — wait until path is computed and we are close enough.
                if (!agent.pathPending && agent.remainingDistance <= agent.stoppingDistance)
                    yield break;

                yield return null;
            }
        }

        /// <summary>
        /// NavMesh-aware portal transition: uses <see cref="NavMeshAgent.Warp"/> to reposition
        /// the agent on the navmesh surface at the exit anchor.
        ///
        /// The base class sets <c>transform.position</c> directly, which desyncs the
        /// NavMeshAgent's internal path state and causes the next <c>SetDestination</c>
        /// call to produce incorrect paths. Override with a fade/animation by adding
        /// a yield before the Warp call.
        /// </summary>
        protected override IEnumerator PortalTransition(Transform agent, Vector3 exitPosition)
        {
            NavAgent.Warp(exitPosition);
            yield return null;
        }
    }
}
