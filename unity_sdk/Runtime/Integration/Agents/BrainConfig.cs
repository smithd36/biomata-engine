using System.Collections.Generic;
using UnityEngine;

namespace Biomata.Integration
{
    /// <summary>
    /// Reusable, authorable agent personality + LLM settings as a ScriptableObject asset,
    /// replacing the hand-escaped JSON blob in <see cref="BiomataAgent"/>'s inspector.
    ///
    /// Create via <c>Assets ▸ Create ▸ Biomata ▸ Brain Config</c>, fill in the fields, and drag
    /// the asset onto an agent. The same personality can be shared across agents and simulations,
    /// diffed in version control, and edited without escaping quotes.
    ///
    /// Serialized to the brain constructor's kwargs at registration time via
    /// <see cref="ToConfigDictionary"/>. The shape matches the builtin OllamaLLMBrain:
    /// <c>{ system_prompt, personality:{ traits, goals, backstory }, llm_config:{ model, base_url, temperature } }</c>.
    /// Empty fields are omitted so the backend's own defaults apply.
    /// </summary>
    [CreateAssetMenu(menuName = "Biomata/Brain Config", fileName = "BrainConfig")]
    public class BrainConfig : ScriptableObject
    {
        [Header("Prompt")]
        [Tooltip("System prompt sentence framing the agent (e.g. 'You are a medieval knight'). Leave empty for the backend default.")]
        [TextArea(2, 6)]
        public string systemPrompt = "";

        [Header("Personality")]
        public string[] traits = System.Array.Empty<string>();
        public string[] goals  = System.Array.Empty<string>();

        [TextArea(2, 6)]
        public string backstory = "";

        [Header("LLM (optional — leave empty for backend defaults)")]
        public string model = "";
        public string baseUrl = "";

        [Tooltip("Negative = leave unset (backend default). 0..1 typical.")]
        public float temperature = -1f;

        /// <summary>
        /// Build the brain-constructor kwargs dictionary. Only populated fields are included.
        /// </summary>
        public Dictionary<string, object> ToConfigDictionary()
        {
            var config = new Dictionary<string, object>();

            if (!string.IsNullOrWhiteSpace(systemPrompt))
                config["system_prompt"] = systemPrompt;

            var personality = new Dictionary<string, object>();
            if (traits != null && traits.Length > 0) personality["traits"] = new List<object>(traits);
            if (goals  != null && goals.Length  > 0) personality["goals"]  = new List<object>(goals);
            if (!string.IsNullOrWhiteSpace(backstory)) personality["backstory"] = backstory;
            if (personality.Count > 0) config["personality"] = personality;

            var llm = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(model))   llm["model"]    = model;
            if (!string.IsNullOrWhiteSpace(baseUrl)) llm["base_url"] = baseUrl;
            if (temperature >= 0f)                   llm["temperature"] = (double)temperature;
            if (llm.Count > 0) config["llm_config"] = llm;

            return config;
        }
    }
}
