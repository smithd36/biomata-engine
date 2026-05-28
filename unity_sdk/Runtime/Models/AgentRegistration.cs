// Biomata.SDK — AgentRegistration.cs
// Input model for AgentClient.RegisterAsync().

using System.Collections.Generic;

namespace Biomata.SDK.Models
{
    /// <summary>
    /// Describes a new agent to register with the running simulation.
    /// </summary>
    public class AgentRegistration
    {
        /// <summary>
        /// Unique agent identifier. Must be unique within the simulation.
        /// Used in all subsequent API calls to identify this agent.
        /// </summary>
        public string AgentId { get; set; }

        /// <summary>
        /// Human-readable display name. Used in logs, events, and observations.
        /// </summary>
        public string AgentName { get; set; }

        /// <summary>
        /// Fully-qualified Python class path for the agent's brain.
        /// Example: <c>"src.plugins.builtin.ollama.brain.OllamaLLMBrain"</c>
        /// </summary>
        public string BrainClass { get; set; }

        /// <summary>
        /// Keyword arguments passed to the brain constructor.
        /// For OllamaLLMBrain this includes <c>llm_config</c> and <c>personality</c>.
        /// Values must be JSON-serializable (string, number, bool, list, dict, null).
        /// </summary>
        public Dictionary<string, object> BrainConfig { get; set; }

        /// <summary>
        /// Optional Python class path for the agent's memory.
        /// Defaults to <c>SimpleMemory</c> when null or empty.
        /// </summary>
        public string MemoryClass { get; set; }

        /// <summary>Keyword arguments for the memory constructor.</summary>
        public Dictionary<string, object> MemoryConfig { get; set; }

        /// <summary>
        /// Capability tags for the agent. Gates which action schemas and observation
        /// providers are visible on the backend. Maps to <c>Agent.capabilities</c>.
        /// Null or empty means no capability-gated actions are available.
        /// </summary>
        public string[] Capabilities { get; set; }
    }
}
