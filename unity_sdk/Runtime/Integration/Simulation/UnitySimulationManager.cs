using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Biomata.SDK;
using Biomata.SDK.Models;
using UnityEngine;

namespace Biomata.Integration
{
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
        [SerializeField] private string        host               = "localhost";
        [SerializeField] private int           port               = 8765;
        [SerializeField] private bool          useTls             = false;
        [SerializeField] private float         connectTimeoutSeconds = 10f;

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

        [Header("World Context")]
        [Tooltip("Optional scene name injected into every tick's world metadata.")]
        [SerializeField] private string worldName = "";

        /// <summary>
        /// Configure connection parameters at runtime (call immediately after AddComponent, before Start).
        ///
        /// Use this instead of reflection when building the manager procedurally. Values
        /// set here are read by Start() on the next frame, before any auto-connect attempt.
        /// </summary>
        public void Configure(
            string host,
            int    port,
            float  tickRate    = 2f,
            bool   autoConnect = true)
        {
            this.host        = host;
            this.port        = port;
            this.tickRate    = tickRate;
            this.autoConnect = autoConnect;
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

        /// <summary>Tick number from the most recent successful tick response.</summary>
        public int LastTick { get; private set; }

        /// <summary>Full result of the most recent tick, including all decisions.</summary>
        public TickResult LastTickResult { get; private set; }

        /// <summary>Read-only view of all currently registered agent bridges.</summary>
        public IReadOnlyList<UnityAgentBridge> RegisteredBridges => _bridges;

        // ── Private ───────────────────────────────────────────────────────────────

        private readonly List<UnityAgentBridge> _bridges = new List<UnityAgentBridge>();
        private CancellationTokenSource _cts;
        private float _timeSinceLastTick;
        private bool  _tickInProgress;

        // Reusable per-tick buffers — avoid GC allocations every tick at 500 agents.
        // GatherObservations clears and refills _observationBuffer each tick.
        private readonly List<AgentObservationData> _observationBuffer = new List<AgentObservationData>(64);
        // BuildWorldMetadata returns a reference to this dict; in-place updates avoid
        // a fresh Dictionary allocation per tick.
        private readonly Dictionary<string, object> _metadataBuffer = new Dictionary<string, object>(4);

        private float TickInterval => tickRate > 0f ? 1f / tickRate : float.Epsilon;

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
            if (autoConnect)
                Connect();
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
            if (!IsConnected || _tickInProgress) return;

            _timeSinceLastTick += dt;
            if (_timeSinceLastTick < TickInterval) return;

            _timeSinceLastTick = 0f;
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

            var config = new BiomataConfig
            {
                Host                  = host,
                Port                  = port,
                UseTls                = useTls,
                ConnectTimeoutSeconds = connectTimeoutSeconds,
            };

            Client = new SimulationClient(config);
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

            // Subscribe to real-time events before the first tick.
            Client.Events.OnAll(ev => OnSimulationEvent?.Invoke(ev));
            _ = Client.Events.StartAsync(_cts.Token);

            OnConnected?.Invoke();
            Debug.Log($"[Biomata] Connected to {host}:{port}");
        }

        private IEnumerator DisconnectCoroutine()
        {
            if (Client == null) yield break;

            var task = Client.DisconnectAsync();
            while (!task.IsCompleted)
                yield return null;

            Client.Dispose();
            Client = null;
            _cts?.Cancel();

            OnDisconnected?.Invoke();
            Debug.Log("[Biomata] Disconnected");
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
    }
}
