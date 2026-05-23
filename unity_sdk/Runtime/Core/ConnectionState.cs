// Biomata.SDK — ConnectionState.cs

namespace Biomata.SDK
{
    /// <summary>Lifecycle state of a <see cref="SimulationClient"/> connection.</summary>
    public enum ConnectionState
    {
        /// <summary>Not yet connected. ConnectAsync() has not been called.</summary>
        Disconnected,

        /// <summary>ConnectAsync() in progress — channel created, health-check pending.</summary>
        Connecting,

        /// <summary>Health-check passed; all sub-clients are ready.</summary>
        Connected,

        /// <summary>DisconnectAsync() called; resources are being released.</summary>
        Disconnecting,

        /// <summary>
        /// An unrecoverable error occurred (max reconnect attempts exceeded,
        /// or the server returned a fatal status). Inspect the most recent
        /// <see cref="SimulationClient.LastError"/> for details.
        /// </summary>
        Faulted
    }
}
