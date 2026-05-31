// Biomata.SDK — SimulationClient.cs
// Top-level client. Owns the WebSocket transport and exposes the sub-clients.
//
// Lifecycle
// ─────────
//   var client = new SimulationClient(config);
//   await client.ConnectAsync(ct);            // opens transport, verifies server
//
//   await client.Agents.RegisterAsync(…);
//   var result = await client.Ticks.TickAsync(…);
//   await client.Events.StartAsync(ct);
//
//   await client.DisconnectAsync();           // stops stream, drains, closes socket

using System;
using System.Threading;
using System.Threading.Tasks;
using Biomata.SDK.Clients;
using Biomata.SDK.Transport;

namespace Biomata.SDK
{
    /// <summary>
    /// Top-level client for the biomata-engine backend.
    ///
    /// Call <see cref="ConnectAsync"/> once after construction; all sub-clients
    /// are then available via properties. The underlying transport is selected
    /// from <see cref="BiomataConfig.Transport"/> at connect time.
    /// </summary>
    public class SimulationClient : IAsyncDisposable, IDisposable
    {
        private readonly BiomataConfig _config;
        private ITransport _transport;

        private volatile ConnectionState _state = ConnectionState.Disconnected;
        private readonly object _stateLock = new object();

        // ── Sub-clients ───────────────────────────────────────────────────────

        /// <summary>Liveness probes.</summary>
        public HealthClient      Health        { get; private set; }

        /// <summary>Register / remove agents.</summary>
        public AgentClient       Agents        { get; private set; }

        /// <summary>Pre-tick observation push.</summary>
        public ObservationClient Observations  { get; private set; }

        /// <summary>Run ticks, pause, resume.</summary>
        public TickClient        Ticks         { get; private set; }

        /// <summary>Real-time event subscriptions.</summary>
        public EventStreamClient Events        { get; private set; }

        /// <summary>Save / restore / persist simulation state.</summary>
        public SnapshotClient    Snapshots     { get; private set; }

        /// <summary>Retrieve role definitions declared in the backend sim.yaml.</summary>
        public RolesClient       Roles         { get; private set; }

        // ── State ─────────────────────────────────────────────────────────────

        public ConnectionState State
        {
            get => _state;
            private set
            {
                lock (_stateLock) _state = value;
                OnStateChanged?.Invoke(value);
            }
        }

        public bool IsConnected => _state == ConnectionState.Connected;

        public Exception LastError { get; private set; }

        public event Action<ConnectionState> OnStateChanged;

        // ── Construction ──────────────────────────────────────────────────────

        public SimulationClient(BiomataConfig config = null)
        {
            _config = config ?? new BiomataConfig();
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            if (_state == ConnectionState.Connected)
                throw new InvalidOperationException("Already connected. Call DisconnectAsync() first.");

            State = ConnectionState.Connecting;
            try
            {
                _transport = BuildTransport(_config);
                _transport.OnStateChanged += s => State = s;

                await _transport.ConnectAsync(ct);

                // Wire up sub-clients now that the transport is ready.
                Health       = new HealthClient(_transport);
                Agents       = new AgentClient(_transport);
                Observations = new ObservationClient(_transport);
                Ticks        = new TickClient(_transport);
                Events       = new EventStreamClient(_transport);
                Snapshots    = new SnapshotClient(_transport);
                Roles        = new RolesClient(_transport);

                // Verify the server is responding to RPCs (not just TCP-connected).
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(_config.ConnectTimeout);
                await Health.WaitUntilReadyAsync(
                    timeoutSeconds  : _config.ConnectTimeoutSeconds,
                    intervalSeconds : 1f,
                    ct              : connectCts.Token
                );

                State = ConnectionState.Connected;
            }
            catch (Exception ex)
            {
                LastError = ex;
                State     = ConnectionState.Faulted;
                await CleanupAsync();
                throw ex is BiomataException
                    ? ex
                    : new BiomataException($"ConnectAsync to {_config.Address} failed: {ex.Message}", ex);
            }
        }

        public async Task DisconnectAsync()
        {
            if (_state is ConnectionState.Disconnected or ConnectionState.Disconnecting)
                return;

            State = ConnectionState.Disconnecting;
            if (Events != null)
            {
                try { await Events.StopAsync(); } catch { /* best-effort */ }
            }
            await CleanupAsync();
            State = ConnectionState.Disconnected;
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync();
        }

        public void Dispose()
        {
            _ = DisconnectAsync();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private async Task CleanupAsync()
        {
            if (_transport != null)
            {
                try { await _transport.DisconnectAsync(); } catch { /* ignore */ }
                try { await _transport.DisposeAsync(); }    catch { /* ignore */ }
                _transport = null;
            }
        }

        private static ITransport BuildTransport(BiomataConfig config)
            => new WebSocketTransport(config);
    }
}
