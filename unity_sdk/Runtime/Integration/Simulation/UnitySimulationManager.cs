using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Biomata.Integration.Simulation;
using Biomata.SDK;
using Biomata.SDK.Clients;
using Biomata.SDK.Models;
using UnityEngine;

namespace Biomata.Integration
{
    /// <summary>
    /// Controls who is responsible for advancing the simulation tick.
    /// </summary>
    public enum TickMode
    {
        /// <summary>
        /// The <see cref="UnitySimulationManager"/> drives its own tick accumulator
        /// from FixedUpdate / Update. Default behavior when used without a bootstrapper.
        /// </summary>
        Internal,

        /// <summary>
        /// An external owner (e.g. <see cref="BiomataSimulationBootstrapper"/>) drives
        /// ticks via <see cref="UnitySimulationManager.ForceTick"/>. The USM's internal
        /// accumulator is completely bypassed — not just slowed down.
        /// </summary>
        External,
    }

    /// <summary>
    /// Central coordinator for NPC simulation. Place exactly one in your scene.
    ///
    /// Responsibilities:
    /// <list type="bullet">
    ///   <item>Manages the WebSocket connection lifecycle.</item>
    ///   <item>Drives simulation ticks from FixedUpdate (or Update) at a configurable rate.</item>
    ///   <item>Collects observations from all registered <see cref="UnityAgentBridge"/> components.</item>
    ///   <item>Distributes backend decisions to the appropriate bridge after each tick.</item>
    ///   <item>Forwards the real-time event stream as Unity events.</item>
    /// </list>
    ///
    /// Access via the <see cref="Instance"/> singleton. The GameObject persists across scenes.
    ///
    /// Subclass and override <see cref="BuildWorldMetadata"/> to inject per-tick scene state
    /// (weather, time of day, active quests, etc.) without modifying this class.
    /// </summary>
    [AddComponentMenu("Biomata/Simulation Manager")]
    public class UnitySimulationManager : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────────

        [Header("Connection")]
        [SerializeField] private string host                  = "localhost";
        [SerializeField] private int    port                  = 8765;
        [SerializeField] private bool   useTls                = false;
        [SerializeField] private float  connectTimeoutSeconds = 10f;

        [Tooltip("Default per-call deadline in seconds. 0 = no deadline.")]
        [SerializeField] private float  defaultCallTimeoutSeconds = 30f;

        [Header("Retry")]
        [SerializeField] private int   retryMaxAttempts         = 8;
        [SerializeField] private float retryInitialDelaySeconds = 0.5f;
        [SerializeField] private float retryMaxDelaySeconds     = 30f;
        [SerializeField] private float retryMultiplier          = 2f;

        [Header("Event Subscriptions")]
        [Tooltip("Subscribe to tick_end events on the event stream.")]
        [SerializeField] private bool subscribeTickEnd = true;

        [Tooltip("Subscribe to action_completed events on the event stream.")]
        [SerializeField] private bool subscribeActionCompleted = true;

        [Tooltip("Start event streaming automatically after connecting.")]
        [SerializeField] private bool autoStartEventStream = true;

        [Header("Simulation")]
        [Tooltip("Connect to the backend automatically on scene start.")]
        [SerializeField] private bool autoConnect = true;

        [Tooltip(
            "Drive ticks from FixedUpdate (physics-synced). " +
            "Uncheck to use Update (frame-synced).")]
        [SerializeField] private bool tickInFixedUpdate = true;

        [Tooltip("Simulation ticks per second. Set to 0 to fire on every FixedUpdate/Update.")]
        [Min(0f)]
        [SerializeField] private float tickRate = 2f;

        [Header("Reconnect")]
        [Tooltip("Automatically reconnect after an unexpected disconnect (standalone use without Bootstrapper).")]
        [SerializeField] private bool  autoReconnect  = false;
        [Tooltip("Seconds to wait before reattempting connection after a drop.")]
        [Min(0f)]
        [SerializeField] private float reconnectDelay = 3f;

        [Header("World Context")]
        [Tooltip("Optional scene name injected into every tick's world metadata.")]
        [SerializeField] private string worldName = "";

        /// <summary>
        /// Configure connection parameters at runtime (call immediately after AddComponent, before Start).
        ///
        /// When <see cref="BiomataSimulationBootstrapper"/> is present it calls this in its own
        /// Awake — before USM.Start() fires — so USM never sees stale inspector values.
        /// </summary>
        public void Configure(
            string host,
            int    port,
            bool   useTls                    = false,
            float  connectTimeoutSeconds     = 10f,
            float  defaultCallTimeoutSeconds = 30f,
            int    retryMaxAttempts          = 8,
            float  retryInitialDelaySeconds  = 0.5f,
            float  retryMaxDelaySeconds      = 30f,
            float  retryMultiplier           = 2f,
            float  tickRate                  = 2f,
            bool   tickInFixedUpdate         = true,
            bool   autoConnect               = true)
        {
            this.host                      = host;
            this.port                      = port;
            this.useTls                    = useTls;
            this.connectTimeoutSeconds     = connectTimeoutSeconds;
            this.defaultCallTimeoutSeconds = defaultCallTimeoutSeconds;
            this.retryMaxAttempts          = retryMaxAttempts;
            this.retryInitialDelaySeconds  = retryInitialDelaySeconds;
            this.retryMaxDelaySeconds      = retryMaxDelaySeconds;
            this.retryMultiplier           = retryMultiplier;
            this.tickRate                  = tickRate;
            this.tickInFixedUpdate         = tickInFixedUpdate;
            this.autoConnect               = autoConnect;
        }

        // ── Events ────────────────────────────────────────────────────────────────

        /// <summary>Fired on the main thread after every successful tick RPC.</summary>
        public event Action<TickResult> OnTickComplete;

        /// <summary>Fired on the main thread when a tick RPC fails.</summary>
        public event Action<Exception> OnTickError;

        /// <summary>Fired once connected and health-checked.</summary>
        public event Action OnConnected;

        /// <summary>Fired after the channel is closed cleanly.</summary>
        public event Action OnDisconnected;

        /// <summary>Forwarded from the event stream for every engine event.</summary>
        public event Action<SimulationEvent> OnSimulationEvent;

        /// <summary>Raised on each <c>tick_end</c> event (requires <see cref="subscribeTickEnd"/>).</summary>
        public event Action<SimulationEvent> OnTickEnd;

        /// <summary>Raised on each <c>action_completed</c> event (requires <see cref="subscribeActionCompleted"/>).</summary>
        public event Action<SimulationEvent> OnActionCompleted;

        /// <summary>Raised when the event stream disconnects unexpectedly.</summary>
        public event Action<Exception> OnStreamDisconnected;

        /// <summary>Raised when the event stream exhausts reconnect attempts.</summary>
        public event Action<BiomataException> OnStreamFailed;

        /// <summary>
        /// Fired on the main thread immediately before a tick coroutine starts.
        /// Subscribe here (e.g. in <see cref="BiomataSimulationBootstrapper"/>) to
        /// timestamp the start of each tick for latency measurement.
        /// </summary>
        public event Action OnTickStarted;

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>The active singleton, or <c>null</c> before first Awake.</summary>
        public static UnitySimulationManager Instance { get; private set; }

        /// <summary>
        /// The underlying client. Available after a successful <see cref="Connect"/>.
        /// Use sub-clients (<c>Client.Agents</c>, <c>Client.Snapshots</c>, etc.) for
        /// operations not covered by the integration layer.
        /// </summary>
        public SimulationClient Client { get; private set; }

        /// <summary>True when the channel is open and the server is responding.</summary>
        public bool IsConnected => Client?.IsConnected == true;

        /// <summary>True when the internal tick loop is enabled and not paused.</summary>
        public bool IsAutoTicking => _autoTicking && !_paused;

        /// <summary>True when ticks are temporarily suppressed via <see cref="SetPaused"/>.</summary>
        public bool IsPaused => _paused;

        /// <summary>Tick number from the most recent successful tick response.</summary>
        public int LastTick { get; private set; }

        /// <summary>Full result of the most recent tick, including all decisions.</summary>
        public TickResult LastTickResult { get; private set; }

        /// <summary>Read-only view of all currently registered agent bridges.</summary>
        public IReadOnlyList<UnityAgentBridge> RegisteredBridges => _bridges;

        // ── Private ───────────────────────────────────────────────────────────────

        private readonly List<UnityAgentBridge> _bridges = new List<UnityAgentBridge>();
        private CancellationTokenSource _cts;
        private TickAccumulator _tickAccum;
        private bool     _tickInProgress;
        private bool     _autoTicking = true;
        private bool     _paused      = false;
        private TickMode _tickMode    = TickMode.Internal;

        // Reusable per-tick buffers — avoid GC allocations every tick at 500 agents.
        // GatherObservations clears and refills _observationBuffer each tick.
        private readonly List<AgentObservationData> _observationBuffer = new List<AgentObservationData>(64);
        // BuildWorldMetadata returns a reference to this dict; in-place updates avoid
        // a fresh Dictionary allocation per tick.
        private readonly Dictionary<string, object> _metadataBuffer = new Dictionary<string, object>(4);

        // ── Lifecycle ─────────────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            // BiomataSimulationBootstrapper.ApplyManagerConfig() runs in its Awake —
            // which Unity guarantees completes before any Start() fires — and passes
            // autoConnect:false via Configure(). So if BSB is present, this is a no-op.
            if (autoConnect) Connect();
        }

        private void FixedUpdate()
        {
            if (tickInFixedUpdate)
                AccumulateAndTick(Time.fixedDeltaTime);
        }

        private void Update()
        {
            if (!tickInFixedUpdate)
                AccumulateAndTick(Time.deltaTime);
        }

        private void AccumulateAndTick(float dt)
        {
            if (_tickMode == TickMode.External) return;
            if (!IsConnected || _tickInProgress) return;
            if (!_autoTicking || _paused) return;
            if (_tickAccum.Advance(dt, tickRate))
                StartCoroutine(TickCoroutine());
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;

            _cts?.Cancel();
            _cts?.Dispose();
            Client?.Dispose();
        }

        // ── Connection ────────────────────────────────────────────────────────────

        private BiomataConfig BuildConfig() => BiomataConfig.FromInspector(
            host, port, useTls, connectTimeoutSeconds, defaultCallTimeoutSeconds,
            new RetryConfig
            {
                MaxAttempts         = retryMaxAttempts,
                InitialDelaySeconds = retryInitialDelaySeconds,
                MaxDelaySeconds     = retryMaxDelaySeconds,
                Multiplier          = retryMultiplier,
            });

        private void WireEventStream(CancellationToken ct)
        {
            var events = Client.Events;
            events.OnDisconnected += ex => OnStreamDisconnected?.Invoke(ex);
            events.OnFailed       += ex => OnStreamFailed?.Invoke(ex);
            events.OnAll(ev => OnSimulationEvent?.Invoke(ev));
            if (autoStartEventStream)
            {
                if (subscribeTickEnd)
                    events.On("tick_end", ev => OnTickEnd?.Invoke(ev));
                if (subscribeActionCompleted)
                    events.On("action_completed", ev => OnActionCompleted?.Invoke(ev));
            }
            _ = events.StartAsync(ct);
        }

        /// <summary>
        /// Open the backend connection. Called automatically when <see cref="autoConnect"/> is true.
        /// Safe to call manually; no-op if already connected.
        /// </summary>
        public void Connect()
        {
            if (IsConnected) return;
            StartCoroutine(ConnectCoroutine());
        }

        /// <summary>Close the backend connection and stop ticking.</summary>
        public void Disconnect() => StartCoroutine(DisconnectCoroutine());

        private IEnumerator ConnectCoroutine()
        {
            _cts = new CancellationTokenSource();

            Client = new SimulationClient(BuildConfig());
            Client.OnStateChanged += s => Debug.Log($"[Biomata] {s}");

            var task = Client.ConnectAsync(_cts.Token);
            while (!task.IsCompleted)
                yield return null;

            if (task.IsFaulted)
            {
                Debug.LogError(
                    $"[Biomata] Connection to {host}:{port} failed: " +
                    task.Exception?.GetBaseException().Message);
                Client.Dispose();
                Client = null;
                yield break;
            }

            // Fetch role definitions from the backend before firing OnConnected so that
            // agents can resolve role defaults (capabilities, brain class) synchronously
            // during their registration coroutines.
            var rolesTask = Client.Roles.ListAsync(_cts.Token);
            while (!rolesTask.IsCompleted)
                yield return null;

            if (!rolesTask.IsFaulted && rolesTask.Result != null)
                RoleManifestLoader.Populate(rolesTask.Result);
            else if (rolesTask.IsFaulted)
                Debug.LogWarning(
                    "[Biomata] roles.list RPC failed — restart the backend to pick up the latest sim.yaml, " +
                    "then reconnect. Agents that rely on role defaults will not register until the manifest " +
                    "is available. Error: " + rolesTask.Exception?.GetBaseException().Message);

            WireEventStream(_cts.Token);

            OnConnected?.Invoke();
            Debug.Log($"[Biomata] Connected to {host}:{port}");
        }

        /// <summary>
        /// Async variant of <see cref="Connect"/>. Awaitable from any async context or
        /// button-click handlers. Connects, fetches roles, and wires the event stream.
        /// </summary>
        public async Task ConnectAsync(CancellationToken ct = default)
        {
            if (IsConnected) return;
            _cts = new CancellationTokenSource();
            Client = new SimulationClient(BuildConfig());
            Client.OnStateChanged += s => Debug.Log($"[Biomata] {s}");
            try
            {
                await Client.ConnectAsync(ct);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Biomata] Connection to {host}:{port} failed: {ex.Message}");
                Client.Dispose();
                Client = null;
                return;
            }
            var rolesResult = await Client.Roles.ListAsync(ct);
            if (rolesResult != null) RoleManifestLoader.Populate(rolesResult);
            WireEventStream(ct);
            OnConnected?.Invoke();
            Debug.Log($"[Biomata] Connected to {host}:{port}");
        }

        /// <summary>Async variant of <see cref="Disconnect"/>. Awaitable from any async context.</summary>
        public async Task DisconnectAsync()
        {
            if (Client == null) return;
            _cts?.Cancel();          // signal all pending async ops first
            await Client.DisconnectAsync();
            Client.Dispose();
            Client = null;
            _cts?.Dispose();         // release the token source
            _cts = null;
            NotifyDisconnected();
        }

        private IEnumerator DisconnectCoroutine()
        {
            if (Client == null) yield break;
            _cts?.Cancel();          // signal all pending async ops first
            var task = Client.DisconnectAsync();
            while (!task.IsCompleted)
                yield return null;
            Client.Dispose();
            Client = null;
            _cts?.Dispose();         // release the token source
            _cts = null;
            NotifyDisconnected();
        }

        private void NotifyDisconnected()
        {
            _autoTicking = false;
            _tickAccum.Reset();
            OnDisconnected?.Invoke();
            Debug.Log("[Biomata] Disconnected");
            if (autoReconnect && _tickMode != TickMode.External)
                StartCoroutine(ReconnectCoroutine());
        }

        private IEnumerator ReconnectCoroutine()
        {
            Debug.Log($"[Biomata] Reconnecting in {reconnectDelay:F1}s…");
            yield return new WaitForSeconds(reconnectDelay);
            Connect();
        }

        // ── Bridge registry ───────────────────────────────────────────────────────

        internal void RegisterBridge(UnityAgentBridge bridge)
        {
            if (!_bridges.Contains(bridge))
                _bridges.Add(bridge);
        }

        internal void UnregisterBridge(UnityAgentBridge bridge) => _bridges.Remove(bridge);

        // ── Tick ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Controls who drives the tick accumulator.
        ///
        /// <see cref="TickMode.Internal"/>: USM fires ticks from its own FixedUpdate / Update loop
        /// at the configured <c>tickRate</c>. Default for standalone use.<br/>
        /// <see cref="TickMode.External"/>: <see cref="BiomataSimulationBootstrapper"/> calls
        /// <see cref="ForceTick"/> directly; the internal loop is bypassed. USM also skips
        /// its <c>autoConnect</c> logic in Start so BSB owns the connection lifecycle too.
        /// </summary>
        public void SetTickMode(TickMode mode) => _tickMode = mode;

        /// <summary>
        /// Enable or disable the automatic tick loop.
        /// When <paramref name="enabled"/> is false the accumulator is reset so the next
        /// <c>StartAutoTick</c> begins cleanly at zero elapsed time.
        /// </summary>
        public void SetAutoTick(bool enabled)
        {
            _autoTicking = enabled;
            if (!enabled) _tickAccum.Reset();
        }

        /// <summary>
        /// Pause or resume the tick loop without resetting the accumulator.
        /// Has no effect when in <see cref="TickMode.External"/> mode.
        /// </summary>
        public void SetPaused(bool paused) => _paused = paused;

        /// <summary>Change the tick rate at runtime (ticks per second). 0 = every frame.</summary>
        public void SetTickRate(float rate) => tickRate = rate;

        /// <summary>
        /// Fire a tick immediately, bypassing the rate timer.
        /// No-op when a tick is already in progress.
        /// </summary>
        public void ForceTick()
        {
            if (!_tickInProgress && IsConnected)
                StartCoroutine(TickCoroutine());
        }

        private IEnumerator TickCoroutine()
        {
            _tickInProgress = true;
            OnTickStarted?.Invoke();
            try
            {
                var observations = GatherObservations();
                var metadata     = BuildWorldMetadata();

                var task = Client.Ticks.TickAsync(observations, metadata, _cts.Token);
                while (!task.IsCompleted)
                    yield return null;

                if (task.IsFaulted)
                {
                    var ex = task.Exception?.GetBaseException();
                    Debug.LogWarning($"[Biomata] Tick failed: {ex?.Message}");
                    OnTickError?.Invoke(ex);
                }
                else
                {
                    LastTickResult = task.Result;
                    LastTick       = LastTickResult.Tick;
                    DistributeDecisions(LastTickResult);
                    OnTickComplete?.Invoke(LastTickResult);
                }
            }
            finally
            {
                _tickInProgress = false;
            }
        }

        // ── Observation / decision helpers ────────────────────────────────────────

        private List<AgentObservationData> GatherObservations()
        {
            // Reuse the same List instance every tick to keep GC quiet at scale.
            // TickClient consumes the list synchronously inside this coroutine
            // (it iterates `agentObservations` to build the JSON request and
            // does not retain a reference), so reuse is safe.
            _observationBuffer.Clear();
            if (_observationBuffer.Capacity < _bridges.Count)
                _observationBuffer.Capacity = _bridges.Count;

            foreach (var bridge in _bridges)
            {
                if (bridge == null || !bridge.isActiveAndEnabled) continue;
                try
                {
                    _observationBuffer.Add(bridge.BuildObservation());
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        $"[Biomata] Observation error on '{bridge.AgentId}': {ex.Message}");
                }
            }
            return _observationBuffer;
        }

        private void DistributeDecisions(TickResult result)
        {
            foreach (var bridge in _bridges)
            {
                if (bridge == null || !bridge.isActiveAndEnabled) continue;
                var decision = result.ForAgent(bridge.AgentId);
                if (decision != null)
                    bridge.ApplyDecision(decision);
            }
        }

        /// <summary>
        /// Override to inject per-tick scene state into the world metadata dict.
        /// The base implementation includes <c>unity_time</c>, <c>unity_frame</c>,
        /// and <c>world</c> (from the Inspector field).
        ///
        /// Performance note: the base implementation reuses a single dictionary
        /// across ticks and mutates it in place. Overrides should follow the same
        /// pattern — call <c>base.BuildWorldMetadata()</c> first and add additional
        /// keys to the returned dict. The dict is consumed synchronously inside
        /// the tick coroutine and is safe to reuse afterward.
        /// </summary>
        protected virtual Dictionary<string, object> BuildWorldMetadata()
        {
            _metadataBuffer["unity_time"]  = (double)Time.time;
            _metadataBuffer["unity_frame"] = Time.frameCount;
            if (!string.IsNullOrEmpty(worldName))
                _metadataBuffer["world"] = worldName;
            else
                _metadataBuffer.Remove("world");
            return _metadataBuffer;
        }

        // ── Async convenience helpers ─────────────────────────────────────────────

        /// <summary>
        /// Run one simulation tick. Gathers observations from all registered bridges and
        /// optionally merges additional <paramref name="extraObservations"/> (e.g. off-scene agents).
        /// </summary>
        public Task<TickResult> TickAsync(
            IEnumerable<AgentObservationData> extraObservations = null,
            Dictionary<string, object>        worldMetadata     = null,
            CancellationToken                 ct                = default)
        {
            if (!IsConnected)
                throw new InvalidOperationException("Not connected. Call ConnectAsync first.");
            var obs = GatherObservations();
            if (extraObservations != null)
                foreach (var o in extraObservations) obs.Add(o);
            return Client.Ticks.TickAsync(obs, worldMetadata ?? BuildWorldMetadata(), ct);
        }

        /// <summary>Pause the server's autonomous run() loop.</summary>
        public Task PauseAsync(CancellationToken ct = default)
        {
            if (!IsConnected)
                throw new InvalidOperationException("Not connected. Call ConnectAsync first.");
            return Client.Ticks.PauseAsync(ct);
        }

        /// <summary>Resume a paused server run() loop.</summary>
        public Task ResumeAsync(CancellationToken ct = default)
        {
            if (!IsConnected)
                throw new InvalidOperationException("Not connected. Call ConnectAsync first.");
            return Client.Ticks.ResumeAsync(ct);
        }
    }
}
