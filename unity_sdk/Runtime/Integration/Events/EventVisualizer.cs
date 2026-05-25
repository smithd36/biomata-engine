using System.Collections.Generic;
using Biomata.Integration.Actions;
using Biomata.SDK.Models;
using UnityEngine;

namespace Biomata.Integration
{
    /// <summary>
    /// Optional in-scene debug overlay and Gizmo renderer for simulation state.
    /// Add to any GameObject in the scene to activate.
    ///
    /// Features:
    /// <list type="bullet">
    ///   <item>HUD overlay — connection state, tick counter, recent event log.</item>
    ///   <item>Speech bubbles — drawn above any agent with an active <see cref="SpeakActionHandler"/>.</item>
    ///   <item>Interaction lines — drawn in the Scene view between agents that interacted.</item>
    /// </list>
    ///
    /// Toggle the HUD with <see cref="toggleKey"/> (default F2) at runtime.
    /// </summary>
    [AddComponentMenu("Biomata/Event Visualizer")]
    public class EventVisualizer : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────────

        [Header("HUD")]
        [SerializeField] private bool    showOverlay   = true;
        [SerializeField] private KeyCode toggleKey     = KeyCode.F2;
        [SerializeField] private int     maxEventLines = 12;

        [Header("Scene")]
        [Tooltip("Render speech bubble labels above speaking agents.")]
        [SerializeField] private bool showSpeechBubbles   = true;
        [Tooltip("Draw lines between agents linked by interact engine_commands.")]
        [SerializeField] private bool showInteractionLines = true;

        /// <summary>Configure visualizer parameters at runtime (call immediately after AddComponent).</summary>
        public void Configure(
            bool    showOverlay        = true,
            bool    showSpeechBubbles  = true,
            bool    showInteractionLines = true,
            int     maxEventLines      = 12,
            KeyCode toggleKey          = KeyCode.F2)
        {
            this.showOverlay         = showOverlay;
            this.showSpeechBubbles   = showSpeechBubbles;
            this.showInteractionLines = showInteractionLines;
            this.maxEventLines       = maxEventLines;
            this.toggleKey           = toggleKey;
        }

        // ── Private ───────────────────────────────────────────────────────────────

        private UnitySimulationManager _manager;
        private readonly List<EventLogEntry> _log = new List<EventLogEntry>();

        private GUIStyle _labelStyle;
        private GUIStyle _headerStyle;

        private readonly struct EventLogEntry
        {
            public readonly float  Time;
            public readonly string Source;
            public readonly string Message;

            public EventLogEntry(string source, string message)
            {
                Time    = UnityEngine.Time.time;
                Source  = source;
                Message = message;
            }
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────────

        private void Start()
        {
            _manager = UnitySimulationManager.Instance ?? FindFirstObjectByType<UnitySimulationManager>();
            if (_manager == null) return;

            _manager.OnTickComplete    += HandleTickComplete;
            _manager.OnSimulationEvent += HandleSimulationEvent;
            _manager.OnConnected       += () => AppendLog("system", "Connected");
            _manager.OnDisconnected    += () => AppendLog("system", "Disconnected");
        }

        private void OnDestroy()
        {
            if (_manager == null) return;
            _manager.OnTickComplete    -= HandleTickComplete;
            _manager.OnSimulationEvent -= HandleSimulationEvent;
        }

        private void Update()
        {
            if (Input.GetKeyDown(toggleKey))
                showOverlay = !showOverlay;
        }

        // ── Event handlers ────────────────────────────────────────────────────────

        private void HandleTickComplete(TickResult result)
        {
            foreach (var d in result.Decisions)
            {
                var summary = string.IsNullOrEmpty(d.OutcomeText)
                    ? d.Action
                    : $"{d.Action} — {d.OutcomeText}";
                AppendLog(d.AgentId, summary);
            }

            foreach (var (agentId, message) in result.Errors)
                AppendLog(agentId, $"ERROR: {message}");
        }

        private void HandleSimulationEvent(SimulationEvent ev) =>
            AppendLog(ev.AgentId ?? "engine", $"[{ev.EventType}]");

        private void AppendLog(string source, string message)
        {
            _log.Insert(0, new EventLogEntry(source, message));
            while (_log.Count > maxEventLines * 2)
                _log.RemoveAt(_log.Count - 1);
        }

        // ── OnGUI ─────────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            EnsureStyles();

            if (showSpeechBubbles)
                DrawSpeechBubbles();

            if (!showOverlay) return;

            DrawStatusBar();
            DrawEventLog();
        }

        private void DrawStatusBar()
        {
            var connected = _manager?.IsConnected == true;
            var prev      = GUI.color;
            GUI.color     = connected ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 0.35f, 0.35f);

            var label = connected
                ? $"BIOMATA  tick={_manager.LastTick}  agents={_manager.RegisteredBridges.Count}"
                : "BIOMATA  DISCONNECTED";

            GUI.Box(new Rect(10, 10, 330, 26), label, _headerStyle);
            GUI.color = prev;
        }

        private void DrawEventLog()
        {
            var count = Mathf.Min(_log.Count, maxEventLines);
            for (var i = 0; i < count; i++)
            {
                var entry   = _log[i];
                var age     = Time.time - entry.Time;
                var alpha   = Mathf.Clamp01(1f - age / 8f);
                GUI.color   = new Color(1f, 1f, 1f, alpha);
                GUI.Label(
                    new Rect(12, 40f + i * 17f, 440, 17),
                    $"[{entry.Source}] {entry.Message}",
                    _labelStyle);
            }
            GUI.color = Color.white;
        }

        private void DrawSpeechBubbles()
        {
            if (Camera.main == null || _manager == null) return;

            foreach (var bridge in _manager.RegisteredBridges)
            {
                if (bridge == null) continue;
                var speaker = bridge.GetComponent<SpeakActionHandler>();
                if (speaker == null || !speaker.IsSpeaking || string.IsNullOrEmpty(speaker.CurrentSpeech))
                    continue;

                var worldPos  = bridge.transform.position + new Vector3(0f, 2.5f, 0f);
                var screenPos = Camera.main.WorldToScreenPoint(worldPos);
                if (screenPos.z < 0f) continue;

                var rect = new Rect(screenPos.x - 85f, Screen.height - screenPos.y - 22f, 170f, 26f);
                GUI.Box(rect, $"\"{speaker.CurrentSpeech}\"", _labelStyle);
            }
        }

        // ── Gizmos ────────────────────────────────────────────────────────────────

        private void OnDrawGizmos()
        {
            if (!showInteractionLines || _manager?.LastTickResult == null) return;

            Gizmos.color = new Color(1f, 0.6f, 0f, 0.7f);

            foreach (var (agentId, cmd) in _manager.LastTickResult.AllCommands)
            {
                if (!cmd.TryGetValue("type", out var typeVal) || typeVal?.ToString() != "interact")
                    continue;
                if (!cmd.TryGetValue("target", out var targetVal)) continue;

                var src = FindAgentPosition(agentId);
                var dst = FindAgentPosition(targetVal?.ToString());
                if (src == null || dst == null) continue;

                Gizmos.DrawLine(src.Value, dst.Value);
                Gizmos.DrawWireSphere(dst.Value, 0.25f);
            }
        }

        private Vector3? FindAgentPosition(string agentId)
        {
            if (string.IsNullOrEmpty(agentId) || _manager == null) return null;
            foreach (var bridge in _manager.RegisteredBridges)
            {
                if (bridge?.AgentId == agentId)
                    return bridge.transform.position;
            }
            return null;
        }

        // ── Style ─────────────────────────────────────────────────────────────────

        private void EnsureStyles()
        {
            if (_labelStyle != null) return;

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize  = 12,
                alignment = TextAnchor.MiddleLeft,
                normal    = { textColor = Color.white },
            };
            _headerStyle = new GUIStyle(GUI.skin.box)
            {
                fontSize  = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal    = { textColor = Color.white },
            };
        }
    }
}
