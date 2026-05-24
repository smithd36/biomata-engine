// Biomata.SDK — AgentDecisionResult.cs
// One agent's decision returned by TickClient.TickAsync().

using System.Collections.Generic;

namespace Biomata.SDK.Models
{
    /// <summary>
    /// The Python engine's decision for one agent in a single tick.
    /// Returned as part of <see cref="TickResult.Decisions"/>.
    /// </summary>
    public class AgentDecisionResult
    {
        /// <summary>Agent identifier.</summary>
        public string AgentId { get; }

        /// <summary>Agent display name.</summary>
        public string AgentName { get; }

        /// <summary>Name of the action the agent chose (e.g. <c>"navigate"</c>, <c>"attack"</c>).</summary>
        public string Action { get; }

        /// <summary>Action parameters from the brain's intent.</summary>
        public Dictionary<string, object> Parameters { get; }

        /// <summary>Human-readable description of the action's outcome.</summary>
        public string OutcomeText { get; }

        /// <summary>
        /// Structured commands for the Unity host to execute.
        /// Each entry is a dict with at minimum a <c>"type"</c> key.
        /// Shape is handler-defined; see the Python action handler for the exact schema.
        /// </summary>
        public IReadOnlyList<Dictionary<string, object>> EngineCommands { get; }

        /// <summary>Non-empty when the agent's step raised an unhandled exception.</summary>
        public string Error { get; }

        /// <summary>True if this decision succeeded without errors.</summary>
        public bool IsSuccess => string.IsNullOrEmpty(Error);

        /// <summary>
        /// Used by WebSocketTransport after a JSON response has been decoded
        /// into plain BCL types.
        /// </summary>
        internal AgentDecisionResult(
            string                                       agentId,
            string                                       agentName,
            string                                       action,
            Dictionary<string, object>                   parameters,
            string                                       outcomeText,
            IReadOnlyList<Dictionary<string, object>>    engineCommands,
            string                                       error)
        {
            AgentId        = agentId   ?? string.Empty;
            AgentName      = agentName ?? string.Empty;
            Action         = action    ?? string.Empty;
            Parameters     = parameters     ?? new Dictionary<string, object>();
            OutcomeText    = outcomeText    ?? string.Empty;
            EngineCommands = engineCommands ?? new List<Dictionary<string, object>>(0);
            Error          = string.IsNullOrEmpty(error) ? null : error;
        }

        public override string ToString() =>
            $"[{AgentId}] {Action}: {OutcomeText}";
    }
}
