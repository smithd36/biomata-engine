// Biomata.SDK — TickResult.cs
// Aggregated result of one simulation tick.

using System.Collections.Generic;

namespace Biomata.SDK.Models
{
    /// <summary>
    /// Aggregated output of one simulation tick returned by <c>TickClient.TickAsync()</c>.
    /// </summary>
    public class TickResult
    {
        /// <summary>Engine tick number (monotonically increasing).</summary>
        public int Tick { get; }

        /// <summary>
        /// One <see cref="AgentDecisionResult"/> per agent that completed a step.
        /// Agents whose steps errored appear in <see cref="Errors"/> instead.
        /// </summary>
        public IReadOnlyList<AgentDecisionResult> Decisions { get; }

        /// <summary>
        /// Per-agent step errors: (agentId, errorMessage).
        /// Non-empty when one or more agents raised an unhandled exception.
        /// </summary>
        public IReadOnlyList<(string AgentId, string Message)> Errors { get; }

        /// <summary>
        /// All engine_commands from every agent, in decision order.
        /// Convenience accessor equivalent to iterating <see cref="Decisions"/>
        /// and collecting each agent's <see cref="AgentDecisionResult.EngineCommands"/>.
        /// </summary>
        public IReadOnlyList<(string AgentId, Dictionary<string, object> Command)> AllCommands { get; }

        // O(1) agent-id lookup table — avoids the O(N²) cost of per-agent
        // ForAgent(id) scans when UnitySimulationManager distributes decisions
        // to N bridges each tick.
        private readonly Dictionary<string, AgentDecisionResult> _byId;

        /// <summary>
        /// Transport-neutral constructor. WebSocketTransport uses this after
        /// decoding the JSON tick response into plain BCL types.
        /// </summary>
        internal TickResult(
            int                                          tick,
            IReadOnlyList<AgentDecisionResult>           decisions,
            IReadOnlyList<(string AgentId, string Msg)>  errors)
        {
            Tick      = tick;
            Decisions = decisions ?? (IReadOnlyList<AgentDecisionResult>)System.Array.Empty<AgentDecisionResult>();
            Errors    = errors    ?? System.Array.Empty<(string, string)>();

            // Build the id → decision lookup and AllCommands view.
            var byId = new Dictionary<string, AgentDecisionResult>(Decisions.Count);
            foreach (var d in Decisions) byId[d.AgentId] = d;
            _byId = byId;

            List<(string, Dictionary<string, object>)> cmds = null;
            foreach (var d in Decisions)
            {
                if (d.EngineCommands.Count == 0) continue;
                cmds ??= new List<(string, Dictionary<string, object>)>();
                foreach (var cmd in d.EngineCommands)
                    cmds.Add((d.AgentId, cmd));
            }
            AllCommands = (IReadOnlyList<(string, Dictionary<string, object>)>)cmds
                          ?? System.Array.Empty<(string, Dictionary<string, object>)>();
        }

        /// <summary>Find the decision for a specific agent, or null if not present. O(1).</summary>
        public AgentDecisionResult ForAgent(string agentId)
        {
            if (string.IsNullOrEmpty(agentId)) return null;
            return _byId.TryGetValue(agentId, out var d) ? d : null;
        }

        public override string ToString() =>
            $"Tick {Tick}: {Decisions.Count} decisions, {Errors.Count} errors";
    }
}
