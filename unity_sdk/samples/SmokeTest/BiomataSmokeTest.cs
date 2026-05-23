// Biomata SDK — Smoke Test
//
// Drop this component on an empty GameObject in a new scene and press Play.
// The smoke test builds its own Canvas, EventSystem, and BiomataManager at
// runtime — no scene/prefab authoring required.
//
// What it verifies
// ────────────────
//   1. The SDK assembly compiles into the consuming project.
//   2. BiomataManager can be added to the scene and configured.
//   3. ConnectAsync reaches the gRPC server.
//   4. HealthCheck round-trips a request/response.
//   5. RegisterAgent successfully adds one agent.
//   6. The event stream delivers events back to the client.
//
// Prerequisites
// ─────────────
//   - Python backend running:
//        biomata-grpc --config examples/corporate/sim.yaml --port 50051
//   - Default host/port (localhost:50051) — override in the Inspector if needed.

using System;
using System.Collections.Generic;
using Biomata.SDK;
using Biomata.SDK.Models;
using Biomata.SDK.Unity;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Biomata.Samples
{
    /// <summary>
    /// One-script smoke test for the Biomata Unity SDK.
    /// </summary>
    [AddComponentMenu("Biomata/Samples/Smoke Test")]
    public class BiomataSmokeTest : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("Connection")]
        [Tooltip("Default transport for Unity 6 is WebSocket (biomata-ws, port 8765).")]
        [SerializeField] private TransportKind transport = TransportKind.WebSocket;
        [SerializeField] private string host = "localhost";
        [SerializeField] private int    port = 8765;

        [Header("Test Agent")]
        [SerializeField] private string testAgentId   = "smoke_agent_001";
        [SerializeField] private string testAgentName = "SmokeBot";
        [Tooltip("Python brain class. IdleBrain has zero dependencies — safest choice for a smoke test.")]
        [SerializeField] private string testBrainClass = "src.plugins.builtin.idle_brain.brain.IdleBrain";

        [Header("Event Log")]
        [SerializeField] private int    maxLogLines = 20;

        // ── Runtime ───────────────────────────────────────────────────────────

        private BiomataManager _manager;
        private Text           _statusLabel;
        private Text           _logLabel;
        private Button         _connectButton;
        private Button         _healthButton;
        private Button         _registerButton;
        private Button         _tickButton;
        private Button         _pauseButton;
        private Button         _resumeButton;

        private readonly Queue<string> _log = new Queue<string>();
        private bool _agentRegistered;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void Awake()
        {
            BuildUI();
            BuildManager();
        }

        private void OnDestroy()
        {
            if (_manager != null)
                _manager.OnConnectionStateChanged -= HandleConnectionStateChanged;
        }

        // ── Manager setup ─────────────────────────────────────────────────────

        private void BuildManager()
        {
            // Reuse an existing manager if the scene already has one; otherwise
            // create a dedicated top-level GameObject for it.
            _manager = BiomataManager.Instance ?? FindFirstObjectByType<BiomataManager>();

            if (_manager == null)
            {
                var go = new GameObject("BiomataManager");
                go.SetActive(false);

                _manager = go.AddComponent<BiomataManager>();
                ApplyConnectionConfig(_manager);

                go.SetActive(true);
            }

            _manager.OnConnectionStateChanged += HandleConnectionStateChanged;

            _manager.OnTickEnd += ev =>
                Log($"tick_end @ t{ev.Tick}");

            _manager.OnActionCompleted += ev =>
            {
                var action =
                    ev.Data.TryGetValue("action", out var actionObj)
                        ? actionObj?.ToString()
                        : "unknown";

                Log($"action @ t{ev.Tick}: {ev.AgentId} → {action}");
            };

            _manager.OnStreamDisconnected += ex =>
                Log($"stream disconnected: {ex?.Message ?? "(clean)"}");
        }

        private void ApplyConnectionConfig(BiomataManager mgr)
        {
            // Use reflection to set the private serialized fields. This is the
            // simplest way to drive the manager from the Inspector of a single
            // smoke-test component without subclassing BiomataManager.
            var t = typeof(BiomataManager);
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            t.GetField("transport",            flags)?.SetValue(mgr, transport);
            t.GetField("host",                 flags)?.SetValue(mgr, host);
            t.GetField("port",                 flags)?.SetValue(mgr, port);
            t.GetField("connectOnStart",       flags)?.SetValue(mgr, false);
            t.GetField("autoStartEventStream", flags)?.SetValue(mgr, true);
        }

        // ── Button handlers ───────────────────────────────────────────────────

        private async void OnConnectClicked()
        {
            SetButtonsInteractable(connect: false, health: false, register: false, tick: false, pause: false, resume: false);
            try
            {
                Log("connecting…");
                await _manager.ConnectAsync(destroyCancellationToken);
                Log("connected");
            }
            catch (Exception ex)
            {
                Log($"connect failed: {ex.Message}");
            }
            finally
            {
                RefreshButtonsForCurrentState();
            }
        }

        private async void OnHealthCheckClicked()
        {
            if (!_manager.IsConnected) { Log("not connected"); return; }
            try
            {
                var status = await _manager.Client.Health.CheckAsync(destroyCancellationToken);
                Log($"health: {status.Status} state={status.SessionState} tick={status.Tick} agents={status.AgentCount}");
            }
            catch (Exception ex)
            {
                Log($"health failed: {ex.Message}");
            }
        }

        private async void OnRegisterAgentClicked()
        {
            if (!_manager.IsConnected) { Log("not connected"); return; }
            if (_agentRegistered)      { Log($"agent '{testAgentId}' already registered"); return; }

            try
            {
                var reg = new AgentRegistration
                {
                    AgentId    = testAgentId,
                    AgentName  = testAgentName,
                    BrainClass = testBrainClass,
                };
                await _manager.Client.Agents.RegisterAsync(reg, destroyCancellationToken);
                _agentRegistered = true;
                Log($"registered: {testAgentId} ({testAgentName})");
            }
            catch (Exception ex)
            {
                Log($"register failed: {ex.Message}");
            }
        }

        private async void OnTickClicked()
        {
            try
            {
                Log("forcing tick...");

                var result = await _manager.Client.Ticks.TickAsync(
                    new List<AgentObservationData>(),
                    new Dictionary<string, object>()
                );

                Log($"tick complete: t{result.Tick}");

                foreach (var decision in result.Decisions)
                {
                    Log($"decision: {decision.AgentId} -> {decision.Action}");
                }
            }
            catch (Exception ex)
            {
                Log($"tick failed: {ex.Message}");
            }
        }

        private async void OnPauseClicked()
        {
            try
            {
                await _manager.Client.Ticks.PauseAsync();
                Log("paused");
            }
            catch (Exception ex)
            {
                Log($"pause failed: {ex.Message}");
            }
        }

        private async void OnResumeClicked()
        {
            try
            {
                await _manager.Client.Ticks.ResumeAsync();
                Log("resumed");
            }
            catch (Exception ex)
            {
                Log($"resume failed: {ex.Message}");
            }
        }

        // ── State plumbing ────────────────────────────────────────────────────

        private void HandleConnectionStateChanged(ConnectionState state)
        {
            _statusLabel.text = $"State: {state}";
            RefreshButtonsForCurrentState();
        }

        private void RefreshButtonsForCurrentState()
        {
            var connected = _manager != null && _manager.IsConnected;
            SetButtonsInteractable(connect: !connected, health: connected, register: connected, tick: connected, pause: connected, resume: connected);
        }

        private void SetButtonsInteractable(bool connect, bool health, bool register, bool tick, bool pause, bool resume)
        {
            if (_connectButton  != null) _connectButton.interactable  = connect;
            if (_healthButton   != null) _healthButton.interactable   = health;
            if (_registerButton != null) _registerButton.interactable = register;
            if (_tickButton    != null) _tickButton.interactable    = tick;
            if (_pauseButton   != null) _pauseButton.interactable   = pause;
            if (_resumeButton  != null) _resumeButton.interactable  = resume;

        }

        // ── Logging ───────────────────────────────────────────────────────────

        private void Log(string line)
        {
            var stamp = DateTime.Now.ToString("HH:mm:ss");
            _log.Enqueue($"[{stamp}] {line}");
            while (_log.Count > maxLogLines) _log.Dequeue();
            _logLabel.text = string.Join("\n", _log);
            Debug.Log($"[SmokeTest] {line}");
        }

        // ── UI construction ───────────────────────────────────────────────────

        private void BuildUI()
        {
            // EventSystem
            if (FindFirstObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem");
                es.AddComponent<EventSystem>();
                es.AddComponent<StandaloneInputModule>();
            }

            // Canvas
            var canvasGo = new GameObject("SmokeTestCanvas");
            canvasGo.transform.SetParent(transform, worldPositionStays: false);

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            canvasGo.AddComponent<CanvasScaler>().uiScaleMode =
                CanvasScaler.ScaleMode.ScaleWithScreenSize;

            canvasGo.AddComponent<GraphicRaycaster>();

            // Status
            _statusLabel = MakeText(
                canvas.transform,
                "Status",
                new Vector2(0, 1),
                new Vector2(1, 1),
                new Vector2(10, -10),
                new Vector2(-10, -40),
                "State: Disconnected",
                18,
                TextAnchor.UpperLeft
            );

            // Buttons
            _connectButton =
                MakeButton(canvas.transform, "Connect", new Vector2(10, -50), OnConnectClicked);

            _healthButton =
                MakeButton(canvas.transform, "Health Check", new Vector2(140, -50), OnHealthCheckClicked);

            _registerButton =
                MakeButton(canvas.transform, "Register Agent", new Vector2(270, -50), OnRegisterAgentClicked);

            _tickButton =
                MakeButton(canvas.transform, "Force Tick", new Vector2(400, -50), OnTickClicked);

            _pauseButton =
                MakeButton(canvas.transform, "Pause", new Vector2(530, -50), OnPauseClicked);

            _resumeButton =
                MakeButton(canvas.transform, "Resume", new Vector2(660, -50), OnResumeClicked);

            // Event log
            _logLabel = MakeText(
                canvas.transform,
                "Log",
                new Vector2(0, 0),
                new Vector2(1, 1),
                new Vector2(10, 10),
                new Vector2(-10, -90),
                "(event log)",
                13,
                TextAnchor.UpperLeft
            );

            SetButtonsInteractable(
                connect: true,
                health: false,
                register: false,
                tick: false,
                pause: false,
                resume: false
            );
        }

        private static Button MakeButton(Transform parent, string label, Vector2 topLeftOffset, Action onClick)
        {
            var go = new GameObject($"Btn_{label}");
            go.transform.SetParent(parent, worldPositionStays: false);

            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(0, 1);
            rt.pivot     = new Vector2(0, 1);
            rt.sizeDelta = new Vector2(120, 30);
            rt.anchoredPosition = topLeftOffset;

            go.AddComponent<CanvasRenderer>();
            var image = go.AddComponent<Image>();
            image.color = new Color(0.2f, 0.2f, 0.2f, 0.85f);

            var button = go.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(() => onClick());

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, worldPositionStays: false);
            var textRt = textGo.AddComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = Vector2.zero;
            textRt.offsetMax = Vector2.zero;

            var text = textGo.AddComponent<Text>();
            text.text      = label;
            text.color     = Color.white;
            text.font      = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                          ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize  = 13;
            text.alignment = TextAnchor.MiddleCenter;

            return button;
        }

        private static Text MakeText(
            Transform   parent,
            string      name,
            Vector2     anchorMin,
            Vector2     anchorMax,
            Vector2     offsetMin,
            Vector2     offsetMax,
            string      content,
            int         fontSize,
            TextAnchor  alignment)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, worldPositionStays: false);

            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;

            var text = go.AddComponent<Text>();
            text.text      = content;
            text.color     = Color.white;
            text.font      = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                          ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize  = fontSize;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow   = VerticalWrapMode.Truncate;
            return text;
        }
    }
}
