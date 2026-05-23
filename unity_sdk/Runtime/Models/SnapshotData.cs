// Biomata.SDK — SnapshotData.cs
// Holds a serialized simulation snapshot returned by SnapshotClient.

using System;

namespace Biomata.SDK.Models
{
    /// <summary>
    /// Opaque snapshot of the complete simulation state at one tick.
    /// Pass to <c>SnapshotClient.RestoreAsync()</c> to restore.
    /// Persist via <c>SnapshotClient.SaveToFileAsync()</c> for save/load.
    /// </summary>
    public class SnapshotData
    {
        /// <summary>
        /// Raw bytes (pickle-serialized Python SimulationSnapshot).
        /// Treat as opaque — do not modify.
        /// </summary>
        public byte[] Data { get; set; }

        /// <summary>Tick at which the snapshot was captured.</summary>
        public int Tick { get; set; }

        /// <summary>ISO-8601 UTC timestamp of snapshot creation on the server.</summary>
        public string CreatedAt { get; set; }

        /// <summary>True if this snapshot was loaded from disk (Tick/CreatedAt may be unknown).</summary>
        public bool IsFromFile { get; set; }

        /// <summary>File path this snapshot was saved to/loaded from, or null.</summary>
        public string FilePath { get; set; }

        public override string ToString() =>
            IsFromFile
                ? $"SnapshotData [file: {FilePath}]"
                : $"SnapshotData [tick={Tick}, created={CreatedAt}]";
    }

    /// <summary>Server health status returned by HealthClient.</summary>
    public class HealthStatus
    {
        public string Status       { get; set; }
        public string SessionState { get; set; }
        public int    Tick         { get; set; }
        public int    AgentCount   { get; set; }

        public bool IsOk => string.Equals(Status, "ok", StringComparison.OrdinalIgnoreCase);

        public override string ToString() =>
            $"Health[{Status}] tick={Tick} agents={AgentCount} session={SessionState}";
    }
}
