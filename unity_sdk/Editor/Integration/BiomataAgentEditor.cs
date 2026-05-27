using Biomata.Integration;
using UnityEditor;
using UnityEngine;

namespace Biomata.Integration.Editor
{
    [CustomEditor(typeof(BiomataAgent))]
    public class BiomataAgentEditor : UnityEditor.Editor
    {
        // Cached serialized properties
        private SerializedProperty _ownershipMode;
        private SerializedProperty _agentId;
        private SerializedProperty _displayName;
        private SerializedProperty _role;
        private SerializedProperty _capabilities;
        private SerializedProperty _brainClass;
        private SerializedProperty _memoryClass;
        private SerializedProperty _brainConfigJson;
        private SerializedProperty _memoryConfigJson;
        private SerializedProperty _autoRegister;

        private void OnEnable()
        {
            _ownershipMode  = serializedObject.FindProperty("ownershipMode");
            _agentId        = serializedObject.FindProperty("agentId");
            _displayName    = serializedObject.FindProperty("displayName");
            _role           = serializedObject.FindProperty("role");
            _capabilities   = serializedObject.FindProperty("capabilities");
            _brainClass     = serializedObject.FindProperty("brainClass");
            _memoryClass    = serializedObject.FindProperty("memoryClass");
            _brainConfigJson = serializedObject.FindProperty("brainConfigJson");
            _memoryConfigJson = serializedObject.FindProperty("memoryConfigJson");
            _autoRegister   = serializedObject.FindProperty("autoRegister");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var agent = (BiomataAgent)target;
            var mode  = (AgentOwnershipMode)_ownershipMode.enumValueIndex;

            DrawValidationNotices(agent, mode);

            // ── Ownership ─────────────────────────────────────────────────────────
            EditorGUILayout.LabelField("Ownership", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_ownershipMode, new GUIContent("Mode"));

            DrawModeHelpBox(mode);

            // ── Identity ──────────────────────────────────────────────────────────
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Identity", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_agentId,    new GUIContent("Agent ID"));
            EditorGUILayout.PropertyField(_displayName, new GUIContent("Display Name"));

            if (mode == AgentOwnershipMode.CreateAtRuntime)
            {
                // ── Role ──────────────────────────────────────────────────────────
                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField("Role", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(_role,         new GUIContent("Role"));
                EditorGUILayout.PropertyField(_capabilities, new GUIContent("Capabilities"));

                // ── Brain ─────────────────────────────────────────────────────────
                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField("Brain", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(_brainClass,    new GUIContent("Brain Class"));
                EditorGUILayout.PropertyField(_memoryClass,   new GUIContent("Memory Class"));
                EditorGUILayout.PropertyField(_brainConfigJson,  new GUIContent("Brain Config (JSON)"));
                EditorGUILayout.PropertyField(_memoryConfigJson, new GUIContent("Memory Config (JSON)"));
            }

            // ── Lifecycle / Debug settings ────────────────────────────────────────
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Debug", EditorStyles.boldLabel);
            var autoRegisterLabel = mode == AgentOwnershipMode.BindToExisting
                ? new GUIContent("Auto Bind", "Automatically bind to the backend agent on manager connect.")
                : new GUIContent("Auto Register", "Automatically register with the backend on manager connect.");
            EditorGUILayout.PropertyField(_autoRegister, autoRegisterLabel);

            serializedObject.ApplyModifiedProperties();

            // ── Runtime state ─────────────────────────────────────────────────────
            if (Application.isPlaying)
            {
                EditorGUILayout.Space(8);
                DrawRuntimeState(agent);
            }
        }

        private static void DrawModeHelpBox(AgentOwnershipMode mode)
        {
            if (mode == AgentOwnershipMode.BindToExisting)
                EditorGUILayout.HelpBox(
                    "BindToExisting — agent is pre-declared on the backend (e.g. sim.yaml). " +
                    "Unity binds the visual shell; no registration RPC is sent.",
                    MessageType.Info);
            else
                EditorGUILayout.HelpBox(
                    "CreateAtRuntime — Unity owns this agent. " +
                    "Registered with the backend on connect, unregistered on destroy. " +
                    "Brain Class is required.",
                    MessageType.Info);
        }

        private void DrawValidationNotices(BiomataAgent agent, AgentOwnershipMode mode)
        {
            var idProp    = _agentId;
            var brainProp = _brainClass;

            bool idEmpty    = string.IsNullOrEmpty(idProp?.stringValue);
            bool brainEmpty = string.IsNullOrEmpty(brainProp?.stringValue);

            if (idEmpty)
                EditorGUILayout.HelpBox(
                    mode == AgentOwnershipMode.BindToExisting
                        ? "Agent ID is empty. Set it to match the backend agent exactly."
                        : "Agent ID is empty. Assign a unique ID or leave blank to auto-generate.",
                    MessageType.Warning);

            if (mode == AgentOwnershipMode.CreateAtRuntime && brainEmpty)
                EditorGUILayout.HelpBox(
                    "Brain Class is required in CreateAtRuntime mode. " +
                    "Set a fully-qualified Python brain class path " +
                    "(e.g. src.plugins.builtin.ollama.brain.OllamaLLMBrain).",
                    MessageType.Warning);

            if (!idEmpty)
            {
                var id  = idProp.stringValue;
                var all = FindObjectsByType<BiomataAgent>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                foreach (var other in all)
                {
                    if (other == null || other == (BiomataAgent)agent) continue;
                    var otherSo = new SerializedObject(other);
                    var otherId = otherSo.FindProperty("agentId")?.stringValue;
                    if (otherId == id)
                    {
                        EditorGUILayout.HelpBox(
                            $"Duplicate agent ID '{id}' also found on '{other.name}'.",
                            MessageType.Error);
                        break;
                    }
                }
            }

            if (idEmpty || (mode == AgentOwnershipMode.CreateAtRuntime && brainEmpty))
                EditorGUILayout.Space(4);
        }

        private static void DrawRuntimeState(BiomataAgent agent)
        {
            EditorGUILayout.LabelField("Runtime State", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(true))
            {
                var prevColor = GUI.color;
                GUI.color     = agent.IsRegistered ? new Color(0.3f, 1f, 0.4f) : Color.yellow;
                var stateLabel = agent.OwnershipMode == AgentOwnershipMode.BindToExisting
                    ? (agent.IsRegistered ? "● Bound" : "○ Not bound")
                    : (agent.IsRegistered ? "● Registered" : "○ Not registered");
                EditorGUILayout.LabelField(stateLabel, EditorStyles.boldLabel);
                GUI.color = prevColor;

                EditorGUILayout.TextField("Mode",         agent.OwnershipMode.ToString());
                EditorGUILayout.TextField("Resolved ID",  agent.AgentId    ?? "—");
                EditorGUILayout.TextField("Display Name", agent.DisplayName ?? "—");

                var d = agent.LastDecision;
                if (d != null)
                {
                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField("Last Decision", EditorStyles.boldLabel);
                    EditorGUILayout.TextField("Action",  d.Action      ?? "—");
                    EditorGUILayout.TextField("Outcome", d.OutcomeText ?? "—");

                    if (!d.IsSuccess && !string.IsNullOrEmpty(d.Error))
                    {
                        var prev = GUI.color;
                        GUI.color = Color.red;
                        EditorGUILayout.TextField("Error", d.Error);
                        GUI.color = prev;
                    }
                }
            }

            EditorGUILayout.Space(4);

            if (agent.Bridge != null)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (agent.OwnershipMode == AgentOwnershipMode.BindToExisting)
                    {
                        using (new EditorGUI.DisabledScope(agent.IsRegistered))
                            if (GUILayout.Button("Bind")) agent.Bridge.MarkBoundToExisting();
                    }
                    else
                    {
                        using (new EditorGUI.DisabledScope(agent.IsRegistered))
                            if (GUILayout.Button("Register"))   agent.Bridge.Register();

                        using (new EditorGUI.DisabledScope(!agent.IsRegistered))
                            if (GUILayout.Button("Unregister")) agent.Bridge.Unregister();
                    }
                }

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Bridge", EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.ObjectField(
                        "UnityAgentBridge", agent.Bridge, typeof(UnityAgentBridge), true);
            }
        }

        public override bool RequiresConstantRepaint() => Application.isPlaying;
    }
}
