using UnityEngine;

namespace Biomata.Integration
{
    /// <summary>
    /// Shareable configuration asset for <see cref="BiomataSimulationBootstrapper"/>.
    ///
    /// Create via <b>right-click → Create → Biomata → Simulation Config</b>.
    /// Assign the asset to the <b>Config Asset</b> slot on a bootstrapper; the
    /// bootstrapper reads its values at Start.  Individual setting groups can be
    /// overridden per-bootstrapper using the Override toggles in the Inspector.
    ///
    /// Typical usage:
    /// <list type="bullet">
    ///   <item>One config asset per environment (Local, Staging, Production).</item>
    ///   <item>Share the same asset across multiple scenes or bootstrapper instances.</item>
    ///   <item>Override only the fields that differ per scene (e.g. tick rate).</item>
    /// </list>
    /// </summary>
    [CreateAssetMenu(
        fileName = "BiomataSimulationConfig",
        menuName = "Biomata/Simulation Config",
        order    = 0)]
    public class BiomataSimulationConfig : ScriptableObject
    {
        // ── Connection ────────────────────────────────────────────────────────────

        [Header("Connection")]
        [Tooltip("Backend server hostname or IP address.")]
        public string host = "localhost";

        [Tooltip("Backend server WebSocket port.")]
        public int port = 8765;

        [Tooltip("Use TLS (wss://) instead of plain WebSocket (ws://).")]
        public bool useTls = false;

        [Tooltip("Seconds to wait for the initial connection handshake before giving up.")]
        [Min(0f)]
        public float connectTimeoutSeconds = 10f;

        // ── Simulation ────────────────────────────────────────────────────────────

        [Header("Simulation")]
        [Tooltip("Open the backend connection automatically when the scene starts.")]
        public bool autoConnect = true;

        [Tooltip("Begin the tick loop automatically once the connection is established.")]
        public bool autoTick = true;

        [Tooltip("Simulation ticks per second. 0 = tick as fast as the update loop allows.")]
        [Min(0f)]
        public float tickRate = 2f;

        [Tooltip(
            "Drive ticks from FixedUpdate (physics-synced, deterministic). " +
            "Uncheck to use Update (frame-synced).")]
        public bool tickInFixedUpdate = false;

        // ── Reconnect ─────────────────────────────────────────────────────────────

        [Header("Reconnect")]
        [Tooltip("Automatically attempt to reconnect after an unexpected disconnect.")]
        public bool autoReconnect = false;

        [Tooltip("Seconds to wait before the automatic reconnect attempt.")]
        [Min(0f)]
        public float reconnectDelay = 3f;

        // ── Debug ─────────────────────────────────────────────────────────────────

        [Header("Debug")]
        [Tooltip("Log connection and tick lifecycle events to the Unity Console.")]
        public bool debugLogging = false;
    }
}
