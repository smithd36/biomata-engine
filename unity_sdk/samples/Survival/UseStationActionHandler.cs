using System.Collections;
using System.Collections.Generic;
using Biomata.Integration;
using Biomata.Integration.Actions;
using Biomata.Integration.Needs;
using Biomata.SDK.Models;
using UnityEngine;

namespace Biomata.Samples.Survival
{
    /// <summary>
    /// Agent-side handler for the "walk to a place, do a timed thing, apply an effect" pattern.
    /// The generalised form of the demo's <c>UsePoiActionHandler</c>/<c>EatActionHandler</c>.
    ///
    /// On a matching action (e.g. <c>eat</c>, <c>sleep</c>, <c>work</c>) it:
    /// <list type="number">
    ///   <item>resolves the target <see cref="Station"/> by name (from the navigate command's
    ///   <c>destination</c>, or a <c>target</c>/<c>station</c>/<c>destination</c> parameter);</item>
    ///   <item>walks the agent to the station — delegating to a sibling
    ///   <see cref="NavMeshMoveActionHandler"/> if present, else a simple straight-line move;</item>
    ///   <item>waits <see cref="Station.Duration"/>, then applies the station's effects to the
    ///   agent's <see cref="NeedsComponent"/>.</item>
    /// </list>
    ///
    /// Setup: add this + <see cref="NeedsComponent"/> to the agent; add <see cref="Station"/>
    /// to world objects tagged <see cref="stationTag"/>. See README.md in this folder.
    /// </summary>
    [AddComponentMenu("Biomata/Samples/Use Station")]
    public class UseStationActionHandler : ActionHandlerBase
    {
        [Tooltip("Action verbs this handler covers (lower-case). Each should also be declared in actions.yaml.")]
        [SerializeField] private string[] verbs = { "use", "eat", "sleep", "work", "rest", "warm" };

        [Tooltip("Unity tag on Station GameObjects. Must match the tag used by MoveActionHandler.")]
        [SerializeField] private string stationTag = "BiomataPOI";

        [Tooltip("Fallback move speed when no NavMeshMoveActionHandler is present.")]
        [SerializeField] private float fallbackMoveSpeed = 3.5f;

        [Tooltip("Arrival radius for the fallback straight-line move.")]
        [SerializeField] private float fallbackArrival = 0.3f;

        private Dictionary<string, Station> _cache;
        private NeedsComponent _needs;
        private NavMeshMoveActionHandler _nav;

        private void Awake()
        {
            _needs = GetComponent<NeedsComponent>();
            _nav   = GetComponent<NavMeshMoveActionHandler>();
            RefreshCache();
        }

        /// <summary>Rebuild the station name→component cache. Call when stations spawn at runtime.</summary>
        public void RefreshCache()
        {
            _cache = new Dictionary<string, Station>();
            if (string.IsNullOrEmpty(stationTag)) return;
            try
            {
                foreach (var go in GameObject.FindGameObjectsWithTag(stationTag))
                {
                    var station = go.GetComponent<Station>();
                    if (station != null) _cache[station.Key.ToLowerInvariant()] = station;
                }
            }
            catch (UnityException)
            {
                Debug.LogWarning(
                    $"[UseStationActionHandler] Tag '{stationTag}' does not exist. " +
                    "Add it in Edit → Project Settings → Tags & Layers.", this);
            }
        }

        public override IReadOnlyCollection<string> DeclaredActionNames => verbs;

        public override bool CanHandle(string action)
        {
            var a = action?.ToLowerInvariant() ?? string.Empty;
            foreach (var v in verbs)
                if (v == a) return true;
            return false;
        }

        public override IEnumerator ExecuteCoroutine(AgentDecisionResult decision, UnityAgentBridge bridge)
        {
            var station = ResolveStation(decision);
            if (station == null)
            {
                Debug.LogWarning(
                    $"[UseStationActionHandler] '{name}': no Station found for action " +
                    $"'{decision.Action}'. Check the brain's target name and the station tag.", this);
                yield break;
            }
            if (!station.IsActive)
                yield break; // depleted/closed — silently abort; brain will pick again next tick

            // 1. Walk to the station.
            if (_nav != null)
                yield return _nav.NavigateTo(station.ApproachPosition, bridge);
            else
                yield return FallbackMove(bridge.transform, station.ApproachPosition);

            // 2. Spend time using it.
            if (station.Duration > 0f)
                yield return new WaitForSeconds(station.Duration);

            // 3. Apply effects to the agent's needs.
            if (_needs != null)
                station.ApplyEffects(_needs);
            else
                Debug.LogWarning(
                    $"[UseStationActionHandler] '{name}': no NeedsComponent — station effects skipped.", this);
        }

        // ── Station resolution ────────────────────────────────────────────────────

        private Station ResolveStation(AgentDecisionResult decision)
        {
            // Navigate-style engine command: { "type": "navigate", "destination": "..." }
            foreach (var cmd in decision.EngineCommands)
            {
                if (cmd.TryGetValue("destination", out var dest) && TryLookup(dest, out var s))
                    return s;
            }

            // Action parameters.
            foreach (var key in new[] { "target", "station", "destination" })
                if (decision.Parameters.TryGetValue(key, out var val) && TryLookup(val, out var s))
                    return s;

            return null;
        }

        private bool TryLookup(object nameObj, out Station station)
        {
            station = null;
            var key = nameObj?.ToString();
            return !string.IsNullOrEmpty(key)
                && _cache != null
                && _cache.TryGetValue(key.ToLowerInvariant(), out station);
        }

        // ── Fallback movement (no NavMesh) ──────────────────────────────────────────

        private IEnumerator FallbackMove(Transform t, Vector3 target)
        {
            while (Vector3.Distance(t.position, target) > fallbackArrival)
            {
                t.position = Vector3.MoveTowards(t.position, target, fallbackMoveSpeed * Time.deltaTime);
                yield return null;
            }
        }
    }
}
