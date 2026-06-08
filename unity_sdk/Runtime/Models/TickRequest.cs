// Biomata.SDK — TickRequest.cs
// Typed input DTO for TickClient.TickAsync(TickRequest).

using System.Collections.Generic;

namespace Biomata.SDK.Models
{
    /// <summary>
    /// All parameters for one simulation tick, bundled as a typed DTO.
    ///
    /// Use the convenience constructor or set properties directly:
    /// <code>
    /// var req = new TickRequest
    /// {
    ///     AgentObservations = myObsList,
    ///     WorldMetadata     = new Dictionary&lt;string, object&gt; { ["time"] = 12.0 },
    /// };
    /// await ticks.TickAsync(req, ct);
    /// </code>
    ///
    /// Alternatively, the flat <c>TickAsync(IEnumerable, Dictionary, CancellationToken)</c>
    /// overload remains available for callers that assemble parameters inline.
    /// </summary>
    public class TickRequest
    {
        /// <summary>
        /// Per-agent world-state observations to push before running cognition.
        /// Null is treated the same as an empty list.
        /// </summary>
        public IList<AgentObservationData> AgentObservations { get; set; }

        /// <summary>
        /// Optional scene-level metadata injected alongside agent observations.
        /// Common keys written by <c>UnitySimulationManager</c>: unity_time, unity_frame, world.
        /// </summary>
        public Dictionary<string, object> WorldMetadata { get; set; }
    }
}
