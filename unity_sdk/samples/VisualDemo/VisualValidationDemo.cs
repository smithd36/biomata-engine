// Biomata SDK — Visual Validation Demo
//
// Proves the full LLM pipeline end-to-end:
//   OllamaLLMBrain (Python) → navigate intent → NavigateHandler → engine_command
//     → MoveActionHandler → visible cube movement in Unity
//
// Drop on an empty GameObject. Press Play.
// The scene (plane, cube NPC, lights, camera) is built entirely at runtime.
//
// Prerequisites
// ─────────────
//   1. Ollama running at http://localhost:11434 (or override in Inspector)
//      with the configured model loaded (default: qwen2.5:14b)
//   2. Python backend:
//        biomata-ws --config examples/visual_demo/sim.yaml --port 8765
//
// Interaction
// ───────────
//   Press Play → Click Connect → Click Register Agent → watch the cube move
//   "Force Tick" fires an extra tick immediately
//
// The OllamaLLMBrain personality and llm_config are sent from Unity via
// AgentRegistration.BrainConfig — no agent is pre-configured in sim.yaml.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Biomata.Integration;
using Biomata.Integration.Actions;
using Biomata.Integration.Observations;
using Biomata.SDK;
using Biomata.SDK.Models;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Biomata.Samples
{
    [AddComponentMenu("Biomata/Samples/Visual Validation Demo")]
    public class VisualValidationDemo : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("Backend")]
        [SerializeField] private string        host      = "localhost";
        [SerializeField] private int           port      = 8765;
        [SerializeField] private TransportKind transport = TransportKind.WebSocket;

        [Header("Tick Rate")]
        [Tooltip("Ticks/s sent to Python. Keep ≤ 0.5 — Ollama needs time between ticks.")]
        [SerializeField] private float tickRate = 0.3f;

        [Header("Agent")]
        [SerializeField] private string agentId   = "npc_001";
        [SerializeField] private string agentName = "Wanderer";

        [Header("Ollama LLM")]
        [SerializeField] private string ollamaModel   = "qwen2.5:14b";
        [SerializeField] private string ollamaBaseUrl = "http://localhost:11434";
        [SerializeField, Range(0f, 1f)] private float llmTemperature = 0.7f;

        [Header("NPC Movement")]
        [SerializeField] private float moveSpeed = 8f;

        // ── Private state ─────────────────────────────────────────────────────

        private UnitySimulationManager   _simMgr;
        private UnityAgentBridge         _bridge;
        private InterruptibleMoveHandler _mover;

        private GameObject _cube;
        private GameObject _targetMarker;
        private Material   _cubeMat;

        private Text   _statusLabel;
        private Text   _decisionLog;
        private Text   _eventLog;
        private Button _connectButton;
        private Button _registerButton;
        private Button _tickButton;

        private readonly Queue<string> _decisions = new Queue<string>();
        private readonly Queue<string> _events    = new Queue<string>();
        private const int LogLines = 10;

        private bool _agentRegistered;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void Awake()
        {
            BuildScene();
        }

        private void Start()
        {
            if (FindFirstObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem");
                es.AddComponent<EventSystem>();
                es.AddComponent<StandaloneInputModule>();
            }

            _simMgr = CreateSimManager();
            CreateNpc();
            BuildUI();
        }

        // ── Scene ─────────────────────────────────────────────────────────────

        private void BuildScene()
        {
            // Floor
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name             = "Floor";
            floor.transform.localScale = new Vector3(2f, 1f, 2f);
            ApplyColor(floor, new Color(0.22f, 0.24f, 0.26f));

            // Target marker — cyan sphere that tracks the active navigate target
            _targetMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _targetMarker.name             = "TargetMarker";
            _targetMarker.transform.localScale = Vector3.one * 0.4f;
            ApplyColor(_targetMarker, new Color(0f, 0.92f, 1f, 0.85f));
            Destroy(_targetMarker.GetComponent<Collider>());
            _targetMarker.SetActive(false);

            // Directional light
            var lightGO = new GameObject("Sun");
            var sun     = lightGO.AddComponent<Light>();
            sun.type                    = LightType.Directional;
            sun.intensity               = 1.25f;
            sun.color                   = new Color(1f, 0.97f, 0.92f);
            lightGO.transform.eulerAngles = new Vector3(50f, 38f, 0f);
            RenderSettings.ambientIntensity = 0.38f;

            // Camera
            var cam = Camera.main;
            if (cam != null)
            {
                cam.transform.position  = new Vector3(0f, 12f, -11f);
                cam.transform.LookAt(new Vector3(0f, 0f, 2f));
                cam.backgroundColor     = new Color(0.05f, 0.06f, 0.1f);
                cam.clearFlags          = CameraClearFlags.SolidColor;
            }
        }

        // ── Simulation manager ────────────────────────────────────────────────

        private UnitySimulationManager CreateSimManager()
        {
            var go = new GameObject("SimulationManager");
            go.SetActive(false);

            var mgr = go.AddComponent<UnitySimulationManager>();
            SetField(mgr, "transport",   transport);
            SetField(mgr, "host",        host);
            SetField(mgr, "port",        port);
            SetField(mgr, "tickRate",    tickRate);
            SetField(mgr, "autoConnect", false);

            go.SetActive(true);

            mgr.OnConnected       += HandleConnected;
            mgr.OnDisconnected    += HandleDisconnected;
            mgr.OnTickComplete    += HandleTickComplete;
            mgr.OnTickError       += ex  => AppendEvent($"tick error: {ex?.Message}");
            mgr.OnSimulationEvent += ev  => AppendEvent($"[{ev.EventType}] {ev.AgentId}");

            return mgr;
        }

        // ── NPC ───────────────────────────────────────────────────────────────

        private void CreateNpc()
        {
            _cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _cube.name             = agentName;
            _cube.transform.position   = new Vector3(0f, 0.75f, 0f);
            _cube.transform.localScale = new Vector3(1.5f, 1.5f, 1.5f);

            _cubeMat = new Material(Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit"));
            _cube.GetComponent<Renderer>().material = _cubeMat;
            SetCubeIdle();

            // Integration components — order matters: each component's Awake
            // calls GetComponents, so dependencies must be added first.
            _cube.AddComponent<TransformObservationProvider>();
            _cube.AddComponent<ObservationCollector>();   // finds TransformObservationProvider

            _mover           = _cube.AddComponent<InterruptibleMoveHandler>();
            _mover.Speed     = moveSpeed;
            _mover.Arrival   = 0.25f;

            _cube.AddComponent<ActionExecutor>();         // finds InterruptibleMoveHandler

            _bridge = _cube.AddComponent<UnityAgentBridge>();
            SetField(_bridge, "agentId",      agentId);
            SetField(_bridge, "agentName",    agentName);
            // Brain class is informational only here — we use BrainConfig at
            // registration time via OnRegisterClicked, so any valid string works.
            SetField(_bridge, "brainClass",   "src.plugins.builtin.idle_brain.brain.IdleBrain");
            SetField(_bridge, "autoRegister", false);     // Register button controls this

            _bridge.OnDecisionReceived += OnDecisionReceived;
            _bridge.OnActionStarted    += OnActionStarted;
            _bridge.OnActionCompleted  += OnActionCompleted;
        }

        // ── Button handlers ───────────────────────────────────────────────────

        private void OnConnectClicked()
        {
            SetStatus($"Connecting to {host}:{port}…");
            _connectButton.interactable = false;
            _simMgr.Connect();
        }

        private async void OnRegisterClicked()
        {
            if (_simMgr?.Client == null || !_simMgr.IsConnected)
            {
                AppendEvent("not connected");
                return;
            }
            if (_agentRegistered)
            {
                AppendEvent($"'{agentId}' already registered");
                return;
            }

            _registerButton.interactable = false;

            var reg = new AgentRegistration
            {
                AgentId    = agentId,
                AgentName  = agentName,
                BrainClass = "src.plugins.builtin.ollama.brain.OllamaLLMBrain",
                BrainConfig = new Dictionary<string, object>
                {
                    // OllamaLLMBrain.__init__(self, personality, llm_config, **kwargs)
                    ["llm_config"] = new Dictionary<string, object>
                    {
                        ["model"]       = ollamaModel,
                        ["base_url"]    = ollamaBaseUrl,
                        ["temperature"] = (double)llmTemperature,
                    },
                    ["personality"] = new Dictionary<string, object>
                    {
                        ["traits"] = new List<string> { "restless", "curious", "nomadic" },
                        ["goals"]  = new List<string>
                        {
                            "Always pick a navigate target at least 3 units from your current position",
                            "Explore varied areas — never return to where you just came from",
                            "Never idle; always move",
                        },
                        ["backstory"] =
                            "You are an autonomous entity on a flat 3D plane. " +
                            "Your position is reported as position_x and position_z. " +
                            "The world is bounded: both X and Z range from -9 to 9. " +
                            "Always choose navigate. Pick targets clearly within bounds. " +
                            "Make meaningful moves — at least 3 units from your current position.",
                    },
                },
            };

            try
            {
                await _simMgr.Client.Agents.RegisterAsync(reg, destroyCancellationToken);
                _agentRegistered = true;
                AppendEvent($"registered '{agentId}' with OllamaLLMBrain");
                SetStatus($"Connected  |  Agent registered  |  Ticking at {tickRate:F2}/s");
            }
            catch (Exception ex)
            {
                AppendEvent($"registration failed: {ex.Message}");
                _registerButton.interactable = true;
            }
        }

        private void OnForceTickClicked()
        {
            if (_simMgr?.IsConnected == true)
                _simMgr.ForceTick();
        }

        // ── Bridge event handlers ─────────────────────────────────────────────

        private void OnDecisionReceived(AgentDecisionResult decision)
        {
            // Interrupt any in-flight movement so new decisions take immediate effect.
            _mover?.Interrupt();

            if (decision.IsSuccess)
                ShowTargetMarker(decision);
        }

        private void OnActionStarted(string action)
        {
            var isMove = action == "navigate" || action == "move" ||
                         action == "walk"     || action == "travel";
            if (isMove) SetCubeMoving();
            else        SetCubeIdle();
        }

        private void OnActionCompleted(string action)
        {
            SetCubeIdle();
            _targetMarker.SetActive(false);
        }

        // ── Manager event handlers ────────────────────────────────────────────

        private void HandleConnected()
        {
            SetStatus($"Connected to {host}:{port}  |  Click Register Agent to start");
            _connectButton.interactable  = false;
            _registerButton.interactable = true;
            _tickButton.interactable     = true;
        }

        private void HandleDisconnected()
        {
            SetStatus("Disconnected");
            _connectButton.interactable  = true;
            _registerButton.interactable = false;
            _tickButton.interactable     = false;
            _agentRegistered             = false;
            SetCubeIdle();
            _targetMarker.SetActive(false);
        }

        private void HandleTickComplete(Biomata.SDK.Models.TickResult result)
        {
            foreach (var d in result.Decisions)
            {
                if (!d.IsSuccess)
                {
                    AppendDecision($"t{result.Tick}  {d.AgentId}: ERROR {d.Error}");
                    continue;
                }

                var target = ExtractTargetString(d);
                var reason = string.IsNullOrEmpty(d.OutcomeText)
                    ? string.Empty
                    : $"  \"{Truncate(d.OutcomeText, 38)}\"";
                AppendDecision($"t{result.Tick}  {d.AgentName}: {d.Action} {target}{reason}");
            }

            foreach (var (aid, msg) in result.Errors)
                AppendDecision($"t{result.Tick}  [{aid}] step error: {msg}");
        }

        // ── Target marker ─────────────────────────────────────────────────────

        private void ShowTargetMarker(AgentDecisionResult decision)
        {
            foreach (var cmd in decision.EngineCommands)
            {
                if (!TryStr(cmd, "type", out var type) || type != "navigate") continue;
                if (TryFloat(cmd, "x", out var cx) && TryFloat(cmd, "z", out var cz))
                {
                    TryFloat(cmd, "y", out var cy);
                    _targetMarker.transform.position = new Vector3(cx, cy + 0.2f, cz);
                    _targetMarker.SetActive(true);
                    return;
                }
            }

            if (TryFloat(decision.Parameters, "target_x", out var px) &&
                TryFloat(decision.Parameters, "target_z", out var pz))
            {
                _targetMarker.transform.position = new Vector3(px, 0.2f, pz);
                _targetMarker.SetActive(true);
            }
        }

        // ── NPC visual state ──────────────────────────────────────────────────

        private void SetCubeIdle()
        {
            if (_cubeMat != null) _cubeMat.color = new Color(1f, 0.86f, 0.08f);
        }

        private void SetCubeMoving()
        {
            if (_cubeMat != null) _cubeMat.color = new Color(0.18f, 0.88f, 0.32f);
        }

        // ── InterruptibleMoveHandler ──────────────────────────────────────────

        // Overrides MoveTowards with a token-based interruption mechanism.
        // When Interrupt() is called, the active MoveTowards coroutine exits on
        // the next frame, preventing overlapping movements when a new decision
        // arrives before the prior move completes.
        //
        // Parent's private fields (moveSpeed, arrivalThreshold, rotateSpeed) are
        // not accessible here, so this override uses its own public fields.
        // ActionExecutor finds this handler via GetComponents<ActionHandlerBase>
        // (InterruptibleMoveHandler → MoveActionHandler → ActionHandlerBase).

        private sealed class InterruptibleMoveHandler : MoveActionHandler
        {
            /// <summary>Move speed in units per second.</summary>
            public float Speed   = 8f;
            /// <summary>Stop when within this distance of the target.</summary>
            public float Arrival = 0.25f;

            private int _moveToken;

            /// <summary>
            /// Increment the token so any in-flight MoveTowards exits next frame.
            /// </summary>
            public void Interrupt() => _moveToken++;

            protected override IEnumerator MoveTowards(Transform t, Vector3 target)
            {
                var myToken = _moveToken;

                while (Vector3.Distance(t.position, target) > Arrival &&
                       _moveToken == myToken)
                {
                    var dir = target - t.position;
                    dir.y = 0f;

                    if (dir.sqrMagnitude > 0.0001f)
                    {
                        var targetRot = Quaternion.LookRotation(dir.normalized);
                        t.rotation    = Quaternion.RotateTowards(
                            t.rotation, targetRot, 360f * Time.deltaTime);
                    }

                    t.position = Vector3.MoveTowards(t.position, target, Speed * Time.deltaTime);
                    yield return null;
                }
            }
        }

        // ── UI construction ───────────────────────────────────────────────────

        private void BuildUI()
        {
            var canvasGO = new GameObject("DemoUI");
            canvasGO.transform.SetParent(transform, worldPositionStays: false);

            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            canvasGO.AddComponent<GraphicRaycaster>();

            var root = canvas.transform;

            // Status bar (top)
            _statusLabel = Label(root,
                new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(10, -8), new Vector2(-10, -32),
                $"Press Play → Connect → Register Agent  |  backend: biomata-ws --config examples/visual_demo/sim.yaml --port {port}",
                13, TextAnchor.UpperLeft);

            // Buttons
            _connectButton  = Btn(root, "Connect",        new Vector2(10,  -38), OnConnectClicked,  true);
            _registerButton = Btn(root, "Register Agent", new Vector2(145, -38), OnRegisterClicked, false);
            _tickButton     = Btn(root, "Force Tick",     new Vector2(295, -38), OnForceTickClicked, false);

            // Decision log (bottom left)
            Label(root,
                new Vector2(0, 0), new Vector2(0.5f, 0),
                new Vector2(10, 10), new Vector2(-5, 28),
                "DECISIONS (LLM output)", 10, TextAnchor.LowerLeft);

            _decisionLog = Label(root,
                new Vector2(0, 0), new Vector2(0.5f, 0),
                new Vector2(10, 30), new Vector2(-5, 230),
                "(no decisions yet — register agent and wait for ticks)", 10, TextAnchor.LowerLeft);

            // Event log (bottom right)
            Label(root,
                new Vector2(0.5f, 0), new Vector2(1, 0),
                new Vector2(5, 10), new Vector2(-10, 28),
                "EVENTS (stream)", 10, TextAnchor.LowerLeft);

            _eventLog = Label(root,
                new Vector2(0.5f, 0), new Vector2(1, 0),
                new Vector2(5, 30), new Vector2(-10, 230),
                "(no events yet)", 10, TextAnchor.LowerLeft);
        }

        private void SetStatus(string msg)
        {
            if (_statusLabel != null) _statusLabel.text = msg;
        }

        private void AppendDecision(string line)
        {
            _decisions.Enqueue($"[{DateTime.Now:HH:mm:ss}] {line}");
            while (_decisions.Count > LogLines) _decisions.Dequeue();
            if (_decisionLog != null) _decisionLog.text = string.Join("\n", _decisions);
        }

        private void AppendEvent(string line)
        {
            _events.Enqueue($"[{DateTime.Now:HH:mm:ss}] {line}");
            while (_events.Count > LogLines) _events.Dequeue();
            if (_eventLog != null) _eventLog.text = string.Join("\n", _events);
        }

        // ── UI factory helpers ────────────────────────────────────────────────

        private static Button Btn(Transform parent, string label, Vector2 pos, Action onClick, bool enabled)
        {
            var go = new GameObject($"Btn_{label}");
            go.transform.SetParent(parent, false);

            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin        = new Vector2(0, 1);
            rt.anchorMax        = new Vector2(0, 1);
            rt.pivot            = new Vector2(0, 1);
            rt.sizeDelta        = new Vector2(125, 28);
            rt.anchoredPosition = pos;

            go.AddComponent<CanvasRenderer>();
            var img   = go.AddComponent<Image>();
            img.color = new Color(0.16f, 0.18f, 0.22f, 0.92f);

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.interactable  = enabled;
            btn.onClick.AddListener(() => onClick());

            var tGO = new GameObject("T");
            tGO.transform.SetParent(go.transform, false);
            var tRt = tGO.AddComponent<RectTransform>();
            tRt.anchorMin = Vector2.zero;
            tRt.anchorMax = Vector2.one;
            tRt.offsetMin = tRt.offsetMax = Vector2.zero;

            var t = tGO.AddComponent<Text>();
            t.text      = label;
            t.color     = Color.white;
            t.font      = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                       ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            t.fontSize  = 12;
            t.alignment = TextAnchor.MiddleCenter;
            return btn;
        }

        private static Text Label(
            Transform parent,
            Vector2 anchorMin, Vector2 anchorMax,
            Vector2 offMin,    Vector2 offMax,
            string content, int size, TextAnchor align)
        {
            var go = new GameObject("Lbl");
            go.transform.SetParent(parent, false);

            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = offMin;
            rt.offsetMax = offMax;

            var t = go.AddComponent<Text>();
            t.text               = content;
            t.color              = Color.white;
            t.font               = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                                ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            t.fontSize           = size;
            t.alignment          = align;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow   = VerticalWrapMode.Truncate;
            return t;
        }

        // ── Utility ───────────────────────────────────────────────────────────

        private static void ApplyColor(GameObject go, Color color)
        {
            var r = go.GetComponent<Renderer>();
            if (r == null) return;
            r.material = new Material(
                Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit"))
            { color = color };
        }

        private static void SetField(object target, string name, object value)
        {
            const BindingFlags f = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;
            target.GetType().GetField(name, f)?.SetValue(target, value);
        }

        private static string ExtractTargetString(AgentDecisionResult d)
        {
            foreach (var cmd in d.EngineCommands)
                if (TryStr(cmd, "type", out var tp) && tp == "navigate" &&
                    TryFloat(cmd, "x", out var cx) && TryFloat(cmd, "z", out var cz))
                    return $"({cx:F1}, {cz:F1})";
            if (TryFloat(d.Parameters, "target_x", out var px) &&
                TryFloat(d.Parameters, "target_z", out var pz))
                return $"({px:F1}, {pz:F1})";
            return string.Empty;
        }

        private static bool TryStr(Dictionary<string, object> d, string k, out string v)
        {
            v = null;
            return d.TryGetValue(k, out var o) && (v = o?.ToString()) != null;
        }

        private static bool TryFloat(Dictionary<string, object> d, string k, out float v)
        {
            v = 0f;
            return d.TryGetValue(k, out var o) &&
                   float.TryParse(o?.ToString(),
                       System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out v);
        }

        private static string Truncate(string s, int max) =>
            s == null ? string.Empty : s.Length <= max ? s : s.Substring(0, max) + "...";
    }
}
