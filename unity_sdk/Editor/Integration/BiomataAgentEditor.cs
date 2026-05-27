using Biomata.Integration;
using UnityEditor;
using UnityEngine;

namespace Biomata.Integration.Editor
{
    [CustomEditor(typeof(BiomataAgent))]
    public class BiomataAgentEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var agent = (BiomataAgent)target;

            // Edit-time validation notices drawn above the default inspector.
            DrawValidationNotices(agent);

            DrawDefaultInspector();

            EditorGUILayout.Space(8);

            if (Application.isPlaying)
                DrawRuntimeState(agent);
        }

        private static void DrawValidationNotices(BiomataAgent agent)
        {
            // Access the raw serialized fields for edit-time checks.
            var so        = new SerializedObject(agent);
            var idProp    = so.FindProperty("agentId");
            var brainProp = so.FindProperty("brainClass");

            bool idEmpty    = string.IsNullOrEmpty(idProp?.stringValue);
            bool brainEmpty = string.IsNullOrEmpty(brainProp?.stringValue);

            if (idEmpty)
                EditorGUILayout.HelpBox(
                    "Agent ID is empty. Assign a unique ID matching your backend config.",
                    MessageType.Warning);

            if (brainEmpty)
                EditorGUILayout.HelpBox(
                    "Brain Class is empty. Set a fully-qualified Python brain class path " +
                    "(e.g. src.plugins.builtin.ollama.brain.OllamaLLMBrain).",
                    MessageType.Warning);

            if (!idEmpty)
            {
                // Duplicate ID check
                var id  = idProp.stringValue;
                var all = FindObjectsByType<BiomataAgent>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                foreach (var other in all)
                {
                    if (other == null || other == (BiomataAgent)agent) continue;
                    // Compare raw field via serialized object
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

            if (idEmpty || brainEmpty)
                EditorGUILayout.Space(4);
        }

        private static void DrawRuntimeState(BiomataAgent agent)
        {
            EditorGUILayout.LabelField("Runtime State", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(true))
            {
                // Registration badge
                var prevColor = GUI.color;
                GUI.color     = agent.IsRegistered ? new Color(0.3f, 1f, 0.4f) : Color.yellow;
                EditorGUILayout.LabelField(
                    agent.IsRegistered ? "● Registered" : "○ Not registered",
                    EditorStyles.boldLabel);
                GUI.color = prevColor;

                EditorGUILayout.TextField("Resolved ID",   agent.AgentId    ?? "—");
                EditorGUILayout.TextField("Display Name",  agent.DisplayName ?? "—");

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
                    using (new EditorGUI.DisabledScope(agent.IsRegistered))
                        if (GUILayout.Button("Register"))   agent.Bridge.Register();

                    using (new EditorGUI.DisabledScope(!agent.IsRegistered))
                        if (GUILayout.Button("Unregister")) agent.Bridge.Unregister();
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
