// Biomata.SDK — BiomataManagerEditor.cs
// Custom Inspector for BiomataManager — adds a live status display,
// a connect/disconnect button, and a health-check button.

#if UNITY_EDITOR
using System;
using Biomata.SDK;
using Biomata.SDK.Unity;
using UnityEditor;
using UnityEngine;

namespace Biomata.SDK.Editor
{
    [CustomEditor(typeof(BiomataManager))]
    public class BiomataManagerEditor : UnityEditor.Editor
    {
        // GUIStyles are lazy-initialised in OnInspectorGUI to avoid
        // accessing them from a static constructor (editor boot order issue).
        private GUIStyle _statusBoxStyle;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var manager = (BiomataManager)target;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Runtime Status", EditorStyles.boldLabel);

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Status is only available in Play Mode.", MessageType.Info);
                return;
            }

            // ── Connection state badge ──────────────────────────────────────
            EnsureStyles();
            var state     = manager.State;
            var stateText = state.ToString();
            var stateColor = state switch
            {
                ConnectionState.Connected     => new Color(0.2f, 0.8f, 0.3f),
                ConnectionState.Connecting    => new Color(1.0f, 0.8f, 0.0f),
                ConnectionState.Disconnecting => new Color(1.0f, 0.6f, 0.0f),
                ConnectionState.Faulted       => new Color(0.9f, 0.2f, 0.2f),
                _                             => new Color(0.6f, 0.6f, 0.6f),
            };

            var prevColor = GUI.color;
            GUI.color = stateColor;
            GUILayout.Box($"● {stateText}", _statusBoxStyle, GUILayout.ExpandWidth(true));
            GUI.color = prevColor;

            // ── Live stats ──────────────────────────────────────────────────
            if (state == ConnectionState.Connected && manager.Client != null)
            {
                var reconnects = manager.Client.Events?.ReconnectAttempts ?? 0;
                EditorGUILayout.LabelField("Event stream reconnects", reconnects.ToString());
            }

            // ── Action buttons ──────────────────────────────────────────────
            EditorGUILayout.Space(4);
            using (new EditorGUI.DisabledGroupScope(state == ConnectionState.Connected))
            {
                if (GUILayout.Button("Connect"))
                {
                    _ = manager.ConnectAsync();
                }
            }

            using (new EditorGUI.DisabledGroupScope(state != ConnectionState.Connected))
            {
                if (GUILayout.Button("Health Check"))
                {
                    _ = RunHealthCheckAsync(manager);
                }

                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Pause")) _ = manager.PauseAsync();
                if (GUILayout.Button("Resume")) _ = manager.ResumeAsync();
                GUILayout.EndHorizontal();

                if (GUILayout.Button("Disconnect"))
                {
                    _ = manager.DisconnectAsync();
                }
            }

            // Repaint while connecting so the badge updates
            if (state == ConnectionState.Connecting || state == ConnectionState.Disconnecting)
                Repaint();
        }

        private async System.Threading.Tasks.Task RunHealthCheckAsync(BiomataManager manager)
        {
            try
            {
                var status = await manager.Client.Health.CheckAsync();
                Debug.Log($"[BiomataSDK] Health: {status}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BiomataSDK] Health check failed: {ex.Message}");
            }
        }

        private void EnsureStyles()
        {
            if (_statusBoxStyle != null) return;
            _statusBoxStyle = new GUIStyle(GUI.skin.box)
            {
                fontSize  = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
        }
    }
}
#endif
