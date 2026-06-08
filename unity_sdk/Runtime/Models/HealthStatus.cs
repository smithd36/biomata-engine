// Biomata.SDK — HealthStatus.cs
// Server health response returned by HealthClient.CheckAsync().

using System;

namespace Biomata.SDK.Models
{
    /// <summary>
    /// Health and session status returned by the <c>health_check</c> RPC.
    /// </summary>
    public class HealthStatus
    {
        public string Status       { get; set; }
        public string SessionState { get; set; }
        public int    Tick         { get; set; }
        public int    AgentCount   { get; set; }

        /// <summary>True when <see cref="Status"/> is "ok" (case-insensitive).</summary>
        public bool IsOk => string.Equals(Status, "ok", StringComparison.OrdinalIgnoreCase);

        public override string ToString() =>
            $"Health[{Status}] tick={Tick} agents={AgentCount} session={SessionState}";
    }
}
