using System;
using System.Collections.Generic;
using UnityEngine;

namespace Biomata.Integration
{
    /// <summary>
    /// Optional v2 data component for POI GameObjects.
    ///
    /// Attach this alongside the "BiomataPOI" tag to supply structured
    /// interaction metadata (type, named anchors) to the Biomata backend.
    ///
    /// <see cref="POIObservationProvider"/> reads this component when present and
    /// includes <c>"type"</c> and <c>"anchors"</c> in each POI observation entry.
    /// GameObjects that do NOT have this component are reported exactly as before
    /// (id, x, z, optional distance) — zero behavior change for existing scenes.
    ///
    /// Anchor offsets are expressed in the POI's local space and are converted to
    /// world space at observation time so the brain receives absolute coordinates.
    /// </summary>
    [AddComponentMenu("Biomata/POI Data (v2)")]
    public class BiomataPOIData : MonoBehaviour
    {
        [Tooltip(
            "Semantic category of this POI. " +
            "Examples: 'location', 'shop', 'door', 'chest', 'waypoint'. " +
            "Passed verbatim to the brain as the 'type' field.")]
        [SerializeField] private string poiType = "location";

        [Tooltip(
            "Named anchor points expressed as local-space offsets from this transform. " +
            "Each anchor is converted to world space before being sent to the backend. " +
            "Leave empty if this POI has no interaction points beyond its root position.")]
        [SerializeField] private List<AnchorEntry> anchors = new();

        [Header("Traversal (Phase 4)")]
        [Tooltip(
            "Mark this POI as a portal — a spatial transition point (door, staircase, " +
            "elevator, zone boundary). When true, MoveActionHandler automatically moves " +
            "the agent to the exit anchor of the connected POI after arrival.")]
        [SerializeField] private bool isPortal = false;

        [Tooltip(
            "The name of the destination POI GameObject (must match its name exactly). " +
            "The agent is moved to that POI's 'exit' anchor after crossing. " +
            "Ignored when isPortal is false.")]
        [SerializeField] private string connectsTo = "";

        /// <summary>Semantic category of this POI (e.g. "location", "shop").</summary>
        public string PoiType => poiType;

        /// <summary>Named anchor points in local space. Read-only at runtime.</summary>
        public IReadOnlyList<AnchorEntry> Anchors => anchors;

        /// <summary>
        /// When <c>true</c> this POI is a spatial transition point (door, staircase, portal).
        /// <see cref="MoveActionHandler"/> moves the agent to <see cref="ConnectsTo"/>'s
        /// <c>exit</c> anchor automatically after arrival.
        /// </summary>
        public bool IsPortal => isPortal;

        /// <summary>
        /// Name of the destination POI GameObject.  The agent resumes at that POI's
        /// <c>exit</c> anchor after the transition.  Empty string = no destination configured.
        /// </summary>
        public string ConnectsTo => connectsTo;

        /// <summary>One named anchor point on a POI.</summary>
        [Serializable]
        public class AnchorEntry
        {
            [Tooltip("Anchor name sent to the backend, e.g. 'interact', 'look_at', 'exit'.")]
            public string name;

            [Tooltip("Position relative to this POI's transform origin.")]
            public Vector3 localOffset;
        }

        /// <summary>
        /// Returns the world-space position of the named anchor, or <c>null</c>
        /// if no anchor with that name exists.
        ///
        /// The local offset is converted to world space using this transform,
        /// so the result is correct regardless of the POI's position, rotation,
        /// or scale hierarchy.
        /// </summary>
        public Vector3? GetWorldAnchor(string anchorName)
        {
            foreach (var anchor in anchors)
            {
                if (string.Equals(anchor.name, anchorName, System.StringComparison.OrdinalIgnoreCase))
                    return transform.TransformPoint(anchor.localOffset);
            }
            return null;
        }
    }
}
