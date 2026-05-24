// Biomata.SDK — BiomataConfig.cs
// Connection configuration for SimulationClient.
// Marked [Serializable] so BiomataManager can expose it in the Unity Inspector.

using System;
using UnityEngine;

namespace Biomata.SDK
{
    /// <summary>
    /// Top-level connection configuration for <see cref="SimulationClient"/>.
    /// The transport is JSON over WebSocket (<c>ws://</c> / <c>wss://</c>).
    /// Pair with <c>biomata-ws</c> on the server (default port 8765).
    /// </summary>
    [Serializable]
    public class BiomataConfig
    {
        [Tooltip("Hostname or IP of the biomata-engine server.")]
        public string Host = "localhost";

        [Tooltip("Server port. Default 8765 for biomata-ws.")]
        public int Port = 8765;

        [Tooltip("Use TLS (wss://). Requires a valid server certificate. Leave false for localhost.")]
        public bool UseTls = false;

        [Tooltip("Seconds to wait for the initial health-check before ConnectAsync throws.")]
        public float ConnectTimeoutSeconds = 10f;

        [Tooltip("Default per-call deadline in seconds. 0 = no deadline.")]
        public float DefaultCallTimeoutSeconds = 30f;

        public RetryConfig Retry = new RetryConfig();

        /// <summary>WebSocket address derived from Host, Port, and UseTls.</summary>
        public string Address => $"{(UseTls ? "wss" : "ws")}://{Host}:{Port}";

        public TimeSpan ConnectTimeout    => TimeSpan.FromSeconds(ConnectTimeoutSeconds);
        public TimeSpan DefaultCallTimeout => DefaultCallTimeoutSeconds > 0
            ? TimeSpan.FromSeconds(DefaultCallTimeoutSeconds)
            : TimeSpan.Zero;
    }

    /// <summary>
    /// Exponential-backoff retry policy used by <see cref="Clients.EventStreamClient"/>
    /// and automatic reconnect logic.
    /// </summary>
    [Serializable]
    public class RetryConfig
    {
        [Tooltip("Maximum reconnect/retry attempts before giving up (0 = infinite).")]
        public int MaxAttempts = 8;

        [Tooltip("Initial delay between reconnect attempts in seconds.")]
        public float InitialDelaySeconds = 0.5f;

        [Tooltip("Upper bound on reconnect delay after exponential growth.")]
        public float MaxDelaySeconds = 30f;

        [Tooltip("Backoff multiplier applied to the delay after each failure.")]
        public float Multiplier = 2f;

        [Tooltip("Jitter fraction (0–1). 0.25 adds ±25% randomness to avoid thundering herd.")]
        public float JitterFraction = 0.25f;

        public TimeSpan InitialDelay => TimeSpan.FromSeconds(InitialDelaySeconds);
        public TimeSpan MaxDelay     => TimeSpan.FromSeconds(MaxDelaySeconds);
    }
}
