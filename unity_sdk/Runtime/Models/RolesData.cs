// Biomata.SDK — RolesData.cs
// Wire types for the roles.list RPC response.
// Matches the shape of the backend roles: block in sim.yaml.

using System;

namespace Biomata.SDK.Models
{
    /// <summary>
    /// A single role entry as returned by the <c>roles.list</c> RPC.
    /// </summary>
    [Serializable]
    public class RoleEntry
    {
        /// <summary>Role name as declared in sim.yaml (e.g. "Villager", "Guard").</summary>
        public string   name;

        /// <summary>Capability tags the role grants (e.g. ["social", "trade"]).</summary>
        public string[] capabilities;

        /// <summary>Observation keys the role expects (informational; may be empty).</summary>
        public string[] observations;

        /// <summary>Python-side brain provider shorthand (e.g. "ollama"). Advisory for Unity.</summary>
        public string   brain_provider;

        /// <summary>Fully-qualified Python brain class path resolved from the provider, if any.</summary>
        public string   brain_class;
    }

    /// <summary>
    /// Full response from the <c>roles.list</c> RPC.
    /// </summary>
    [Serializable]
    public class RolesData
    {
        public string     version;
        public RoleEntry[] roles;
    }
}
