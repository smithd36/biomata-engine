// Biomata.SDK — AgentClient.cs
// Register / remove agents in the running simulation.

using System;
using System.Threading;
using System.Threading.Tasks;
using Biomata.SDK.Models;
using Biomata.SDK.Transport;

namespace Biomata.SDK.Clients
{
    /// <summary>
    /// Manages the lifecycle of agents in the running simulation.
    /// </summary>
    public class AgentClient
    {
        private readonly ITransport _transport;

        internal AgentClient(ITransport transport)
        {
            _transport = transport;
        }

        /// <summary>Register a new agent.</summary>
        public Task RegisterAsync(AgentRegistration registration, CancellationToken ct = default)
        {
            if (registration == null) throw new ArgumentNullException(nameof(registration));
            if (string.IsNullOrEmpty(registration.AgentId))
                throw new ArgumentException("AgentId is required", nameof(registration));
            if (string.IsNullOrEmpty(registration.BrainClass))
                throw new ArgumentException("BrainClass is required", nameof(registration));
            return _transport.RegisterAgentAsync(registration, ct);
        }

        /// <summary>Remove an agent from the running simulation.</summary>
        public Task RemoveAsync(string agentId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(agentId))
                throw new ArgumentException("agentId is required", nameof(agentId));
            return _transport.RemoveAgentAsync(agentId, ct);
        }

        /// <summary>Remove an agent without throwing if it doesn't exist.</summary>
        public async Task<bool> TryRemoveAsync(string agentId, CancellationToken ct = default)
        {
            try { await RemoveAsync(agentId, ct); return true; }
            catch (BiomataException) { return false; }
        }
    }
}
