// Biomata.SDK — HealthClient.cs
// Liveness probe against the biomata-engine server.

using System;
using System.Threading;
using System.Threading.Tasks;
using Biomata.SDK.Models;
using Biomata.SDK.Transport;

namespace Biomata.SDK.Clients
{
    /// <summary>
    /// Sends health-check probes to verify the server is alive and responsive.
    /// Transport-agnostic — delegates to ITransport which may be backed by
    /// WebSocket or gRPC depending on BiomataConfig.Transport.
    /// </summary>
    public class HealthClient
    {
        private readonly ITransport _transport;

        internal HealthClient(ITransport transport)
        {
            _transport = transport;
        }

        /// <summary>
        /// Send one health-check call and return the server status.
        /// </summary>
        /// <exception cref="BiomataException">Thrown on transport error.</exception>
        public Task<HealthStatus> CheckAsync(
            CancellationToken ct             = default,
            float             timeoutSeconds = 0f)
        {
            // timeoutSeconds is kept for API parity with the old gRPC client.
            // Each transport applies its own per-call deadline from BiomataConfig.
            return _transport.HealthCheckAsync(ct);
        }

        /// <summary>
        /// Poll the server until it responds "ok" or the timeout elapses.
        /// Useful after starting the server before the first tick.
        /// </summary>
        public async Task<HealthStatus> WaitUntilReadyAsync(
            float             timeoutSeconds  = 30f,
            float             intervalSeconds = 1f,
            CancellationToken ct              = default)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
            Exception lastEx = null;
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                try
                {
                    var status = await CheckAsync(ct, intervalSeconds);
                    if (status.IsOk) return status;
                }
                catch (Exception ex) when (ex is BiomataException || ex is OperationCanceledException)
                {
                    lastEx = ex;
                }
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
            }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException(
                $"Server did not become ready within {timeoutSeconds}s. Last error: {lastEx?.Message}");
        }
    }
}
