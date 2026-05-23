// Biomata.SDK — BiomataManager.cs
// Singleton MonoBehaviour that owns the SimulationClient for the game's lifetime.
//
// Attach to a persistent GameObject in your bootstrap scene.
// Access via BiomataManager.Instance from any other script.
//
// Inspector-configurable connection settings; event hooks for tick_end and
// action_completed are exposed as C# events so any script can subscribe
// without direct coupling to this component.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Biomata.SDK.Clients;
using Biomata.SDK.Models;
using UnityEngine;

namespace Biomata.SDK.Unity
{
    /// <summary>
    /// Singleton MonoBehaviour wrapper around <see cref="SimulationClient"/>.
    ///
    /// Place in your bootstrap/persistent scene. All API access goes through
    /// <see cref="Instance"/>.<see cref="Client"/>.
    ///
    /// Connection is established in <c>Start()</c> when <see cref="ConnectOnStart"/> is true.
    /// The client is cleanly disconnected on <c>OnDestroy()</c>.
    /// </summary>
    [AddComponentMenu("Biomata/Biomata Manager")]
    public class BiomataManager : MonoBehaviour
    {
        // ── Singleton ─────────────────────────────────────────────────────────

        public static BiomataManager Instance { get; private set; }

        // ── Inspector fields ──────────────────────────────────────────────────

        [Header("Connection")]
        [Tooltip("Wire-level transport. WebSocket (default) is recommended for Unity 6.")]
        [SerializeField] private TransportKind transport = TransportKind.WebSocket;

        [Tooltip("Server hostname or IP.")]
        [SerializeField] private string host = "localhost";

        [Tooltip("Server port. Default 8765 for WebSocket; switch to 50051 for gRPC.")]
        [SerializeField] private int port = 8765;

        [Tooltip("Use TLS (wss:// or https://). Requires a valid server certificate. Leave false for local dev.")]
        [SerializeField] private bool useTls = false;

        [Tooltip("Seconds to wait for the server to become ready on startup.")]
        [SerializeField] private float connectTimeoutSeconds = 15f;

        [Tooltip("Per-RPC call timeout in seconds. 0 = no timeout.")]
        [SerializeField] private float defaultCallTimeoutSeconds = 30f;

        [Tooltip("Automatically connect to the server when this component starts.")]
        [SerializeField] private bool connectOnStart = true;

        [Header("Retry")]
        [SerializeField] private int retryMaxAttempts = 8;
        [SerializeField] private float retryInitialDelaySeconds = 0.5f;
        [SerializeField] private float retryMaxDelaySeconds = 30f;
        [SerializeField] private float retryMultiplier = 2f;

        [Header("Event Subscriptions")]
        [Tooltip("Subscribe to tick_end events on the event stream.")]
        [SerializeField] private bool subscribeTickEnd = true;

        [Tooltip("Subscribe to action_completed events on the event stream.")]
        [SerializeField] private bool subscribeActionCompleted = true;

        [Tooltip("Start event streaming automatically after connecting.")]
        [SerializeField] private bool autoStartEventStream = true;

        // ── C# events (subscribe from code; fired on the main thread) ─────────

        /// <summary>Raised when the connection state changes.</summary>
        public event Action<ConnectionState>   OnConnectionStateChanged;

        /// <summary>Raised on each <c>tick_end</c> event (requires <see cref="subscribeTickEnd"/>).</summary>
        public event Action<SimulationEvent>   OnTickEnd;

        /// <summary>Raised on each <c>action_completed</c> event.</summary>
        public event Action<SimulationEvent>   OnActionCompleted;

        /// <summary>Raised when the event stream disconnects (arg = exception or null).</summary>
        public event Action<Exception>         OnStreamDisconnected;

        /// <summary>Raised when the event stream exhausts reconnect attempts.</summary>
        public event Action<BiomataException>  OnStreamFailed;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>The underlying <see cref="SimulationClient"/>. Available after ConnectAsync() succeeds.</summary>
        public SimulationClient Client { get; private set; }

        /// <summary>Current connection state.</summary>
        public ConnectionState State => Client?.State ?? ConnectionState.Disconnected;

        /// <summary>True when connected to the server.</summary>
        public bool IsConnected => State == ConnectionState.Connected;

        // ── MonoBehaviour lifecycle ────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            Client = BuildClient();
            Client.OnStateChanged += state => OnConnectionStateChanged?.Invoke(state);
        }

        private async void Start()
        {
            if (connectOnStart)
            {
                try
                {
                    await ConnectAsync(destroyCancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // destroyed before connect completed — normal
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[BiomataManager] Auto-connect failed: {ex.Message}");
                }
            }
        }

        private async void OnDestroy()
        {
            Instance = null;
            if (Client != null)
            {
                try
                {
                    await Client.DisconnectAsync();
                    Client.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[BiomataManager] Error during disconnect: {ex.Message}");
                }
            }
        }

        // ── Connection helpers ────────────────────────────────────────────────

        /// <summary>
        /// Connect to the simulation server and optionally start event streaming.
        /// Safe to await from any async context or from button-click handlers.
        /// </summary>
        public async Task ConnectAsync(CancellationToken ct = default)
        {
            if (Client == null) Client = BuildClient();
            await Client.ConnectAsync(ct);

            if (autoStartEventStream)
                await StartEventStreamAsync(ct);
        }

        /// <summary>
        /// Disconnect cleanly. Stops the event stream and closes the gRPC channel.
        /// </summary>
        public async Task DisconnectAsync()
        {
            if (Client != null)
                await Client.DisconnectAsync();
        }

        // ── Event stream ──────────────────────────────────────────────────────

        /// <summary>
        /// Start the event stream after connecting. Called automatically when
        /// <see cref="autoStartEventStream"/> is true.
        /// </summary>
        public async Task StartEventStreamAsync(CancellationToken ct = default)
        {
            if (!Client.IsConnected)
                throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

            var events = Client.Events;

            events.OnDisconnected += ex => OnStreamDisconnected?.Invoke(ex);
            events.OnFailed       += ex => OnStreamFailed?.Invoke(ex);

            if (subscribeTickEnd)
                events.On("tick_end", ev => OnTickEnd?.Invoke(ev));

            if (subscribeActionCompleted)
                events.On("action_completed", ev => OnActionCompleted?.Invoke(ev));

            await events.StartAsync(ct);
        }

        // ── Convenience tick helpers ──────────────────────────────────────────

        /// <summary>
        /// Run one simulation tick with the provided per-agent observations.
        /// </summary>
        public Task<TickResult> TickAsync(
            IEnumerable<AgentObservationData> observations = null,
            Dictionary<string, object>        worldMetadata = null,
            CancellationToken                 ct = default)
        {
            EnsureConnected();
            return Client.Ticks.TickAsync(observations, worldMetadata, ct);
        }

        /// <summary>Pause the server's autonomous run() loop.</summary>
        public Task PauseAsync(CancellationToken ct = default)
        {
            EnsureConnected();
            return Client.Ticks.PauseAsync(ct);
        }

        /// <summary>Resume a paused server run() loop.</summary>
        public Task ResumeAsync(CancellationToken ct = default)
        {
            EnsureConnected();
            return Client.Ticks.ResumeAsync(ct);
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private void EnsureConnected()
        {
            if (!IsConnected)
                throw new InvalidOperationException(
                    "BiomataManager is not connected. Await ConnectAsync() first.");
        }

        private BiomataConfig BuildConfig() => new BiomataConfig
        {
            Transport                 = transport,
            Host                      = host,
            Port                      = port,
            UseTls                    = useTls,
            ConnectTimeoutSeconds     = connectTimeoutSeconds,
            DefaultCallTimeoutSeconds = defaultCallTimeoutSeconds,
            Retry = new RetryConfig
            {
                MaxAttempts          = retryMaxAttempts,
                InitialDelaySeconds  = retryInitialDelaySeconds,
                MaxDelaySeconds      = retryMaxDelaySeconds,
                Multiplier           = retryMultiplier,
            }
        };

        private SimulationClient BuildClient() => new SimulationClient(BuildConfig());
    }
}
