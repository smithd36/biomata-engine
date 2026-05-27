using Biomata.Integration;
using UnityEditor;
using UnityEngine;

namespace Biomata.Integration.Editor
{
    [CustomEditor(typeof(BiomataSimulationBootstrapper))]
    public class BiomataSimulationBootstrapperEditor : UnityEditor.Editor
    {
        // Config asset + override flags
        private SerializedProperty _config;
        private SerializedProperty _overrideConnection;
        private SerializedProperty _overrideSimulation;
        private SerializedProperty _overrideReconnect;
        private SerializedProperty _overrideDebug;

        // Connection group
        private SerializedProperty _host;
        private SerializedProperty _port;
        private SerializedProperty _useTls;
        private SerializedProperty _connectTimeoutSeconds;

        // Simulation group
        private SerializedProperty _autoConnect;
        private SerializedProperty _autoTick;
        private SerializedProperty _tickRate;
        private SerializedProperty _tickInFixedUpdate;

        // Reconnect group
        private SerializedProperty _autoReconnect;
        private SerializedProperty _reconnectDelay;

        // Debug group
        private SerializedProperty _debugLogging;

        private void OnEnable()
        {
            _config             = serializedObject.FindProperty("config");
            _overrideConnection = serializedObject.FindProperty("overrideConnection");
            _overrideSimulation = serializedObject.FindProperty("overrideSimulation");
            _overrideReconnect  = serializedObject.FindProperty("overrideReconnect");
            _overrideDebug      = serializedObject.FindProperty("overrideDebug");

            _host                  = serializedObject.FindProperty("host");
            _port                  = serializedObject.FindProperty("port");
            _useTls                = serializedObject.FindProperty("useTls");
            _connectTimeoutSeconds = serializedObject.FindProperty("connectTimeoutSeconds");

            _autoConnect      = serializedObject.FindProperty("autoConnect");
            _autoTick         = serializedObject.FindProperty("autoTick");
            _tickRate         = serializedObject.FindProperty("tickRate");
            _tickInFixedUpdate = serializedObject.FindProperty("tickInFixedUpdate");

            _autoReconnect = serializedObject.FindProperty("autoReconnect");
            _reconnectDelay = serializedObject.FindProperty("reconnectDelay");

            _debugLogging = serializedObject.FindProperty("debugLogging");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var bs        = (BiomataSimulationBootstrapper)target;
            var cfgAsset  = _config.objectReferenceValue as BiomataSimulationConfig;
            bool hasConfig = cfgAsset != null;

            // ── Config Asset ──────────────────────────────────────────────────────

            EditorGUILayout.LabelField("Configuration Asset", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_config, new GUIContent("Config Asset",
                "Optional shared ScriptableObject. When assigned, all settings are " +
                "read from the asset. Enable Override to replace individual groups " +
                "with the inline Inspector values below."));

            if (hasConfig)
            {
                EditorGUILayout.HelpBox(
                    "Settings are driven by the config asset. Enable an Override toggle " +
                    "to use the inline values for that group instead.",
                    MessageType.Info);
            }

            EditorGUILayout.Space(6);

            // ── Connection ────────────────────────────────────────────────────────

            DrawGroupHeader("Connection", _overrideConnection, hasConfig);
            bool showConnection = !hasConfig || _overrideConnection.boolValue;
            if (showConnection)
            {
                using (new EditorGUI.IndentLevelScope(1))
                {
                    EditorGUILayout.PropertyField(_host,                  new GUIContent("Host"));
                    EditorGUILayout.PropertyField(_port,                  new GUIContent("Port"));
                    EditorGUILayout.PropertyField(_useTls,                new GUIContent("Use TLS"));
                    EditorGUILayout.PropertyField(_connectTimeoutSeconds, new GUIContent("Connect Timeout (s)"));
                }
            }
            else
            {
                DrawConfigPreview(() =>
                {
                    EditorGUILayout.LabelField("Host",               cfgAsset.host);
                    EditorGUILayout.LabelField("Port",               cfgAsset.port.ToString());
                    EditorGUILayout.LabelField("Use TLS",            cfgAsset.useTls.ToString());
                    EditorGUILayout.LabelField("Connect Timeout (s)", cfgAsset.connectTimeoutSeconds.ToString("F1"));
                });
            }

            EditorGUILayout.Space(4);

            // ── Simulation ────────────────────────────────────────────────────────

            DrawGroupHeader("Simulation", _overrideSimulation, hasConfig);
            bool showSimulation = !hasConfig || _overrideSimulation.boolValue;
            if (showSimulation)
            {
                using (new EditorGUI.IndentLevelScope(1))
                {
                    EditorGUILayout.PropertyField(_autoConnect,       new GUIContent("Auto Connect"));
                    EditorGUILayout.PropertyField(_autoTick,          new GUIContent("Auto Tick"));
                    EditorGUILayout.PropertyField(_tickRate,          new GUIContent("Tick Rate"));
                    EditorGUILayout.PropertyField(_tickInFixedUpdate, new GUIContent("Tick In Fixed Update"));
                }
            }
            else
            {
                DrawConfigPreview(() =>
                {
                    EditorGUILayout.LabelField("Auto Connect",         cfgAsset.autoConnect.ToString());
                    EditorGUILayout.LabelField("Auto Tick",            cfgAsset.autoTick.ToString());
                    EditorGUILayout.LabelField("Tick Rate",            cfgAsset.tickRate.ToString("F2") + " /s");
                    EditorGUILayout.LabelField("Tick In Fixed Update", cfgAsset.tickInFixedUpdate.ToString());
                });
            }

            EditorGUILayout.Space(4);

            // ── Reconnect ─────────────────────────────────────────────────────────

            DrawGroupHeader("Reconnect", _overrideReconnect, hasConfig);
            bool showReconnect = !hasConfig || _overrideReconnect.boolValue;
            if (showReconnect)
            {
                using (new EditorGUI.IndentLevelScope(1))
                {
                    EditorGUILayout.PropertyField(_autoReconnect, new GUIContent("Auto Reconnect"));
                    EditorGUILayout.PropertyField(_reconnectDelay, new GUIContent("Reconnect Delay (s)"));
                }
            }
            else
            {
                DrawConfigPreview(() =>
                {
                    EditorGUILayout.LabelField("Auto Reconnect",     cfgAsset.autoReconnect.ToString());
                    EditorGUILayout.LabelField("Reconnect Delay (s)", cfgAsset.reconnectDelay.ToString("F1"));
                });
            }

            EditorGUILayout.Space(4);

            // ── Debug ─────────────────────────────────────────────────────────────

            DrawGroupHeader("Debug", _overrideDebug, hasConfig);
            bool showDebug = !hasConfig || _overrideDebug.boolValue;
            if (showDebug)
            {
                using (new EditorGUI.IndentLevelScope(1))
                    EditorGUILayout.PropertyField(_debugLogging, new GUIContent("Debug Logging"));
            }
            else
            {
                DrawConfigPreview(() =>
                    EditorGUILayout.LabelField("Debug Logging", cfgAsset.debugLogging.ToString()));
            }

            serializedObject.ApplyModifiedProperties();

            // ── Runtime State ─────────────────────────────────────────────────────

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Runtime State", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.Toggle("Connected",    Application.isPlaying && bs.IsConnected);
                EditorGUILayout.Toggle("Auto Ticking", Application.isPlaying && bs.IsAutoTicking);
                EditorGUILayout.Toggle("Paused",       Application.isPlaying && bs.IsPaused);

                if (Application.isPlaying)
                    EditorGUILayout.FloatField("Last Tick (ms)", bs.LastTickDurationMs);
            }

            if (!Application.isPlaying) return;

            // ── Play-mode Controls ────────────────────────────────────────────────

            EditorGUILayout.Space(4);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(bs.IsConnected))
                    if (GUILayout.Button("Connect"))    bs.Connect();

                using (new EditorGUI.DisabledScope(!bs.IsConnected))
                    if (GUILayout.Button("Disconnect")) bs.Disconnect();
            }

            EditorGUILayout.Space(2);

            using (new EditorGUI.DisabledScope(!bs.IsConnected))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(bs.IsAutoTicking))
                        if (GUILayout.Button("Start Auto")) bs.StartAutoTick();

                    using (new EditorGUI.DisabledScope(!bs.IsAutoTicking))
                        if (GUILayout.Button("Stop Auto"))  bs.StopAutoTick();
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Force Tick"))
                        bs.ForceTick();

                    if (GUILayout.Button(bs.IsPaused ? "Resume" : "Pause"))
                        bs.SetPaused(!bs.IsPaused);
                }
            }

            if (bs.Manager != null)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Managed USM", EditorStyles.boldLabel);

                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField(
                        "Manager", bs.Manager, typeof(UnitySimulationManager), true);

                    if (Application.isPlaying)
                        EditorGUILayout.IntField("Last Tick", bs.Manager.LastTick);
                }
            }
        }

        public override bool RequiresConstantRepaint() => Application.isPlaying;

        // ── Helpers ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Draws the group header label. When a config asset is assigned, also draws
        /// the Override toggle inline with the header.
        /// </summary>
        private static void DrawGroupHeader(
            string label,
            SerializedProperty overrideProp,
            bool hasConfig)
        {
            if (hasConfig)
            {
                var rect  = EditorGUILayout.GetControlRect();
                var left  = new Rect(rect.x, rect.y, rect.width * 0.6f, rect.height);
                var right = new Rect(rect.x + rect.width * 0.6f, rect.y, rect.width * 0.4f, rect.height);

                EditorGUI.LabelField(left, label, EditorStyles.boldLabel);

                bool before = overrideProp.boolValue;
                bool after  = EditorGUI.ToggleLeft(right,
                    new GUIContent("Override", "Use the inline Inspector values for this group instead of the config asset."),
                    before);
                if (after != before)
                    overrideProp.boolValue = after;
            }
            else
            {
                EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            }
        }

        /// <summary>
        /// Draws read-only preview fields showing values sourced from the config asset.
        /// </summary>
        private static void DrawConfigPreview(System.Action draw)
        {
            using (new EditorGUI.DisabledScope(true))
            using (new EditorGUI.IndentLevelScope(1))
            {
                var style = new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(6, 6, 4, 4) };
                EditorGUILayout.BeginVertical(style);
                draw();
                EditorGUILayout.EndVertical();
            }
        }
    }
}
