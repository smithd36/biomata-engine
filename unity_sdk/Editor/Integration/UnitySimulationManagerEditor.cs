using UnityEditor;
using UnityEngine;

namespace Biomata.Integration.Editor
{
    [CustomEditor(typeof(UnitySimulationManager))]
    public class UnitySimulationManagerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var manager = (UnitySimulationManager)target;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Runtime State", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.Toggle("Connected", Application.isPlaying && manager.IsConnected);

                if (Application.isPlaying)
                {
                    EditorGUILayout.IntField("Last Tick", manager.LastTick);
                    EditorGUILayout.IntField("Registered Agents", manager.RegisteredBridges.Count);

                    if (manager.LastTickResult != null)
                    {
                        EditorGUILayout.IntField(
                            "Tick Decisions", manager.LastTickResult.Decisions.Count);
                        EditorGUILayout.IntField(
                            "Tick Errors",    manager.LastTickResult.Errors.Count);
                    }
                }
            }

            if (!Application.isPlaying) return;

            EditorGUILayout.Space(4);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(manager.IsConnected))
                    if (GUILayout.Button("Connect"))    manager.Connect();

                using (new EditorGUI.DisabledScope(!manager.IsConnected))
                    if (GUILayout.Button("Disconnect")) manager.Disconnect();
            }

            using (new EditorGUI.DisabledScope(!manager.IsConnected))
            {
                if (GUILayout.Button("Force Tick"))
                    manager.ForceTick();
            }

            // Registered agents list
            if (manager.RegisteredBridges.Count > 0)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Registered Bridges", EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(true))
                {
                    foreach (var bridge in manager.RegisteredBridges)
                    {
                        if (bridge == null) continue;
                        EditorGUILayout.ObjectField(
                            $"{bridge.AgentId}", bridge, typeof(UnityAgentBridge), true);
                    }
                }
            }
        }

        public override bool RequiresConstantRepaint() => Application.isPlaying;
    }
}
