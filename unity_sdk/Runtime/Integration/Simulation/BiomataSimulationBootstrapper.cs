using System;
using System.Collections;
using Biomata.SDK.Models;
using UnityEngine;

namespace Biomata.Integration
{
    /// <summary>
    /// Production-ready lifecycle manager for a Biomata simulation session.
    ///
    /// Place on any persistent GameObject. The bootstrapper finds or creates a
    /// <see cref="UnitySimulationManager"/> in the scene, owns the connection and
    /// tick lifecycle, and exposes a clean Unity-inspector-friendly API.
    ///
    /// Responsibilities:
    /// <list type="bullet">
    ///   <item>Connect / disconnect with optional auto-connect on Start.</item>
    ///   <item>Auto-tick loop with pause / resume support.</item>
    ///   <item>Manual <see cref="ForceTick"/> for HUD or test-driven ticking.</item>
    ///   <item>Automatic reconnect with configurable delay.</item>
    ///   <item>Host / port / TLS configuration surfaced in the Inspector.</item>
    ///   <item>Optional debug logging to the Unity Console.</item>
    /// </list>
    ///
    /// ── ScriptableObject config ────────────────────────────────────────────────
    ///
    /// Assign a <see cref="BiomataSimulationConfig"/> asset to the <b>Config Asset</b>
    /// slot to drive settings from a shared, version-controlled asset.
    /// Enable any <b>Override</b> toggle to replace that group's config values
    /// with the inline Inspector fields for this specific bootstrapper instance.
    /// When no config asset is assigned, all inline fields are used directly
    /// (identical to pre-Phase-4 behavior).
    ///
    /// ── Programmatic setup ────────────────────────────────────────────────────
    ///
    /// Call <see cref="Configure(string,int,float,bool,bool,bool)"/> immediately
    /// after <c>AddComponent</c> when building the bootstrapper procedurally;
    /// values are applied in Start.
    ///
    /// Subclass <see cref="UnitySimulationManager"/> and override
    /// <c>BuildWorldMetadata</c> to inject per-tick scene state without touching
    /// this class.
    /// </summary>
    [AddComponentMenu("Biomata/Simulation Bootstrapper")]
    public class BiomataSimulationBootstrapper : MonoBehaviour
    {
        // ── Config asset ──────────────────────────────────────────────────────────

        [Tooltip(
            "Optional shared config asset (Create → Biomata → Simulation Config). " +
            "When assigned, all settings are read from the asset. Enable an Override " +
            "toggle below to replace individual groups with the inline Inspector values.")]
        [SerializeField] private BiomataSimulationConfig config;

        // ── Per-group override flags ───────────────────────────────────────────────
        // These are only meaningful when a config asset is assigned.
        // When true, the inline Inspector fields for that group take precedence.

        [SerializeField] private bool overrideConnection = false;
        [SerializeField] private bool overrideSimulation = false;
        [SerializeField] private bool overrideReconnect  = false;
        [SerializeField] private bool overrideDebug      = false;

        // ── Inline Inspector fields ────────────────────────────────────────────────
        // Used directly when no config asset is assigned, or when the matching
        // override flag is enabled.

        [Header("Connection")]
        [SerializeField] private string host                  = "localhost";
        [SerializeField] private int    port                  = 8765;
        [SerializeField] private bool   useTls                = false;
        [SerializeField] private float  connectTimeoutSeconds = 10f;

        [Header("Simulation")]
        [Tooltip("Connect to the backend automatically on Start.")]
        [SerializeField] private bool  autoConnect      = true;

        [Tooltip("Begin ticking automatically once connected.")]
        [SerializeField] private bool  autoTick         = true;

        [Tooltip("Simulation ticks per second. 0 = as fast as the update loop allows.")]
        [Min(0f)]
        [SerializeField] private float tickRate          = 2f;

        [Tooltip(
            "Drive ticks from FixedUpdate (physics-synced). " +
            "Uncheck to use Update (frame-synced).")]
        [SerializeField] private bool  tickInFixedUpdate = false;

        [Header("Reconnect")]
        [Tooltip("Automatically reconnect after an unexpected disconnect.")]
        [SerializeField] private bool  autoReconnect = false;

        [Tooltip("Seconds to wait before attempting an automatic reconnect.")]
        [Min(0f)]
        [SerializeField] private float reconnectDelay = 3f;

        [Header("Debug")]
        [Tooltip("Log connection and tick lifecycle events to the Unity Console.")]
        [SerializeField] private bool debugLogging = false;

        // ── Resolved config ───────────────────────────────────────────────────────
        // Each property returns the config asset's value unless the matching
        // override flag is true (or no config is assigned).

        private bool   HasConfig         => config != null;
        private string RHost             => (HasConfig && !overrideConnection) ? config.host                  : host;
        private int    RPort             => (HasConfig && !overrideConnection) ? config.port                  : port;
        private bool   RUseTls           => (HasConfig && !overrideConnection) ? config.useTls                : useTls;
        private float  RConnectTimeout   => (HasConfig && !overrideConnection) ? config.connectTimeoutSeconds : connectTimeoutSeconds;
        private bool   RAutoConnect      => (HasConfig && !overrideSimulation) ? config.autoConnect           : autoConnect;
        private bool   RAutoTick         => (HasConfig && !overrideSimulation) ? config.autoTick              : autoTick;
        private float  RTickRate         => (HasConfig && !overrideSimulation) ? config.tickRate              : tickRate;
        private bool   RTickInFixedUpdate => (HasConfig && !overrideSimulation) ? config.tickInFixedUpdate    : tickInFixedUpdate;
        private bool   RAutoReconnect    => (HasConfig && !overrideReconnect)  ? config.autoReconnect         : autoReconnect;
        private float  RReconnectDelay   => (HasConfig && !overrideReconnect)  ? config.reconnectDelay        : reconnectDelay;
        private bool   RDebugLogging     => (HasConfig && !overrideDebug)      ? config.debugLogging          : debugLogging;

        // ── Events ────────────────────────────────────────────────────────────────

        /// <summary>Fired on the main thread once connected and health-checked.</summary>
        public event Action OnConnected;

        /// <summary>Fired on the main thread after the channel is closed cleanly.</summary>
        public event Action OnDisconnected;

        /// <summary>Fired on the main thread after every successful tick RPC.</summary>
        public event Action<TickResult> OnTickComplete;

        /// <summary>Fired on the main thread when a tick RPC fails.</summary>
        public event Action<Exception> OnTickError;

        /// <summary>Forwarded from the event stream for every engine event.</summary>
        public event Action<SimulationEvent> OnSimulationEvent;

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>
        /// The underlying simulation manager. Available after Awake.
        /// Use <c>Manager.Client</c> sub-clients (Agents, Snapshots, etc.)
        /// for operations not covered by the bootstrapper.
        /// </summary>
        public UnitySimulationManager Manager { get; private set; }

        /// <summary>True when the backend channel is open and responding.</summary>
        public bool IsConnected => Manager?.IsConnected == true;

        /// <summary>True when the auto-tick loop is running and not paused.</summary>
        public bool IsAutoTicking => _autoTicking && !_paused;

        /// <summary>True when the auto-tick loop is suspended via <see cref="SetPaused"/>.</summary>
        public bool IsPaused => _paused;

        /// <summary>Round-trip duration of the most recent tick, in milliseconds.</summary>
        public float LastTickDurationMs { get; private set; }

        /// <summary>
        /// The config asset currently driving this bootstrapper, if any.
        /// Null when running from inline Inspector values only.
        /// </summary>
        public BiomataSimulationConfig Config => config;

        /// <summary>
        /// Assign a config asset at runtime and clear all override flags.
        /// Call immediately after <c>AddComponent</c>, before <c>Start</c>.
        /// </summary>
        public void Configure(BiomataSimulationConfig simulationConfig)
        {
            config             = simulationConfig;
            overrideConnection = false;
            overrideSimulation = false;
            overrideReconnect  = false;
            overrideDebug      = false;
        }

        /// <summary>
        /// Configure connection and simulation parameters directly at runtime.
        /// Call immediately after <c>AddComponent</c>, before <c>Start</c>.
        /// Values are written into the inline fields and all override flags are
        /// set to <c>true</c> so they win over any assigned config asset.
        /// </summary>
        public void Configure(
            string host,
            int    port,
            float  tickRate     = 2f,
            bool   autoConnect  = true,
            bool   autoTick     = true,
            bool   debugLogging = false)
        {
            this.host         = host;
            this.port         = port;
            this.tickRate     = tickRate;
            this.autoConnect  = autoConnect;
            this.autoTick     = autoTick;
            this.debugLogging = debugLogging;

            // Programmatic config always wins over any assigned config asset.
            overrideConnection = true;
            overrideSimulation = true;
            overrideDebug      = true;
        }

        // ── Connection ────────────────────────────────────────────────────────────

        /// <summary>
        /// Open the backend connection. No-op if already connected.
        /// Called automatically when the resolved <c>autoConnect</c> is true.
        /// </summary>
        public void Connect()
        {
            if (IsConnected) return;
            ApplyManagerConfig();
            Log($"Connecting to {RHost}:{RPort}…");
            Manager.Connect();
        }

        /// <summary>Close the backend connection and stop ticking.</summary>
        public void Disconnect() => Manager?.Disconnect();

        /// <summary>
        /// Disconnect, wait <paramref name="delay"/> seconds, then reconnect.
        /// Pass a negative value to fall back to the resolved <c>reconnectDelay</c>.
        /// </summary>
        public void Reconnect(float delay = -1f)
        {
            StartCoroutine(ReconnectCoroutine(delay < 0f ? RReconnectDelay : delay));
        }

        // ── Tick control ──────────────────────────────────────────────────────────

        /// <summary>Enable the auto-tick loop. Resets the tick accumulator.</summary>
        public void StartAutoTick()
        {
            _autoTicking = true;
            _paused      = false;
            _tickAccum   = 0f;
            Log("Auto-tick started");
        }

        /// <summary>Disable the auto-tick loop.</summary>
        public void StopAutoTick()
        {
            _autoTicking = false;
            Log("Auto-tick stopped");
        }

        /// <summary>Suspend or resume the auto-tick loop without resetting the accumulator.</summary>
        public void SetPaused(bool paused)
        {
            _paused = paused;
            Log(paused ? "Paused" : "Resumed");
        }

        /// <summary>
        /// Fire a tick immediately, bypassing the rate timer.
        /// No-op when not connected or a tick is already in progress.
        /// </summary>
        public void ForceTick()
        {
            if (!IsConnected) return;
            _tickStartTime = Time.realtimeSinceStartup;
            Manager.ForceTick();
        }

        // ── Private state ─────────────────────────────────────────────────────────

        private bool  _autoTicking;
        private bool  _paused;
        private float _tickAccum;
        private float _tickStartTime;

        // ── Unity lifecycle ───────────────────────────────────────────────────────

        private void Awake()
        {
            Manager = UnitySimulationManager.Instance
                   ?? GetComponent<UnitySimulationManager>()
                   ?? FindFirstObjectByType<UnitySimulationManager>();

            if (Manager == null)
            {
                var go = new GameObject("UnitySimulationManager");
                Manager = go.AddComponent<UnitySimulationManager>();
            }

            // Subscribe now so events are captured regardless of when Start fires.
            // ApplyManagerConfig() is deferred to Start so that Configure() called
            // between AddComponent and Start uses the updated field values.
            Manager.OnConnected       += HandleConnected;
            Manager.OnDisconnected    += HandleDisconnected;
            Manager.OnTickComplete    += HandleTickComplete;
            Manager.OnTickError       += HandleTickError;
            Manager.OnSimulationEvent += ev => OnSimulationEvent?.Invoke(ev);
        }

        private void Start()
        {
            ApplyManagerConfig();
            if (RAutoConnect) Connect();
        }

        private void FixedUpdate()
        {
            if (RTickInFixedUpdate) AccumulateAndTick(Time.fixedDeltaTime);
        }

        private void Update()
        {
            if (!RTickInFixedUpdate) AccumulateAndTick(Time.deltaTime);
        }

        private void OnDestroy()
        {
            if (Manager == null) return;
            Manager.OnConnected       -= HandleConnected;
            Manager.OnDisconnected    -= HandleDisconnected;
            Manager.OnTickComplete    -= HandleTickComplete;
            Manager.OnTickError       -= HandleTickError;
        }

        // ── Internal ──────────────────────────────────────────────────────────────

        private void ApplyManagerConfig()
        {
            Manager.Configure(RHost, RPort, autoConnect: false);
            Manager.SetTickMode(TickMode.External);
        }

        private void AccumulateAndTick(float dt)
        {
            if (!_autoTicking || _paused || !IsConnected) return;

            _tickAccum += dt;
            float rate     = RTickRate;
            float interval = rate > 0f ? 1f / rate : float.Epsilon;
            if (_tickAccum < interval) return;

            _tickAccum     = 0f;
            _tickStartTime = Time.realtimeSinceStartup;
            Manager.ForceTick();
        }

        private IEnumerator ReconnectCoroutine(float delay)
        {
            Manager?.Disconnect();
            Log($"Reconnecting in {delay:F1}s…");
            yield return new WaitForSeconds(delay);
            Connect();
        }

        private void HandleConnected()
        {
            Log($"Connected to {RHost}:{RPort}");
            if (RAutoTick) StartAutoTick();
            OnConnected?.Invoke();
        }

        private void HandleDisconnected()
        {
            _autoTicking = false;
            _paused      = false;
            Log("Disconnected");

            if (RAutoReconnect)
                Reconnect();

            OnDisconnected?.Invoke();
        }

        private void HandleTickComplete(TickResult result)
        {
            LastTickDurationMs = (Time.realtimeSinceStartup - _tickStartTime) * 1000f;
            OnTickComplete?.Invoke(result);
        }

        private void HandleTickError(Exception ex)
        {
            Log($"Tick error: {ex?.Message}");
            OnTickError?.Invoke(ex);
        }

        private void Log(string msg)
        {
            if (RDebugLogging) Debug.Log($"[Bootstrapper] {msg}");
        }
    }
}
