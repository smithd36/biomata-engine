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

        [Tooltip(
            "When the requested destination is off the NavMesh (e.g. the centre of a " +
            "solid prop a POI sits on), snap it to the nearest NavMesh point within this " +
            "radius so the agent has a reachable target. 0 = use the raw destination.")]
        [Min(0f)]
        [SerializeField] private float navSampleRadius = 2f;

        // Lazy cache — avoids Awake ordering concerns with the parent's private Awake.
        private NavMeshAgent _navAgent;
        private NavMeshAgent NavAgent => _navAgent != null
            ? _navAgent
            : (_navAgent = GetComponent<NavMeshAgent>());

        // Current goal, read live by the drive loop so Retarget() can steer an
        // in-flight move without restarting its coroutine.
        private Vector3 _destination;
        private bool    _warnedOffMesh;

        // ── Public navigation API ─────────────────────────────────────────────

        /// <summary>
        /// Navigate to a world-space position, reusing all configured NavMesh settings.
        /// Call from other action handlers (e.g. EatActionHandler) to delegate movement
        /// without duplicating NavMesh logic.
        /// </summary>
        public IEnumerator NavigateTo(Vector3 destination, UnityAgentBridge bridge)
        {
            _destination = ResolveOnNavMesh(destination);
            yield return DriveToDestination(bridge);
        }

        // ── Execution ─────────────────────────────────────────────────────────

        public override IEnumerator ExecuteCoroutine(AgentDecisionResult decision, UnityAgentBridge bridge)
        {
            if (NavAgent == null)
            {
                Debug.LogError(
                    $"[NavMeshMoveActionHandler] '{gameObject.name}' is missing a NavMeshAgent. " +
                    "Add the component or use MoveActionHandler for Transform-based movement.", this);
                yield break;
            }

            var target = ExtractTarget(decision);
            if (target == null) yield break;

            _destination = ResolveOnNavMesh(target.Value);
            yield return DriveToDestination(bridge);
        }

        // ── Re-targeting / interruption ─────────────────────────────────────────

        /// <summary>Movement is continuous: stream new destinations in without restarting.</summary>
        public override bool CanRetarget => true;

        /// <inheritdoc/>
        public override void Retarget(AgentDecisionResult decision, UnityAgentBridge bridge)
        {
            var target = ExtractTarget(decision);
            if (target == null) return;

            _destination = ResolveOnNavMesh(target.Value);
            var agent = NavAgent;
            if (agent != null && agent.isOnNavMesh)
                agent.SetDestination(_destination);   // drive loop picks it up; no restart
        }

        /// <inheritdoc/>
        public override void OnInterrupted(UnityAgentBridge bridge)
        {
            // Stop the agent so a following stationary action (idle/speak/interact) does
            // not keep drifting toward the cancelled destination.
            var agent = NavAgent;
            if (agent != null && agent.isOnNavMesh)
            {
                agent.ResetPath();
                agent.velocity = Vector3.zero;
            }
        }

        // ── Shared drive loop ───────────────────────────────────────────────────

        /// <summary>
        /// Steer the NavMeshAgent toward <see cref="_destination"/> until it arrives or
        /// the destination is proven unreachable. Reads the field every frame so
        /// <see cref="Retarget"/> can change the goal mid-flight.
        /// </summary>
        private IEnumerator DriveToDestination(UnityAgentBridge bridge)
        {
            var agent = NavAgent;
            if (agent == null) yield break;

            if (!agent.isOnNavMesh)
            {
                if (!_warnedOffMesh)
                {
                    _warnedOffMesh = true;
                    Debug.LogWarning(
                        $"[NavMeshMoveActionHandler] '{gameObject.name}' is not on a baked NavMesh — " +
                        "agent cannot move. Bake a NavMesh (Window → AI → Navigation) and ensure the " +
                        "agent spawns on it.", this);
                }
                yield break;
            }
            _warnedOffMesh = false;

            agent.stoppingDistance = stoppingDistance;
            agent.updateRotation   = false;  // manual yaw below
            agent.SetDestination(_destination);

            float repathTimer = 0f;
            while (true)
            {
                // Re-issue periodically to recover from path invalidation (and to pick up
                // a destination changed by Retarget on a frame between repaths).
                repathTimer += Time.deltaTime;
                if (repathTimer >= repathInterval)
                {
                    repathTimer = 0f;
                    agent.SetDestination(_destination);
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

                // Arrival / termination — only valid once the path is computed.
                if (!agent.pathPending)
                {
                    // Unreachable target (blocked, or off-mesh beyond navSampleRadius):
                    // stop at the closest reachable point instead of looping forever on
                    // a remainingDistance that never drops (or stays Infinity).
                    if (agent.pathStatus != NavMeshPathStatus.PathComplete)
                        yield break;
                    if (agent.remainingDistance <= agent.stoppingDistance)
                        yield break;
                }

                yield return null;
            }
        }

        /// <summary>
        /// Snap a requested destination onto the NavMesh so it is reachable. Falls back
        /// to the raw point when sampling is disabled or finds no mesh nearby.
        /// </summary>
        private Vector3 ResolveOnNavMesh(Vector3 raw)
        {
            if (navSampleRadius > 0f &&
                NavMesh.SamplePosition(raw, out var hit, navSampleRadius, NavMesh.AllAreas))
                return hit.position;
            return raw;
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
