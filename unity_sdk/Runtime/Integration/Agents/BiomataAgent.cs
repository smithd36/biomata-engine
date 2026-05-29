using System;
using System.Collections.Generic;
using Biomata.Integration.Actions;
using Biomata.Integration.Observations;
using Biomata.Integration.Simulation;
using Biomata.SDK.Models;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Biomata.Integration
{
    /// <summary>
    /// Designer-first agent component. Drop on any prefab to create a fully wired NPC —
    /// configure identity, ownership mode, brain, and capabilities in the Inspector, then play.
    ///
    /// Two ownership modes:
    /// <list type="bullet">
    ///   <item><see cref="AgentOwnershipMode.BindToExisting"/> — agent already exists on the backend
    ///   (e.g. declared in sim.yaml). Unity binds the visual shell; no registration RPC is sent.</item>
    ///   <item><see cref="AgentOwnershipMode.CreateAtRuntime"/> — agent is owned by this Unity client.
    ///   Registered with the backend on connect and unregistered on destroy. Requires Brain Class.</item>
    /// </list>
    ///
    /// Auto-wires <see cref="ObservationCollector"/>, <see cref="ActionExecutor"/>, and
    /// <see cref="UnityAgentBridge"/> when first attached. A default set of action handlers
    /// and <see cref="TransformObservationProvider"/> are added for immediate use; remove or
    /// replace them freely after attachment.
    ///
    /// Call <see cref="Configure"/> immediately after <c>AddComponent</c> when constructing
    /// agents procedurally; values are applied in Awake.
    /// </summary>
    [AddComponentMenu("Biomata/Agent")]
    [RequireComponent(typeof(ObservationCollector))]
    [RequireComponent(typeof(ActionExecutor))]
    [RequireComponent(typeof(UnityAgentBridge))]
    public class BiomataAgent : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────────

        [Header("Ownership")]
        [Tooltip(
            "BindToExisting: agent is pre-declared on the backend. Unity binds to it as a visual " +
            "shell — no registration RPC is sent.\n\n" +
            "CreateAtRuntime: agent is owned by Unity. Registered on connect, unregistered on destroy. " +
            "Requires Brain Class.")]
        [SerializeField] private AgentOwnershipMode ownershipMode = AgentOwnershipMode.BindToExisting;

        [Header("Identity")]
        [Tooltip(
            "Unique agent ID. Must match the backend agent exactly in BindToExisting mode. " +
            "In CreateAtRuntime mode, leave empty to auto-generate from the GameObject name.")]
        [SerializeField] private string agentId = "";

        [Tooltip("Human-readable name shown in logs and events. Defaults to the GameObject name.")]
        [SerializeField] private string displayName = "";

        [Header("Role")]
        [Tooltip(
            "Agent role injected as the 'role' key in every tick observation " +
            "(e.g. 'Guard', 'Merchant', 'Villager').")]
        [SerializeField] private string role = "";

        [Tooltip(
            "Capability tags forwarded as the 'capabilities' key in every tick observation " +
            "(e.g. 'patrol', 'trade', 'social').")]
        [SerializeField] private string[] capabilities = Array.Empty<string>();

        [Header("Brain")]
        [Tooltip(
            "Fully-qualified Python class path for the agent brain. Required in CreateAtRuntime mode.\n" +
            "Examples:\n" +
            "  src.plugins.builtin.ollama.brain.OllamaLLMBrain\n" +
            "  src.plugins.builtin.waypoint_brain.brain.WaypointBrain")]
        [SerializeField] private string brainClass = "";

        [Tooltip(
            "Fully-qualified Python class path for the agent memory. " +
            "Leave empty to use the simulation default.")]
        [SerializeField] private string memoryClass = "";

        [Tooltip(
            "JSON object forwarded to the brain constructor as keyword arguments.\n" +
            "Example: {\"model\": \"qwen2.5:14b\", \"temperature\": 0.7}")]
        [TextArea(3, 8)]
        [SerializeField] private string brainConfigJson = "";

        [Tooltip("JSON object forwarded to the memory constructor as keyword arguments.")]
        [TextArea(2, 4)]
        [SerializeField] private string memoryConfigJson = "";

        [Header("Lifecycle")]
        [Tooltip(
            "BindToExisting: automatically bind to the backend agent when the manager connects.\n" +
            "CreateAtRuntime: automatically register with the backend when the manager connects.\n" +
            "Disable to call Bridge.Register() or Bridge.MarkBoundToExisting() manually.")]
        [SerializeField] private bool autoRegister = true;

        // ── Events ────────────────────────────────────────────────────────────────

        /// <summary>Forwarded from <see cref="Bridge"/> each time a backend decision arrives.</summary>
        public event Action<AgentDecisionResult> OnDecisionReceived;

        /// <summary>Forwarded from <see cref="Bridge"/> just before an action handler starts.</summary>
        public event Action<string> OnActionStarted;

        /// <summary>Forwarded from <see cref="Bridge"/> after an action handler completes.</summary>
        public event Action<string> OnActionCompleted;

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>The current ownership mode.</summary>
        public AgentOwnershipMode OwnershipMode => ownershipMode;

        /// <summary>
        /// The underlying agent bridge. Available after Awake.
        /// Use this for low-level control (manual Register, Unregister, etc.).
        /// </summary>
        public UnityAgentBridge Bridge { get; private set; }

        /// <summary>
        /// Resolved agent identifier. In edit mode reflects the raw Inspector field;
        /// at runtime reflects the value passed to the bridge.
        /// </summary>
        public string AgentId => Bridge != null ? Bridge.AgentId : agentId;

        /// <summary>Display name used in logs and events.</summary>
        public string DisplayName => Bridge != null ? Bridge.AgentName : displayName;

        /// <summary>The configured role string. Read by the editor validator.</summary>
        public string RoleForValidation => role;

        /// <summary>
        /// True once the backend has acknowledged registration (CreateAtRuntime) or
        /// the agent has been bound (BindToExisting).
        /// </summary>
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
            string             agentId,
            string             displayName    = null,
            string             role           = null,
            string[]           capabilities   = null,
            string             brainClass     = null,
            string             memoryClass    = null,
            bool               autoRegister   = true,
            AgentOwnershipMode ownershipMode  = AgentOwnershipMode.BindToExisting)
        {
            this.agentId       = agentId;
            this.ownershipMode = ownershipMode;
            if (displayName  != null) this.displayName  = displayName;
            if (role         != null) this.role         = role;
            if (capabilities != null) this.capabilities = capabilities;
            if (brainClass   != null) this.brainClass   = brainClass;
            if (memoryClass  != null) this.memoryClass  = memoryClass;
            this.autoRegister  = autoRegister;
        }

        // ── Private ───────────────────────────────────────────────────────────────

        private UnitySimulationManager _manager;
        private string _resolvedId;

        // ── Unity lifecycle ───────────────────────────────────────────────────────

        private void Awake()
        {
            Bridge = GetComponent<UnityAgentBridge>();

            _resolvedId = string.IsNullOrEmpty(agentId)
                ? $"{gameObject.name}_{GetInstanceID():x8}"
                : agentId;
            var resolvedName = string.IsNullOrEmpty(displayName) ? gameObject.name : displayName;

            // ── Role expansion ────────────────────────────────────────────────────
            // Apply role defaults for any fields not set explicitly on this agent.
            // Agent-level settings always win over role defaults.
            var resolvedCapabilities = (capabilities != null && capabilities.Length > 0)
                ? capabilities
                : Array.Empty<string>();
            var resolvedBrainClass  = brainClass;
            var resolvedBrainConfig = ParseJsonConfig(brainConfigJson);

            if (!string.IsNullOrEmpty(role))
            {
                var roleEntry = RoleManifestLoader.FindRole(role);
                if (roleEntry != null)
                {
                    // Auto-populate capabilities from role when not set on the agent
                    if (resolvedCapabilities.Length == 0
                        && roleEntry.capabilities != null
                        && roleEntry.capabilities.Length > 0)
                    {
                        resolvedCapabilities = roleEntry.capabilities;
                    }

                    // Auto-populate brain class from role when not set on the agent.
                    // brain_class is preferred; brain_provider is a Python-side shorthand
                    // and cannot be resolved to a C# class, so it is ignored here.
                    if (string.IsNullOrEmpty(resolvedBrainClass)
                        && !string.IsNullOrEmpty(roleEntry.brain_class))
                    {
                        resolvedBrainClass = roleEntry.brain_class;
                    }
                }
                else if (RoleManifestLoader.IsLoaded)
                {
                    Debug.LogWarning(
                        $"[BiomataAgent] '{name}': Role '{role}' not found in BiomataRoles.json. " +
                        "Regenerate the JSON after editing the roles: block in sim.yaml.");
                }
            }

            if (ownershipMode == AgentOwnershipMode.CreateAtRuntime)
            {
                Bridge.Configure(
                    agentId:      _resolvedId,
                    agentName:    resolvedName,
                    autoRegister: autoRegister,
                    brainClass:   string.IsNullOrEmpty(resolvedBrainClass)  ? null : resolvedBrainClass,
                    memoryClass:  string.IsNullOrEmpty(memoryClass) ? null : memoryClass,
                    brainConfig:  resolvedBrainConfig,
                    memoryConfig: ParseJsonConfig(memoryConfigJson),
                    capabilities: resolvedCapabilities.Length > 0 ? resolvedCapabilities : null);
            }
            else // BindToExisting
            {
                // Suppress the bridge's own registration — we handle binding in Start.
                Bridge.Configure(
                    agentId:      _resolvedId,
                    agentName:    resolvedName,
                    autoRegister: false,
                    brainClass:   null,
                    memoryClass:  null,
                    brainConfig:  null,
                    memoryConfig: null);
            }

            Bridge.OnDecisionReceived += d => OnDecisionReceived?.Invoke(d);
            Bridge.OnActionStarted   += a => OnActionStarted?.Invoke(a);
            Bridge.OnActionCompleted += a => OnActionCompleted?.Invoke(a);

            var collector = GetComponent<ObservationCollector>();
            if (!string.IsNullOrEmpty(role))
                collector.SetData("role", role);
            if (resolvedCapabilities.Length > 0)
                collector.SetData("capabilities", resolvedCapabilities);

            ValidateAtRuntime(_resolvedId);
        }

        private void Start()
        {
            _manager = UnitySimulationManager.Instance ?? FindFirstObjectByType<UnitySimulationManager>();

            if (ownershipMode == AgentOwnershipMode.CreateAtRuntime)
                return; // Bridge handles registration via its own Start()

            // BindToExisting — bind visual shell to the pre-existing backend agent.
            if (!autoRegister || _manager == null) return;

            if (_manager.IsConnected)
                Bridge.MarkBoundToExisting();
            else
                _manager.OnConnected += HandleManagerConnectedBind;
        }

        private void OnDestroy()
        {
            if (_manager != null)
                _manager.OnConnected -= HandleManagerConnectedBind;

            if (ownershipMode == AgentOwnershipMode.CreateAtRuntime
                && Bridge != null
                && Bridge.IsRegistered
                && _manager?.Client != null)
            {
                // Fire-and-forget: Task continues even after MonoBehaviour is destroyed.
                _ = _manager.Client.Agents.TryRemoveAsync(_resolvedId);
            }
        }

        private void HandleManagerConnectedBind()
        {
            _manager.OnConnected -= HandleManagerConnectedBind;
            Bridge.MarkBoundToExisting();
        }

        // ── Editor-only callbacks ─────────────────────────────────────────────────

#if UNITY_EDITOR
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
                    "Assign a unique ID matching the backend agent.", this);

            if (ownershipMode == AgentOwnershipMode.CreateAtRuntime
                && string.IsNullOrEmpty(brainClass)
                && string.IsNullOrEmpty(role))
                Debug.LogWarning(
                    $"[BiomataAgent] '{name}': Brain Class is required in CreateAtRuntime mode " +
                    "unless a Role is set (role may supply the brain class).", this);

            // Validate role name against manifest if manifest is loaded
            if (!string.IsNullOrEmpty(role) && RoleManifestLoader.IsLoaded)
            {
                var roleEntry = RoleManifestLoader.FindRole(role);
                if (roleEntry == null)
                    Debug.LogWarning(
                        $"[BiomataAgent] '{name}': Role '{role}' is not declared in " +
                        "BiomataRoles.json. Check for typos or re-export the manifest.", this);
            }

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
