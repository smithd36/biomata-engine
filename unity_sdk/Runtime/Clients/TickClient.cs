// Biomata.SDK — TickClient.cs
// Run cognition ticks; pause/resume the server's autonomous loop.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Biomata.SDK.Models;
using Biomata.SDK.Transport;

namespace Biomata.SDK.Clients
{
    /// <summary>
    /// Drives simulation ticks and controls the server's autonomous run loop.
    /// </summary>
    public class TickClient
    {
        private readonly ITransport _transport;

        internal TickClient(ITransport transport)
        {
            _transport = transport;
        }

        /// <summary>Run one cognition cycle on the server.</summary>
        public Task<TickResult> TickAsync(
            IEnumerable<AgentObservationData> agentObservations = null,
            Dictionary<string, object>        worldMetadata     = null,
            CancellationToken                 ct                = default)
            => _transport.TickAsync(agentObservations, worldMetadata, ct);

        /// <summary>Suspend the server's autonomous run loop after the current tick.</summary>
        public Task PauseAsync(CancellationToken ct = default) => _transport.PauseAsync(ct);

        /// <summary>Resume a paused server autonomous run loop.</summary>
        public Task ResumeAsync(CancellationToken ct = default) => _transport.ResumeAsync(ct);
    }
}
