// Biomata.SDK — ITransport.cs
//
// Internal wire-level transport contract. The concrete implementation is
// WebSocketTransport (JSON over System.Net.WebSockets).
//
// The sub-clients (HealthClient, AgentClient, …) depend ONLY on this interface.
// All methods are async and accept a CancellationToken. Errors are surfaced as
// BiomataException so callers don't need transport-specific catch blocks.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Biomata.SDK.Models;

namespace Biomata.SDK.Transport
{
    /// <summary>
    /// Wire-level transport contract. Surfaces every method the Python SimulationSession
    /// exposes plus the asynchronous event stream. Implemented by WebSocketTransport.
    /// </summary>
    internal interface ITransport : IAsyncDisposable
    {
        // ── Connection lifecycle ──────────────────────────────────────────────

        ConnectionState                State        { get; }
        bool                           IsConnected  { get; }
        event Action<ConnectionState>  OnStateChanged;

        Task ConnectAsync(CancellationToken ct = default);
        Task DisconnectAsync();

        // ── Methods ───────────────────────────────────────────────────────────

        Task<HealthStatus>  HealthCheckAsync(CancellationToken ct = default);
        Task                RegisterAgentAsync(AgentRegistration registration, CancellationToken ct = default);
        Task                RemoveAgentAsync(string agentId, CancellationToken ct = default);
        Task                SendObservationAsync(string agentId, Dictionary<string, object> observation, CancellationToken ct = default);
        Task<TickResult>    TickAsync(IEnumerable<AgentObservationData> observations, Dictionary<string, object> metadata, CancellationToken ct = default);
        Task<string>        PauseAsync(CancellationToken ct = default);
        Task<string>        ResumeAsync(CancellationToken ct = default);
        Task<SnapshotData>  SnapshotAsync(CancellationToken ct = default);
        Task<int>           RestoreAsync(byte[] snapshotData, CancellationToken ct = default);
        Task<RolesData>     RolesListAsync(CancellationToken ct = default);
        Task<ManifestData>  ActionsListAsync(CancellationToken ct = default);

        // ── Event stream ──────────────────────────────────────────────────────

        /// <summary>
        /// Begin receiving server-pushed events. Idempotent — re-calling with
        /// a different filter updates the filter on the same subscription.
        /// </summary>
        Task SubscribeEventsAsync(IEnumerable<string> eventTypeFilter, CancellationToken ct = default);

        /// <summary>Stop the event flow. Safe to call when not subscribed.</summary>
        Task UnsubscribeEventsAsync(CancellationToken ct = default);

        /// <summary>Raised on every event from the server. Always invoked on a thread-pool thread.</summary>
        event Action<SimulationEvent>  OnEvent;

        /// <summary>Raised when the event stream disconnects (arg: exception or null on clean shutdown).</summary>
        event Action<Exception>        OnEventStreamDisconnected;
    }
}
