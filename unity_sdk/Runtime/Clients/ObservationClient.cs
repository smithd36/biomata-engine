// Biomata.SDK — ObservationClient.cs
// Push per-agent world observations to the server before a tick.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Biomata.SDK.Models;
using Biomata.SDK.Transport;

namespace Biomata.SDK.Clients
{
    /// <summary>
    /// Push per-agent world observations to the server.
    ///
    /// Two usage patterns:
    ///  1. Inline in TickAsync — preferred for simple cases.
    ///  2. Pre-tick via SendAsync — preferred when observations arrive asynchronously.
    /// </summary>
    public class ObservationClient
    {
        private readonly ITransport _transport;

        internal ObservationClient(ITransport transport)
        {
            _transport = transport;
        }

        /// <summary>Push one agent's current world-state.</summary>
        public Task SendAsync(
            string agentId,
            Dictionary<string, object> observation,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(agentId))
                throw new ArgumentException("agentId is required", nameof(agentId));
            return _transport.SendObservationAsync(agentId, observation, ct);
        }

        /// <summary>Push observations for multiple agents in parallel.</summary>
        public async Task SendBatchAsync(
            IEnumerable<AgentObservationData> observations,
            CancellationToken ct = default)
        {
            if (observations == null) return;
            var tasks = new List<Task>();
            foreach (var o in observations)
            {
                if (o != null && !string.IsNullOrEmpty(o.AgentId))
                    tasks.Add(SendAsync(o.AgentId, o.Observation, ct));
            }
            await Task.WhenAll(tasks);
        }
    }
}
