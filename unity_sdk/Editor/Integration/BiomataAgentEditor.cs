using System.Collections.Generic;
using Biomata.Integration;
using Biomata.Integration.Actions;
using Biomata.Integration.Agents;
using Biomata.Integration.Simulation;
using UnityEditor;
using UnityEngine;

namespace Biomata.Integration.Editor
{
    [CustomEditor(typeof(BiomataAgent))]
    public class BiomataAgentEditor : UnityEditor.Editor
    {
        // ── Serialized properties ─────────────────────────────────────────────────

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

        // ── Foldout state (per editor instance) ───────────────────────────────────

        private bool _coverageFoldout = true;
        private bool _gatedFoldout    = false;

        private void OnEnable()
        {
            _ownershipMode   = serializedObject.FindProperty("ownershipMode");
            _agentId         = serializedObject.FindProperty("agentId");
            _displayName     = serializedObject.FindProperty("displayName");
            _role            = serializedObject.FindProperty("role");
            _capabilities    = serializedObject.FindProperty("capabilities");
            _brainClass      = serializedObject.FindProperty("brainClass");
            _memoryClass     = serializedObject.FindProperty("memoryClass");
            _brainConfigJson  = serializedObject.FindProperty("brainConfigJson");
            _memoryConfigJson = serializedObject.FindProperty("memoryConfigJson");
            _autoRegister    = serializedObject.FindProperty("autoRegister");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var agent = (BiomataAgent)target;
            var mode  = (AgentOwnershipMode)_ownershipMode.enumValueIndex;

            // ── Validation notices (top, most visible) ────────────────────────────
            DrawValidationNotices(agent, mode);

            // ── Ownership ─────────────────────────────────────────────────────────
            EditorGUILayout.LabelField("Ownership", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_ownershipMode, new GUIContent("Mode"));
            DrawModeHelpBox(mode);

            // ── Identity ──────────────────────────────────────────────────────────
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Identity", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_agentId,     new GUIContent("Agent ID"));
            EditorGUILayout.PropertyField(_displayName, new GUIContent("Display Name"));

            // CreateAtRuntime-only fields
            if (mode == AgentOwnershipMode.CreateAtRuntime)
            {
                // ── Role ──────────────────────────────────────────────────────────
                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField("Role", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(_role,         new GUIContent("Role"));
                EditorGUILayout.PropertyField(_capabilities, new GUIContent("Capabilities"));
                DrawRoleDerivedCapabilitiesHint();

                // ── Brain ─────────────────────────────────────────────────────────
                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField("Brain", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(_brainClass,     new GUIContent("Brain Class"));
                EditorGUILayout.PropertyField(_memoryClass,    new GUIContent("Memory Class"));
                EditorGUILayout.PropertyField(_brainConfigJson,  new GUIContent("Brain Config (JSON)"));
                EditorGUILayout.PropertyField(_memoryConfigJson, new GUIContent("Memory Config (JSON)"));
                DrawBrainSourceHint();
            }

            // ── Lifecycle ─────────────────────────────────────────────────────────
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Lifecycle", EditorStyles.boldLabel);
            var autoLabel = mode == AgentOwnershipMode.BindToExisting
                ? new GUIContent("Auto Bind",     "Automatically bind to the backend agent on manager connect.")
                : new GUIContent("Auto Register", "Automatically register with the backend on manager connect.");
            EditorGUILayout.PropertyField(_autoRegister, autoLabel);

            // ── Action Coverage ───────────────────────────────────────────────────
            DrawActionCoverage(mode);

            serializedObject.ApplyModifiedProperties();

            // ── Runtime state ─────────────────────────────────────────────────────
            if (Application.isPlaying)
            {
                EditorGUILayout.Space(8);
                DrawRuntimeState(agent);
            }
        }

        // ── Mode help box ─────────────────────────────────────────────────────────

        private static void DrawModeHelpBox(AgentOwnershipMode mode)
        {
            if (mode == AgentOwnershipMode.BindToExisting)
                EditorGUILayout.HelpBox(
                    "BindToExisting — the agent is pre-declared on the backend (sim.yaml). " +
                    "Unity binds this visual shell to it. No registration RPC is sent. " +
                    "Capabilities and brain are owned by the backend.",
                    MessageType.Info);
            else
                EditorGUILayout.HelpBox(
                    "CreateAtRuntime — Unity owns this agent. " +
                    "Registered with the backend on connect; unregistered on destroy. " +
                    "Set a Role or provide Capabilities and Brain Class below.",
                    MessageType.Info);
        }

        // ── Validation notices ────────────────────────────────────────────────────

        private void DrawValidationNotices(BiomataAgent agent, AgentOwnershipMode mode)
        {
            bool anyNotice = false;

            // ── 1. Empty agent ID ─────────────────────────────────────────────────
            bool idEmpty = string.IsNullOrEmpty(_agentId?.stringValue);
            if (idEmpty)
            {
                EditorGUILayout.HelpBox(
                    mode == AgentOwnershipMode.BindToExisting
                        ? "Agent ID is empty. Set it to match the backend agent exactly."
                        : "Agent ID is empty. Assign a unique ID or leave blank to auto-generate.",
                    MessageType.Warning);
                anyNotice = true;
            }

            // ── 2. Duplicate agent ID in scene ────────────────────────────────────
            if (!idEmpty)
            {
                var id  = _agentId.stringValue;
                var all = FindObjectsByType<BiomataAgent>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                foreach (var other in all)
                {
                    if (other == null || other == (BiomataAgent)target) continue;
                    var otherSo = new SerializedObject(other);
                    var otherId = otherSo.FindProperty("agentId")?.stringValue;
                    if (otherId == id)
                    {
                        EditorGUILayout.HelpBox(
                            $"Duplicate Agent ID '{id}' on '{other.name}'. " +
                            "IDs must be unique within the simulation.",
                            MessageType.Error);
                        anyNotice = true;
                        break;
                    }
                }
            }

            // ── 3. CreateAtRuntime: no brain and no role ──────────────────────────
            if (mode == AgentOwnershipMode.CreateAtRuntime)
            {
                bool brainEmpty = string.IsNullOrEmpty(_brainClass?.stringValue);
                bool roleEmpty  = string.IsNullOrEmpty(_role?.stringValue);
                if (brainEmpty && roleEmpty)
                {
                    EditorGUILayout.HelpBox(
                        "Brain Class is required in CreateAtRuntime mode. " +
                        "Set a fully-qualified Python brain class path, or assign a Role that includes a brain definition.",
                        MessageType.Warning);
                    anyNotice = true;
                }
            }

            // ── 4. Unknown role name ──────────────────────────────────────────────
            if (mode == AgentOwnershipMode.CreateAtRuntime)
            {
                var roleName = _role?.stringValue;
                if (!string.IsNullOrEmpty(roleName))
                {
                    if (RoleManifestLoader.IsLoaded)
                    {
                        if (RoleManifestLoader.FindRole(roleName) == null)
                        {
                            EditorGUILayout.HelpBox(
                                $"Role '{roleName}' is not in BiomataRoles.json. " +
                                "Check the spelling or regenerate the manifest:\n" +
                                "Biomata > Validate Roles",
                                MessageType.Error);
                            anyNotice = true;
                        }
                    }
                    else
                    {
                        EditorGUILayout.HelpBox(
                            "BiomataRoles.json not found in Resources — role name cannot be validated.\n" +
                            "Place the file in a Resources folder.",
                            MessageType.Info);
                        anyNotice = true;
                    }
                }
            }

            // ── 5. Missing handlers for visible actions ───────────────────────────
            var manifest = ActionManifestLoader.Load();
            if (manifest?.actions != null)
            {
                var caps     = CollectEffectiveCapabilities();
                var go       = ((BiomataAgent)target).gameObject;
                var handlers = go.GetComponents<ActionHandlerBase>();
                var covered  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var h in handlers)
                    foreach (var n in h.DeclaredActionNames)
                        covered.Add(n.ToLowerInvariant());

                var missing = new List<string>();
                foreach (var action in manifest.actions)
                    if (CanAgentSeeAction(action, caps)
                        && !covered.Contains(action.name.ToLowerInvariant()))
                        missing.Add(action.name);

                if (missing.Count > 0)
                {
                    EditorGUILayout.HelpBox(
                        $"Missing handlers for {missing.Count} visible action(s): " +
                        string.Join(", ", missing) + "\n" +
                        "Add ActionHandlerBase component(s) to this GameObject.",
                        MessageType.Warning);
                    anyNotice = true;
                }
            }

            if (anyNotice) EditorGUILayout.Space(4);
        }

        // ── Role-derived capabilities hint ────────────────────────────────────────

        private void DrawRoleDerivedCapabilitiesHint()
        {
            var roleName = _role?.stringValue;
            if (string.IsNullOrEmpty(roleName)) return;

            var roleEntry = RoleManifestLoader.FindRole(roleName);
            if (roleEntry?.capabilities == null || roleEntry.capabilities.Length == 0) return;

            using (new EditorGUI.IndentLevelScope(1))
                EditorGUILayout.HelpBox(
                    $"Role '{roleName}' adds: {string.Join(", ", roleEntry.capabilities)}\n" +
                    "These are unioned with any capabilities listed above.",
                    MessageType.None);
        }

        // ── Brain source hint ─────────────────────────────────────────────────────

        private void DrawBrainSourceHint()
        {
            if (!string.IsNullOrEmpty(_brainClass?.stringValue)) return;

            var roleName = _role?.stringValue;
            if (string.IsNullOrEmpty(roleName)) return;

            var roleEntry = RoleManifestLoader.FindRole(roleName);
            if (roleEntry == null) return;

            string src;
            if (!string.IsNullOrEmpty(roleEntry.brain_class))
                src = $"Role '{roleName}': {roleEntry.brain_class}";
            else if (!string.IsNullOrEmpty(roleEntry.brain_provider))
                src = $"Role '{roleName}': provider={roleEntry.brain_provider} (resolved on backend)";
            else
                return;

            using (new EditorGUI.IndentLevelScope(1))
                EditorGUILayout.HelpBox($"Brain will be supplied by {src}", MessageType.None);
        }

        // ── Action Coverage panel ─────────────────────────────────────────────────

        private void DrawActionCoverage(AgentOwnershipMode mode)
        {
            EditorGUILayout.Space(6);
            _coverageFoldout = EditorGUILayout.Foldout(
                _coverageFoldout, "Action Coverage", true, EditorStyles.foldoutHeader);

            if (!_coverageFoldout) return;

            var manifest = ActionManifestLoader.Load();
            if (manifest?.actions == null || manifest.actions.Length == 0)
            {
                using (new EditorGUI.IndentLevelScope(1))
                    EditorGUILayout.HelpBox(
                        "BiomataActions.json not found in Resources.\n" +
                        "Run: Biomata > Validate Action Manifest",
                        MessageType.Info);
                return;
            }

            var caps     = CollectEffectiveCapabilities();
            var go       = ((BiomataAgent)target).gameObject;
            var handlers = go.GetComponents<ActionHandlerBase>();

            var coveredActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in handlers)
                foreach (var n in h.DeclaredActionNames)
                    coveredActions.Add(n.ToLowerInvariant());

            var visible = new List<ManifestActionEntry>();
            var gated   = new List<ManifestActionEntry>();
            foreach (var action in manifest.actions)
            {
                if (CanAgentSeeAction(action, caps))
                    visible.Add(action);
                else
                    gated.Add(action);
            }

            int missingCount = 0;

            using (new EditorGUI.IndentLevelScope(1))
            {
                // Capability summary
                if (caps.Count == 0)
                {
                    if (mode == AgentOwnershipMode.BindToExisting)
                        EditorGUILayout.LabelField(
                            "Capabilities determined by sim.yaml (BindToExisting).",
                            EditorStyles.miniLabel);
                    else
                        EditorGUILayout.HelpBox(
                            "No capabilities set. Only universal actions are visible.\n" +
                            "Set capabilities or assign a role to unlock gated actions.",
                            MessageType.Info);
                }
                else
                {
                    EditorGUILayout.LabelField(
                        $"Capabilities ({caps.Count}): {string.Join(", ", caps)}",
                        EditorStyles.miniLabel);
                }

                EditorGUILayout.Space(4);

                // Visible actions
                if (visible.Count == 0)
                {
                    EditorGUILayout.LabelField("No visible actions.", EditorStyles.miniLabel);
                }
                else
                {
                    EditorGUILayout.LabelField(
                        $"Visible to this agent ({visible.Count}):",
                        EditorStyles.boldLabel);

                    foreach (var action in visible)
                    {
                        bool handled = coveredActions.Contains(action.name.ToLowerInvariant());
                        DrawActionRow(action, handled, handlers);
                        if (!handled) missingCount++;
                    }
                }

                // Gated actions (collapsible)
                if (gated.Count > 0)
                {
                    EditorGUILayout.Space(4);
                    _gatedFoldout = EditorGUILayout.Foldout(
                        _gatedFoldout,
                        $"Gated — capability not held ({gated.Count})",
                        false,
                        EditorStyles.foldout);

                    if (_gatedFoldout)
                    {
                        using (new EditorGUI.IndentLevelScope(1))
                        {
                            foreach (var action in gated)
                            {
                                var req = action.required_capabilities != null
                                    ? string.Join(", ", action.required_capabilities)
                                    : "";
                                EditorGUILayout.LabelField(
                                    $"○  {action.name}",
                                    $"needs: {req}",
                                    EditorStyles.miniLabel);
                            }
                        }
                    }
                }
            }

            EditorGUILayout.Space(4);

            if (missingCount > 0)
                EditorGUILayout.HelpBox(
                    $"{missingCount} visible action(s) have no handler on this GameObject. " +
                    "Add ActionHandlerBase component(s), or run Biomata > Validate Action Manifest " +
                    "for a project-wide report.",
                    MessageType.Warning);
            else if (visible.Count > 0)
                EditorGUILayout.HelpBox(
                    $"All {visible.Count} visible action(s) have handlers.  ✓",
                    MessageType.None);

            EditorGUILayout.Space(2);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Refresh", GUILayout.Width(70)))
                    ActionManifestLoader.ClearCache();

                if (GUILayout.Button("Validate Scene"))
                    Biomata.Editor.ActionManifestValidator.Validate();
            }
        }

        private static void DrawActionRow(
            ManifestActionEntry action,
            bool handled,
            ActionHandlerBase[] handlers)
        {
            var rect  = EditorGUILayout.GetControlRect(false, 18f);
            var left  = new Rect(rect.x, rect.y, rect.width * 0.52f, rect.height);
            var right = new Rect(rect.x + rect.width * 0.52f, rect.y, rect.width * 0.48f, rect.height);

            var prevColor = GUI.color;
            if (handled)
            {
                var handlerName = FindHandlerName(handlers, action.name);
                GUI.color = new Color(0.3f, 0.88f, 0.45f);
                EditorGUI.LabelField(left, $"✓  {action.name}");
                GUI.color = prevColor;
                EditorGUI.LabelField(right, handlerName ?? "covered", EditorStyles.miniLabel);
            }
            else
            {
                GUI.color = new Color(1f, 0.38f, 0.28f);
                EditorGUI.LabelField(left, $"✗  {action.name}");
                GUI.color = prevColor;
                EditorGUI.LabelField(right, "no handler", EditorStyles.miniLabel);
            }
        }

        // ── Runtime state ─────────────────────────────────────────────────────────

        private static void DrawRuntimeState(BiomataAgent agent)
        {
            EditorGUILayout.LabelField("Runtime State", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(true))
            {
                var prevColor  = GUI.color;
                GUI.color      = agent.IsRegistered ? new Color(0.3f, 1f, 0.4f) : Color.yellow;
                var stateLabel = agent.OwnershipMode == AgentOwnershipMode.BindToExisting
                    ? (agent.IsRegistered ? "●  Bound"        : "○  Not bound")
                    : (agent.IsRegistered ? "●  Registered"  : "○  Not registered");
                EditorGUILayout.LabelField(stateLabel, EditorStyles.boldLabel);
                GUI.color = prevColor;

                EditorGUILayout.TextField("Mode",         agent.OwnershipMode.ToString());
                EditorGUILayout.TextField("Resolved ID",  agent.AgentId             ?? "—");
                EditorGUILayout.TextField("Display Name", agent.DisplayName          ?? "—");
                EditorGUILayout.TextField("Role",         agent.RoleForValidation    ?? "—");

                var d = agent.LastDecision;
                if (d != null)
                {
                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField("Last Decision", EditorStyles.boldLabel);
                    EditorGUILayout.TextField("Action",  d.Action      ?? "—");
                    EditorGUILayout.TextField("Outcome", d.OutcomeText ?? "—");

                    if (!d.IsSuccess && !string.IsNullOrEmpty(d.Error))
                    {
                        prevColor = GUI.color;
                        GUI.color = Color.red;
                        EditorGUILayout.TextField("Error", d.Error);
                        GUI.color = prevColor;
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

        // ── Helpers ───────────────────────────────────────────────────────────────

        private HashSet<string> CollectEffectiveCapabilities()
        {
            var caps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (_capabilities != null)
                for (int i = 0; i < _capabilities.arraySize; i++)
                {
                    var v = _capabilities.GetArrayElementAtIndex(i).stringValue;
                    if (!string.IsNullOrEmpty(v)) caps.Add(v);
                }

            var roleName = _role?.stringValue;
            if (!string.IsNullOrEmpty(roleName))
            {
                var roleEntry = RoleManifestLoader.FindRole(roleName);
                if (roleEntry?.capabilities != null)
                    foreach (var c in roleEntry.capabilities)
                        if (!string.IsNullOrEmpty(c)) caps.Add(c);
            }

            return caps;
        }

        private static bool CanAgentSeeAction(ManifestActionEntry action, HashSet<string> caps)
        {
            if (action.required_capabilities == null || action.required_capabilities.Length == 0)
                return true;
            foreach (var req in action.required_capabilities)
                if (caps.Contains(req)) return true;
            return false;
        }

        private static string FindHandlerName(ActionHandlerBase[] handlers, string actionName)
        {
            foreach (var h in handlers)
            {
                if (h == null) continue;
                foreach (var n in h.DeclaredActionNames)
                    if (string.Equals(n, actionName, StringComparison.OrdinalIgnoreCase))
                        return h.GetType().Name;
            }
            return null;
        }

        public override bool RequiresConstantRepaint() => Application.isPlaying;
    }
}
