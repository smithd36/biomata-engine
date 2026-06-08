// Biomata.SDK — ManifestData.cs
// Canonical wire types for the BiomataActions.json action manifest.
// Matches the shape generated from simulation/actions.yaml by ActionManifest.export_json().

using System;

namespace Biomata.SDK.Models
{
    /// <summary>
    /// One action entry as declared in BiomataActions.json (generated from actions.yaml).
    /// </summary>
    [Serializable]
    public class ManifestActionEntry
    {
        /// <summary>Action name (e.g. "move", "speak", "interact").</summary>
        public string   name;

        /// <summary>Human-readable description of what the action does.</summary>
        public string   description;

        /// <summary>Action kind tag (e.g. "movement", "social").</summary>
        public string   kind;

        /// <summary>Capability tags required for this action to be available to an agent.</summary>
        public string[] required_capabilities;
        // 'parameters' is omitted — it is metadata for humans and Python validation only.
    }

    /// <summary>
    /// Full content of BiomataActions.json — the canonical action manifest.
    /// Returned by <see cref="Biomata.Integration.Actions.ActionManifestLoader.Load"/>.
    /// </summary>
    [Serializable]
    public class ManifestData
    {
        public string                version;
        public ManifestActionEntry[] actions;
    }
}
