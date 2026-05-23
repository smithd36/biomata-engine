// Biomata.SDK — BiomataConfig.cs
// Connection configuration for SimulationClient.
// Marked [Serializable] so BiomataManager can expose it in the Unity Inspector.

using System;
using UnityEngine;

namespace Biomata.SDK
{
    /// <summary>
    /// Selects which on-the-wire transport <see cref="SimulationClient"/> uses
    /// to reach the Python backend.
    ///
    /// <list type="bullet">
    ///   <item><b>WebSocket</b> (default for Unity 6) — JSON over System.Net.WebSockets.
    ///         Compiles cleanly on Unity 6's restricted .NET Standard 2.1 reference
    ///         assemblies, works on every platform Unity targets, including WebGL.
    ///         Pair with <c>biomata-ws</c> on the server (default port 8765).</item>
    ///   <item><b>Grpc</b> — protobuf over Grpc.Net.Client. Higher throughput and
    ///         typed contracts, but fragile to compile on Unity 6 (relies on
    ///         reflection workarounds for SocketsHttpHandler). Prefer for
    ///         server-to-server / research integrations. Pair with
    ///         <c>biomata-grpc</c> on the server (default port 50051).</item>
    /// </list>
    /// </summary>
    public enum TransportKind
    {
        /// <summary>JSON over WebSocket — default for Unity 6.</summary>
        WebSocket = 0,
        /// <summary>Protobuf over gRPC — retained for research / server use.</summary>
        Grpc      = 1,
    }


    /// <summary>
    /// Top-level connection configuration for <see cref="SimulationClient"/>.
    /// </summary>
    [Serializable]
    public class BiomataConfig
    {
        [Tooltip("Wire-level transport. WebSocket (default) is recommended for Unity 6; gRPC is kept for research / server integrations.")]
        public TransportKind Transport = TransportKind.WebSocket;

        [Tooltip("Hostname or IP of the biomata-engine server.")]
        public string Host = "localhost";

        [Tooltip("Server port. Default 8765 for WebSocket; if you switch to gRPC set this to 50051.")]
        public int Port = 8765;

        [Tooltip("Use TLS (HTTPS / WSS). Requires a valid server certificate. Leave false for localhost.")]
        public bool UseTls = false;

        [Tooltip("Seconds to wait for the initial health-check before ConnectAsync throws.")]
        public float ConnectTimeoutSeconds = 10f;

        [Tooltip("Default per-RPC deadline in seconds. 0 = no deadline.")]
        public float DefaultCallTimeoutSeconds = 30f;

        public RetryConfig Retry = new RetryConfig();

        /// <summary>
        /// Composed address string. Scheme is chosen by Transport + UseTls:
        ///   WebSocket → ws:// or wss://
        ///   Grpc      → http:// or https://
        /// </summary>
        public string Address
        {
            get
            {
                var scheme = Transport == TransportKind.WebSocket
                    ? (UseTls ? "wss"   : "ws")
                    : (UseTls ? "https" : "http");
                return $"{scheme}://{Host}:{Port}";
            }
        }

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
