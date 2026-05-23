using UnityEditor;
using UnityEngine;

namespace Biomata.Integration.Editor
{
    [CustomEditor(typeof(UnityAgentBridge))]
    public class UnityAgentBridgeEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var bridge = (UnityAgentBridge)target;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Runtime State", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(true))
            {
                if (Application.isPlaying)
                {
                    // Connection status badge
                    var prevColor = GUI.color;
                    GUI.color     = bridge.IsRegistered ? new Color(0.3f, 1f, 0.4f) : Color.yellow;
                    EditorGUILayout.LabelField(
                        bridge.IsRegistered ? "● Registered" : "○ Not registered",
                        EditorStyles.boldLabel);
                    GUI.color = prevColor;

                    if (bridge.LastDecision != null)
                    {
                        EditorGUILayout.Space(2);
                        EditorGUILayout.LabelField("Last Decision", EditorStyles.boldLabel);
                        EditorGUILayout.TextField("Action",  bridge.LastDecision.Action ?? "—");
                        EditorGUILayout.TextField("Outcome", bridge.LastDecision.OutcomeText ?? "—");

                        if (!bridge.LastDecision.IsSuccess)
                        {
                            GUI.color = Color.red;
                            EditorGUILayout.TextField("Error", bridge.LastDecision.Error);
                            GUI.color = Color.white;
                        }
                    }
                }
            }

            if (!Application.isPlaying) return;

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(bridge.IsRegistered))
                    if (GUILayout.Button("Register"))   bridge.Register();

                using (new EditorGUI.DisabledScope(!bridge.IsRegistered))
                    if (GUILayout.Button("Unregister")) bridge.Unregister();
            }
        }

        public override bool RequiresConstantRepaint() => Application.isPlaying;
    }
}
