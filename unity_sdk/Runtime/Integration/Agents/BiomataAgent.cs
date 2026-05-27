using System;
using System.Collections.Generic;
using Biomata.Integration.Actions;
using Biomata.Integration.Observations;
using Biomata.SDK.Models;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Biomata.Integration
{
    /// <summary>
    /// Designer-first agent component. Drop on any prefab to create a fully wired
    /// autonomous NPC — configure agent identity, brain, and capabilities in the
    /// Inspector, then press Play.
    ///
    /// Auto-wires the required infrastructure (<see cref="ObservationCollector"/>,
    /// <see cref="ActionExecutor"/>, <see cref="UnityAgentBridge"/>) and adds
    /// a default set of action handlers (<see cref="MoveActionHandler"/>,
    /// <see cref="SpeakActionHandler"/>, <see cref="InteractActionHandler"/>) and
    /// <see cref="TransformObservationProvider"/> when the component is first attached.
    ///
    /// Designers may remove or replace any of those defaults after attachment.
    /// The existing <see cref="ActionHandlerBase"/> architecture is fully preserved —
    /// custom handlers still work exactly as before.
    ///
    /// Call <see cref="Configure"/> immediately after <c>AddComponent</c> when
    /// constructing agents procedurally; values are applied in Awake.
    /// </summary>
    [AddComponentMenu("Biomata/Agent")]
    [RequireComponent(typeof(ObservationCollector))]
    [RequireComponent(typeof(ActionExecutor))]
    [RequireComponent(typeof(UnityAgentBridge))]
    public class BiomataAgent : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────────

        [Header("Identity")]
        [Tooltip(
            "Unique agent ID. Must be unique within the simulation and match the backend YAML " +
            "config when agents are pre-declared. Leave empty to auto-generate from the " +
            "GameObject name at startup.")]
        [SerializeField] private string agentId = "";

        [Tooltip("Human-readable name shown in logs and events. Defaults to the GameObject name.")]
        [SerializeField] private string displayName = "";

        [Tooltip(
            "Agent role injected as the 'role' key in every tick observation " +
            "(e.g. 'Guard', 'Merchant', 'Villager'). Used by the backend brain " +
            "for context.")]
        [SerializeField] private string role = "";

        [Tooltip(
            "Capability tags forwarded as the 'capabilities' key in every tick observation " +
            "(e.g. 'patrol', 'trade', 'social'). The backend brain uses these to decide " +
            "which actions are available.")]
        [SerializeField] private string[] capabilities = Array.Empty<string>();

        [Header("Brain")]
        [Tooltip(
            "Fully-qualified Python class path for the agent brain.\n" +
            "Examples:\n" +
            "  src.plugins.builtin.ollama.brain.OllamaLLMBrain\n" +
            "  src.plugins.builtin.waypoint_brain.brain.WaypointBrain\n" +
            "Required — registration fails without this.")]
        [SerializeField] private string brainClass = "";

        [Tooltip(
            "Fully-qualified Python class path for the agent memory. " +
            "Leave empty to use the simulation default.")]
        [SerializeField] private string memoryClass = "";

        [Tooltip(
            "JSON object forwarded to the brain constructor as keyword arguments. " +
            "Leave empty for no extra config.\n" +
            "Example: {\"model\": \"qwen2.5:14b\", \"temperature\": 0.7}")]
        [TextArea(3, 8)]
        [SerializeField] private string brainConfigJson = "";

        [Tooltip("JSON object forwarded to the memory constructor as keyword arguments.")]
        [TextArea(2, 4)]
        [SerializeField] private string memoryConfigJson = "";

        [Header("Lifecycle")]
        [Tooltip(
            "Register this agent with the backend automatically when the simulation " +
            "manager connects. Disable to register manually via Bridge.Register().")]
        [SerializeField] private bool autoRegister = true;

        // ── Events ────────────────────────────────────────────────────────────────

        /// <summary>Forwarded from <see cref="Bridge"/> each time a backend decision arrives.</summary>
        public event Action<AgentDecisionResult> OnDecisionReceived;

        /// <summary>Forwarded from <see cref="Bridge"/> just before an action handler starts.</summary>
        public event Action<string> OnActionStarted;

        /// <summary>Forwarded from <see cref="Bridge"/> after an action handler completes.</summary>
        public event Action<string> OnActionCompleted;

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>
        /// The underlying agent bridge. Available after Awake.
        /// Use this for low-level control (manual Register, Unregister, etc.).
        /// </summary>
        public UnityAgentBridge Bridge { get; private set; }

        /// <summary>
        /// Resolved agent identifier. In edit mode this reflects the raw Inspector
        /// field; at runtime it reflects the value passed to the bridge (auto-generated
        /// if the field was empty).
        /// </summary>
        public string AgentId => Bridge != null ? Bridge.AgentId : agentId;

        /// <summary>Display name used in logs and events.</summary>
        public string DisplayName => Bridge != null ? Bridge.AgentName : displayName;

        /// <summary>True once the backend has acknowledged registration.</summary>
        public bool IsRegistered => Bridge?.IsRegistered == true;

        /// <summary>Most recent decision received from the backend, or <c>null</c>.</summary>
        public AgentDecisionResult LastDecision => Bridge?.LastDecision;

        /// <summary>
        /// Configure agent parameters at runtime.
        /// Call immediately after <c>AddComponent</c>, before <c>Start</c>.
        /// Values set here override Inspector fields and are applied in Awake.
        /// Omit parameters to leave the corresponding Inspector value unchanged.
        /// </summary>
        public void Configure(
            string   agentId,
            string   displayName  = null,
            string   role         = null,
            string[] capabilities = null,
            string   brainClass   = null,
            string   memoryClass  = null,
            bool     autoRegister = true)
        {
            this.agentId      = agentId;
            if (displayName  != null) this.displayName  = displayName;
            if (role         != null) this.role         = role;
            if (capabilities != null) this.capabilities = capabilities;
            if (brainClass   != null) this.brainClass   = brainClass;
            if (memoryClass  != null) this.memoryClass  = memoryClass;
            this.autoRegister = autoRegister;
        }

        // ── Unity lifecycle ───────────────────────────────────────────────────────

        private void Awake()
        {
            Bridge = GetComponent<UnityAgentBridge>();

            var resolvedId   = string.IsNullOrEmpty(agentId)
                ? $"{gameObject.name}_{GetInstanceID():x8}"
                : agentId;
            var resolvedName = string.IsNullOrEmpty(displayName) ? gameObject.name : displayName;

            Bridge.Configure(
                agentId:     resolvedId,
                agentName:   resolvedName,
                autoRegister: autoRegister,
                brainClass:  string.IsNullOrEmpty(brainClass)  ? null : brainClass,
                memoryClass: string.IsNullOrEmpty(memoryClass) ? null : memoryClass,
                brainConfig:  ParseJsonConfig(brainConfigJson),
                memoryConfig: ParseJsonConfig(memoryConfigJson));

            Bridge.OnDecisionReceived += d => OnDecisionReceived?.Invoke(d);
            Bridge.OnActionStarted   += a => OnActionStarted?.Invoke(a);
            Bridge.OnActionCompleted += a => OnActionCompleted?.Invoke(a);

            // Inject static metadata into every observation so the brain
            // always has role and capability context without extra YAML setup.
            var collector = GetComponent<ObservationCollector>();
            if (!string.IsNullOrEmpty(role))
                collector.SetData("role", role);
            if (capabilities != null && capabilities.Length > 0)
                collector.SetData("capabilities", capabilities);

            ValidateAtRuntime(resolvedId);
        }

        // ── Editor-only callbacks ─────────────────────────────────────────────────

#if UNITY_EDITOR
        // Called by Unity when the component is first added to a GameObject,
        // and when "Reset" is chosen from the component context menu.
        // Adds a default set of providers and handlers so the NPC works immediately.
        private void Reset()
        {
            AddIfMissing<TransformObservationProvider>();
            AddIfMissing<MoveActionHandler>();
            AddIfMissing<SpeakActionHandler>();
            AddIfMissing<InteractActionHandler>();
        }

        private void OnValidate()
        {
            if (string.IsNullOrEmpty(agentId))
                Debug.LogWarning(
                    $"[BiomataAgent] '{name}': Agent ID is empty. " +
                    "Assign a unique ID to match the backend config.", this);

            if (string.IsNullOrEmpty(brainClass))
                Debug.LogWarning(
                    $"[BiomataAgent] '{name}': Brain Class is empty. " +
                    "Set a fully-qualified Python brain class path.", this);

            if (!string.IsNullOrEmpty(agentId))
                CheckDuplicateIdInEditor(agentId);
        }

        private void CheckDuplicateIdInEditor(string id)
        {
            var all = FindObjectsByType<BiomataAgent>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var other in all)
            {
                if (other == this || other == null) continue;
                if (other.agentId == id)
                {
                    Debug.LogWarning(
                        $"[BiomataAgent] Duplicate agent ID '{id}' on " +
                        $"'{name}' and '{other.name}'.", this);
                    break;
                }
            }
        }

        private void AddIfMissing<T>() where T : Component
        {
            if (GetComponent<T>() == null)
                gameObject.AddComponent<T>();
        }
#endif

        // ── Private helpers ───────────────────────────────────────────────────────

        private void ValidateAtRuntime(string resolvedId)
        {
            var all = FindObjectsByType<BiomataAgent>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var other in all)
            {
                if (other == this) continue;
                var otherId = other.Bridge != null ? other.Bridge.AgentId : other.agentId;
                if (otherId == resolvedId)
                    Debug.LogWarning(
                        $"[BiomataAgent] Duplicate agent ID '{resolvedId}' on " +
                        $"'{name}' and '{other.name}'. Registration may fail.", this);
            }
        }

        private static Dictionary<string, object> ParseJsonConfig(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                return ParseJObject(JObject.Parse(json));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BiomataAgent] Invalid JSON config — {ex.Message}");
                return null;
            }
        }

        private static Dictionary<string, object> ParseJObject(JObject obj)
        {
            var d = new Dictionary<string, object>(obj.Count);
            foreach (var kv in obj)
                d[kv.Key] = ParseJToken(kv.Value);
            return d;
        }

        private static object ParseJToken(JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.String:  return token.Value<string>();
                case JTokenType.Integer: return token.Value<long>();
                case JTokenType.Float:   return token.Value<double>();
                case JTokenType.Boolean: return token.Value<bool>();
                case JTokenType.Null:    return null;
                case JTokenType.Object:  return ParseJObject((JObject)token);
                case JTokenType.Array:
                    var list = new List<object>(((JArray)token).Count);
                    foreach (var item in (JArray)token) list.Add(ParseJToken(item));
                    return list;
                default: return token.ToString();
            }
        }
    }
}
