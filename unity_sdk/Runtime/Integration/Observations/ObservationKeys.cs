namespace Biomata.Integration.Observations
{
    /// <summary>
    /// Canonical observation-dictionary keys — the producer/consumer contract between Unity
    /// providers and the Python brain. Reference these constants instead of typing string
    /// literals so a typo is a compile error, not a silently-ignored key.
    ///
    /// These names appear verbatim in the agent's observation dict on the Python side. See
    /// <c>docs/observation_contract.md</c> for the full table (types, producers, meaning) and
    /// the suffix conventions for prefixed/dynamic keys (POIs, nearby objects, needs).
    ///
    /// Sims are free to emit additional, sim-specific keys (hunger, suspicion, …) — the brain
    /// renders unknown keys generically. This registry only pins the keys the SDK itself
    /// produces or the engine injects, where both ends must agree exactly.
    /// </summary>
    public static class ObservationKeys
    {
        // ── Identity / role (set by BiomataAgent) ──────────────────────────────────
        public const string Role         = "role";
        public const string Capabilities = "capabilities";

        // ── Transform (TransformObservationProvider) ───────────────────────────────
        public const string PositionX = "position_x";
        public const string PositionY = "position_y";
        public const string PositionZ = "position_z";
        public const string RotationY = "rotation_y";
        public const string VelocityX = "velocity_x";
        public const string VelocityZ = "velocity_z";

        // ── Time (TimeObservationProvider) ─────────────────────────────────────────
        public const string SimTime    = "sim_time";
        public const string TimeOfDay  = "time_of_day";
        public const string FrameCount = "frame_count";

        // ── Nearby agents (NearbyAgentsObservationProvider) ────────────────────────
        public const string NearbyAgents         = "nearby_agents";
        public const string NearbyAgentCount     = "nearby_agent_count";
        public const string NearestAgentId       = "nearest_agent_id";
        public const string NearestAgentDistance = "nearest_agent_distance";

        // ── Messages (ObservationCollector) ────────────────────────────────────────
        public const string IncomingMessages = "incoming_messages";
        // Subkeys inside each incoming_messages entry.
        public const string MsgFrom     = "from";
        public const string MsgFromName = "from_name";
        public const string MsgText     = "text";

        // ── Entry subkeys (shared by nearby_agents / nearby_pois / nearby_objects) ──
        public const string EntryId       = "id";
        public const string EntryName     = "name";
        public const string EntryDistance = "distance";

        // ── Suffix conventions for prefixed / dynamic keys ─────────────────────────
        // POIObservationProvider / NearbyObjectsObservationProvider: "{key}", "{key}_count",
        // "{key}_nearest". NeedsObservationProvider: "{key}", "{key}_max", "{key}_threshold",
        // "{key}_critical".
        public const string CountSuffix     = "_count";
        public const string NearestSuffix   = "_nearest";
        public const string MaxSuffix       = "_max";
        public const string ThresholdSuffix = "_threshold";
        public const string CriticalSuffix  = "_critical";

        // ── Engine-injected (added by the backend; do NOT overwrite from Unity) ─────
        // Listed for discoverability so custom providers avoid clobbering them.
        public const string AgentId     = "agent_id";
        public const string AgentName   = "agent_name";
        public const string Inventory   = "inventory";
        public const string StateStr    = "state_str";
        public const string StateAdvice = "state_advice";
        public const string StateExt    = "state_ext";
    }
}
