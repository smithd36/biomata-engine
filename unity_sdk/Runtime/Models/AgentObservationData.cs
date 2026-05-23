// Biomata.SDK — AgentObservationData.cs
// Per-agent world-state pushed to the server before each TickAsync() call.

using System.Collections.Generic;

namespace Biomata.SDK.Models
{
    /// <summary>
    /// One agent's current world-state, as observed by the Unity host.
    /// </summary>
    public class AgentObservationData
    {
        /// <summary>
        /// ID of the agent this observation belongs to.
        /// Must match a registered agent's ID.
        /// </summary>
        public string AgentId { get; set; }

        /// <summary>
        /// Domain-specific state dictionary. All values must be JSON-serializable.
        ///
        /// Common keys:
        ///   "location"      — string or position description
        ///   "nearby_agents" — list of dicts with id/name (overrides server-side visibility)
        ///   "health"        — numeric current HP
        ///   ... any domain-specific fields the brain reads ...
        /// </summary>
        public Dictionary<string, object> Observation { get; set; }

        public AgentObservationData() { }

        public AgentObservationData(string agentId, Dictionary<string, object> observation)
        {
            AgentId     = agentId;
            Observation = observation;
        }
    }
}
