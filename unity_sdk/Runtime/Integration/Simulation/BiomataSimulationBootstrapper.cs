using System;
using System.Collections;
using Biomata.SDK.Models;
using UnityEngine;

namespace Biomata.Integration
{
    /// <summary>
    /// Production-ready lifecycle controller for a Biomata simulation session.
    ///
    /// Place on any persistent GameObject alongside (or without) a
    /// <see cref="UnitySimulationManager"/>. The bootstrapper locates or creates a USM,
    /// forwards <em>all</em> connection configuration to it in Awake, and delegates tick
    /// scheduling, pause / resume, and event forwarding to the USM — so there is exactly
    /// one connection manager, one tick driver, and one event dispatcher at runtime.
    ///
    /// ── Division of responsibility ─────────────────────────────────────────────
    ///
    /// | Concern              | Owner  |
    /// |----------------------|--------|
    /// | SimulationClient     | USM    |
    /// | Tick accumulator     | USM    |
    /// | Event stream wiring  | USM    |
    /// | Bridge registry      | USM    |
    /// | Connection config    | BSB → USM (forwarded in Awake) |
    /// | Auto-reconnect       | BSB    |
    /// | Tick pause / resume  | BSB → USM (via SetPaused / SetAutoTick) |
    /// | ScriptableObject cfg | BSB    |
    ///
    /// ── ScriptableObject config ────────────────────────────────────────────────
    ///
    /// Assign a <see cref="BiomataSimulationConfig"/> asset to the <b>Config Asset</b>
    /// slot to drive settings from a shared, version-controlled asset.
    /// Enable any <b>Override</b> toggle to replace that group's config values with the
    /// inline Inspector fields for this specific bootstrapper instance.
    /// When no config asset is assigned, all inline fields are used directly.
    ///
    /// ── Programmatic setup ────────────────────────────────────────────────────
    ///
    /// Call <see cref="Configure(string,int,float,bool,bool,bool)"/> immediately after
    /// <c>AddComponent</c>, before <c>Start</c>; values take effect in Awake.
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

        [SerializeField] private bool overrideConnection = false;
        [SerializeField] private bool overrideSimulation = false;
        [SerializeField] private bool overrideReconnect  = false;
        [SerializeField] private bool overrideRetry      = false;
        [SerializeField] private bool overrideDebug      = false;

        // ── Inline Inspector fields ────────────────────────────────────────────────

        [Header("Connection")]
        [SerializeField] private string host                  = "localhost";
        [SerializeField] private int    port                  = 8765;
        [SerializeField] private bool   useTls                = false;
        [SerializeField] private float  connectTimeoutSeconds = 10f;

        [Header("Retry")]
        [SerializeField] private int   retryMaxAttempts         = 8;
        [SerializeField] private float retryInitialDelaySeconds = 0.5f;
        [SerializeField] private float retryMaxDelaySeconds     = 30f;
        [SerializeField] private float retryMultiplier          = 2f;

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
        // override flag is true (or no config asset is assigned).

        private bool   HasConfig          => config != null;
        private string RHost              => (HasConfig && !overrideConnection) ? config.host                  : host;
        private int    RPort              => (HasConfig && !overrideConnection) ? config.port                  : port;
        private bool   RUseTls            => (HasConfig && !overrideConnection) ? config.useTls                : useTls;
        private float  RConnectTimeout    => (HasConfig && !overrideConnection) ? config.connectTimeoutSeconds : connectTimeoutSeconds;
        private bool   RAutoConnect       => (HasConfig && !overrideSimulation) ? config.autoConnect           : autoConnect;
        private bool   RAutoTick          => (HasConfig && !overrideSimulation) ? config.autoTick              : autoTick;
        private float  RTickRate          => (HasConfig && !overrideSimulation) ? config.tickRate              : tickRate;
        private bool   RTickInFixedUpdate => (HasConfig && !overrideSimulation) ? config.tickInFixedUpdate     : tickInFixedUpdate;
        private bool   RAutoReconnect     => (HasConfig && !overrideReconnect)  ? config.autoReconnect         : autoReconnect;
        private float  RReconnectDelay    => (HasConfig && !overrideReconnect)  ? config.reconnectDelay        : reconnectDelay;
        private bool   RDebugLogging      => (HasConfig && !overrideDebug)      ? config.debugLogging          : debugLogging;

        // Retry — BiomataSimulationConfig has no retry block; BSB's inline fields
        // are the authoritative source when BSB is present. overrideRetry is
        // reserved for future config asset retry support.
        private int   RRetryMaxAttempts         => retryMaxAttempts;
        private float RRetryInitialDelaySeconds => retryInitialDelaySeconds;
        private float RRetryMaxDelaySeconds     => retryMaxDelaySeconds;
        private float RRetryMultiplier          => retryMultiplier;

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
        public bool IsAutoTicking => Manager?.IsAutoTicking == true;

        /// <summary>True when the auto-tick loop is suspended via <see cref="SetPaused"/>.</summary>
        public bool IsPaused => Manager?.IsPaused == true;

        /// <summary>Round-trip duration of the most recent tick, in milliseconds.</summary>
        public float LastTickDurationMs { get; private set; }

        /// <summary>
        /// The config asset currently driving this bootstrapper, if any.
        /// Null when running from inline Inspector values only.
        /// </summary>
        public BiomataSimulationConfig Config => config;

        /// <summary>
        /// Assign a config asset at runtime and clear all override flags.
        /// Call before Awake (e.g. immediately after AddComponent).
        /// </summary>
        public void Configure(BiomataSimulationConfig simulationConfig)
        {
            config             = simulationConfig;
            overrideConnection = false;
            overrideSimulation = false;
            overrideReconnect  = false;
            overrideRetry      = false;
            overrideDebug      = false;
        }

        /// <summary>
        /// Configure connection and simulation parameters directly at runtime.
        /// Call before Awake (e.g. immediately after AddComponent); values take effect in Awake.
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

        /// <summary>
        /// Enable the auto-tick loop. Delegates to <see cref="UnitySimulationManager.SetAutoTick"/>.
        /// </summary>
        public void StartAutoTick()
        {
            Manager.SetAutoTick(true);
            Manager.SetPaused(false);
            Log("Auto-tick started");
        }

        /// <summary>Disable the auto-tick loop.</summary>
        public void StopAutoTick()
        {
            Manager.SetAutoTick(false);
            Log("Auto-tick stopped");
        }

        /// <summary>Suspend or resume the auto-tick loop without resetting the accumulator.</summary>
        public void SetPaused(bool paused)
        {
            Manager.SetPaused(paused);
            Log(paused ? "Paused" : "Resumed");
        }

        /// <summary>
        /// Fire a tick immediately, bypassing the rate timer.
        /// No-op when not connected or a tick is already in progress.
        /// </summary>
        public void ForceTick()
        {
            if (!IsConnected) return;
            Manager.ForceTick();
        }

        // ── Private state ─────────────────────────────────────────────────────────

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

            // Configure USM here — before USM.Start() fires — so USM never auto-
            // connects with stale inspector values, and has the correct tick mode
            // from the first frame.
            ApplyManagerConfig();

            Manager.OnConnected       += HandleConnected;
            Manager.OnDisconnected    += HandleDisconnected;
            Manager.OnTickStarted     += HandleTickStarted;
            Manager.OnTickComplete    += HandleTickComplete;
            Manager.OnTickError       += HandleTickError;
            Manager.OnSimulationEvent += ev => OnSimulationEvent?.Invoke(ev);
        }

        private void Start()
        {
            if (RAutoConnect) Connect();
        }

        private void OnDestroy()
        {
            if (Manager == null) return;
            Manager.OnConnected       -= HandleConnected;
            Manager.OnDisconnected    -= HandleDisconnected;
            Manager.OnTickStarted     -= HandleTickStarted;
            Manager.OnTickComplete    -= HandleTickComplete;
            Manager.OnTickError       -= HandleTickError;
        }

        // ── Internal ──────────────────────────────────────────────────────────────

        private void ApplyManagerConfig()
        {
            // Forward the full resolved config to USM so BuildConfig() reads the correct
            // values at connect time — this fixes the silent-ignore bug where useTls and
            // connectTimeoutSeconds were never forwarded from BSB to USM.
            Manager.Configure(
                host:                   RHost,
                port:                   RPort,
                useTls:                 RUseTls,
                connectTimeoutSeconds:  RConnectTimeout,
                retryMaxAttempts:       RRetryMaxAttempts,
                retryInitialDelaySeconds: RRetryInitialDelaySeconds,
                retryMaxDelaySeconds:   RRetryMaxDelaySeconds,
                retryMultiplier:        RRetryMultiplier,
                tickRate:               RTickRate,
                tickInFixedUpdate:      RTickInFixedUpdate,
                autoConnect:            false);   // BSB owns connection; suppress USM.Start() auto-connect

            // BSB delegates tick scheduling to USM's internal loop (one update path,
            // one accumulator). SetTickMode must be called before USM.Start() fires,
            // which is why ApplyManagerConfig lives in Awake.
            Manager.SetTickMode(TickMode.Internal);
            Manager.SetAutoTick(false);    // will be enabled in HandleConnected when autoTick=true
            Manager.SetPaused(false);

            // BSB owns reconnect; suppress USM's standalone reconnect so only one
            // reconnect path is active at a time.
            // (USM.autoReconnect is an inspector field that stays as-is from USM's
            //  inspector — if BSB is present, users should configure reconnect on BSB.)
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
            Log("Disconnected");
            // USM.NotifyDisconnected() already set _autoTicking=false; nothing to do here.
            if (RAutoReconnect) Reconnect();
            OnDisconnected?.Invoke();
        }

        private void HandleTickStarted() =>
            _tickStartTime = Time.realtimeSinceStartup;

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
