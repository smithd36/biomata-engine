// Biomata SDK — Multi-Agent Orchestration Demo
//
// 20 white cubes navigating simultaneously, each driven by OllamaLLMBrain over WebSocket.
// Proves that Biomata orchestrates many LLM-backed agents concurrently through a single session.
//
// All 20 registrations are sent in parallel on connect. Python processes agents through
// the SimultaneousScheduler — all 20 LLM calls fire concurrently each tick.
//
// Tick rate defaults to 0.2 Hz (one tick per 5 s). Each tick issues 20 concurrent Ollama
// calls; raise the rate only if your GPU can sustain the throughput.
//
// Backend: biomata-ws --config examples/visual_demo/sim.yaml --port 8765

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
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
    [AddComponentMenu("Biomata/Samples/Multi-Agent Orchestration Demo")]
    public class MultiAgentOrchestrationDemo : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("Backend")]
        [SerializeField] private string host = "localhost";
        [SerializeField] private int    port = 8765;

        [Header("Simulation")]
        [Tooltip("Ticks per second. 20 concurrent Ollama calls per tick — keep ≤ 0.3 unless your GPU is fast.")]
        [SerializeField] private float tickRate  = 0.2f;
        [SerializeField] private float moveSpeed = 5f;
        [Tooltip("World-space distance between agent grid centres.")]
        [SerializeField] private float gridSpacing = 9f;

        [Header("Ollama LLM")]
        [SerializeField] private string ollamaModel   = "qwen2.5:14b";
        [SerializeField] private string ollamaBaseUrl = "http://localhost:11434";
        [SerializeField, Range(0f, 1f)] private float llmTemperature = 0.7f;

        // ── Grid layout ───────────────────────────────────────────────────────

        private const int GridCols   = 4;
        private const int GridRows   = 5;
        private const int AgentCount = GridCols * GridRows; // 20

        // 20 distinct names — one per agent, used in LLM personality backstory.
        private static readonly string[] AgentNames =
        {
            "Wanderer", "Rover",      "Scout",    "Drifter",
            "Nomad",    "Pilgrim",    "Ranger",   "Pathfinder",
            "Strider",  "Rambler",    "Explorer", "Seeker",
            "Tracer",   "Roamer",     "Pioneer",  "Wayfarer",
            "Voyager",  "Trekker",    "Cruiser",  "Prowler",
        };

        // ── Cube colours ──────────────────────────────────────────────────────

        private static readonly Color CubeIdle   = Color.white;
        private static readonly Color CubeMoving = new Color(0.45f, 0.95f, 0.45f); // green tint

        // ── Per-agent record ──────────────────────────────────────────────────

        private class AgentRecord
        {
            public int              Index;
            public string           Id;
            public string           Name;
            public Vector3          GridPos;
            public Material         Mat;
            public UnityAgentBridge Bridge;
            public bool             IsMoving;
        }

        // ── Private state ─────────────────────────────────────────────────────

        private UnitySimulationManager _simMgr;
        private AgentRecord[]          _agents;
        private int                    _registeredCount;
        private int                    _totalDecisions;
        private int                    _totalTicks;

        // ── UI references ─────────────────────────────────────────────────────

        private Text    _statusLabel;
        private Text    _statsLabel;
        private Text    _logText;
        private Image[] _indicators;
        private Button  _connectButton;

        private readonly Queue<string> _log     = new Queue<string>();
        private const int              LogLines = 14;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void Awake()
        {
            BuildScene();
        }

        private void Start()
        {
            EnsureEventSystem();
            InitAgentRecords();
            CreateSimManager();      // before agents so UnityAgentBridge.Start finds it
            CreateAgentObjects();
            BuildUI();
        }

        // ── Scene ─────────────────────────────────────────────────────────────

        private void BuildScene()
        {
            float halfW = (GridCols - 1) * gridSpacing * 0.5f;
            float halfH = (GridRows - 1) * gridSpacing * 0.5f;
            float side  = Mathf.Max(halfW, halfH) * 2f + 14f;

            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name             = "Floor";
            floor.transform.localScale = Vector3.one * (side / 10f);
            floor.transform.position   = Vector3.down * 0.01f;
            ApplyMat(floor, new Color(0.17f, 0.18f, 0.20f));

            var lightGO = new GameObject("Sun");
            var sun     = lightGO.AddComponent<Light>();
            sun.type                      = LightType.Directional;
            sun.intensity                 = 1.15f;
            sun.color                     = new Color(1f, 0.96f, 0.90f);
            lightGO.transform.eulerAngles = new Vector3(52f, 30f, 0f);
            RenderSettings.ambientIntensity = 0.40f;

            var cam = Camera.main;
            if (cam != null)
            {
                cam.transform.position  = new Vector3(0f, 46f, -28f);
                cam.transform.LookAt(new Vector3(0f, 0f, halfH * 0.5f));
                cam.fieldOfView         = 58f;
                cam.backgroundColor     = new Color(0.04f, 0.05f, 0.08f);
                cam.clearFlags          = CameraClearFlags.SolidColor;
            }
        }

        // ── Agent records ─────────────────────────────────────────────────────

        private void InitAgentRecords()
        {
            _agents = new AgentRecord[AgentCount];
            float halfW = (GridCols - 1) * gridSpacing * 0.5f;
            float halfH = (GridRows - 1) * gridSpacing * 0.5f;

            for (int i = 0; i < AgentCount; i++)
            {
                int   col = i % GridCols;
                int   row = i / GridCols;
                float cx  = col * gridSpacing - halfW;
                float cz  = row * gridSpacing - halfH;

                _agents[i] = new AgentRecord
                {
                    Index   = i,
                    Id      = $"agent_{i + 1:D3}",
                    Name    = AgentNames[i],
                    GridPos = new Vector3(cx, 0f, cz),
                };
            }
        }

        // ── Agent GameObjects ─────────────────────────────────────────────────

        private void CreateAgentObjects()
        {
            foreach (var rec in _agents)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name             = rec.Name;
                go.transform.position   = rec.GridPos + Vector3.up * 0.75f;
                go.transform.localScale = Vector3.one * 1.3f;

                var mat = new Material(
                    Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit"))
                    { color = CubeIdle };
                go.GetComponent<Renderer>().material = mat;
                rec.Mat = mat;

                // Integration stack — order matters for component Awake dependencies.
                go.AddComponent<TransformObservationProvider>();
                go.AddComponent<ObservationCollector>();

                var mover = go.AddComponent<MoveActionHandler>();
                SetField(mover, "moveSpeed",        moveSpeed);
                SetField(mover, "arrivalThreshold", 0.4f);

                go.AddComponent<ActionExecutor>();

                var bridge = go.AddComponent<UnityAgentBridge>();
                SetField(bridge, "agentId",      rec.Id);
                SetField(bridge, "agentName",    rec.Name);
                SetField(bridge, "brainClass",   "src.plugins.builtin.ollama.brain.OllamaLLMBrain");
                SetField(bridge, "autoRegister", false);

                rec.Bridge = bridge;

                var captured = rec;
                bridge.OnActionStarted   += _ => SetMoving(captured, true);
                bridge.OnActionCompleted += _ => SetMoving(captured, false);
            }
        }

        // ── Simulation manager ────────────────────────────────────────────────

        private void CreateSimManager()
        {
            var go = new GameObject("SimulationManager");
            go.SetActive(false);

            _simMgr = go.AddComponent<UnitySimulationManager>();
            SetField(_simMgr, "host",        host);
            SetField(_simMgr, "port",        port);
            SetField(_simMgr, "tickRate",    tickRate);
            SetField(_simMgr, "autoConnect", false);

            go.SetActive(true);

            _simMgr.OnConnected       += HandleConnected;
            _simMgr.OnDisconnected    += HandleDisconnected;
            _simMgr.OnTickComplete    += HandleTickComplete;
            _simMgr.OnTickError       += ex => Log($"tick error: {ex?.Message}");
        }

        // ── Connection ────────────────────────────────────────────────────────

        private void HandleConnected()
        {
            SetStatus($"Connected — registering {AgentCount} agents in parallel…");
            _connectButton.interactable = false;
            RegisterAllAgents();
        }

        private void HandleDisconnected()
        {
            SetStatus("Disconnected");
            _connectButton.interactable = true;
            _registeredCount            = 0;
            foreach (var rec in _agents) SetMoving(rec, false);
            UpdateStats();
        }

        private void HandleTickComplete(TickResult result)
        {
            _totalTicks++;
            int n = result.Decisions?.Count ?? 0;
            _totalDecisions += n;
            Log($"t{result.Tick}: {n} LLM decisions");
            UpdateStats();
        }

        // ── Parallel registration ─────────────────────────────────────────────

        private async void RegisterAllAgents()
        {
            var tasks = new List<Task>(AgentCount);
            foreach (var rec in _agents)
                tasks.Add(RegisterOne(rec));

            try
            {
                await Task.WhenAll(tasks);
                SetStatus(
                    $"All {AgentCount} agents active — {tickRate:F2} ticks/s  " +
                    $"|  model: {ollamaModel}  |  backend port {port}");
            }
            catch (Exception ex)
            {
                SetStatus($"Registration error: {ex.Message}");
                Log($"ERROR: {ex.Message}");
                _connectButton.interactable = true;
            }
        }

        private async Task RegisterOne(AgentRecord rec)
        {
            var reg = new AgentRegistration
            {
                AgentId    = rec.Id,
                AgentName  = rec.Name,
                BrainClass = "src.plugins.builtin.ollama.brain.OllamaLLMBrain",
                BrainConfig = new Dictionary<string, object>
                {
                    ["llm_config"] = new Dictionary<string, object>
                    {
                        ["model"]       = ollamaModel,
                        ["base_url"]    = ollamaBaseUrl,
                        ["temperature"] = (double)llmTemperature,
                    },
                    ["personality"] = new Dictionary<string, object>
                    {
                        ["traits"] = new List<string> { "autonomous", "restless", "wandering" },
                        ["goals"]  = new List<string>
                        {
                            "Always choose navigate — never idle",
                            "Pick a target at least 4 units away from your current position",
                            "Explore varied areas of the plane — do not repeat the same spot twice in a row",
                        },
                        ["backstory"] =
                            $"You are {rec.Name}, one of 20 autonomous agents on a flat plane. " +
                            "Your position is given as position_x and position_z. " +
                            "The world spans -20 to 20 in both X and Z — stay within those bounds. " +
                            "Your only action is navigate. Each decision, choose a new target " +
                            "at least 4 units from where you currently are.",
                    },
                },
            };

            await _simMgr.Client.Agents.RegisterAsync(reg, destroyCancellationToken);
            _registeredCount++;
            Log($"registered {rec.Name} ({rec.Id})");
            UpdateStats();
        }

        // ── Per-agent state ───────────────────────────────────────────────────

        private void SetMoving(AgentRecord rec, bool moving)
        {
            rec.IsMoving = moving;

            if (rec.Mat != null)
                rec.Mat.color = moving ? CubeMoving : CubeIdle;

            if (_indicators != null && rec.Index < _indicators.Length)
                _indicators[rec.Index].color = moving
                    ? new Color(0.40f, 0.90f, 0.40f)
                    : new Color(0.30f, 0.30f, 0.30f);
        }

        // ── UI ────────────────────────────────────────────────────────────────

        private void BuildUI()
        {
            var canvasGO = new GameObject("DemoUI");
            canvasGO.transform.SetParent(transform, false);

            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            canvasGO.AddComponent<GraphicRaycaster>();

            var root = canvas.transform;

            // Status bar (top)
            _statusLabel = MakeLbl(root,
                new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(10, -8), new Vector2(-10, -32),
                $"Click Connect — backend: biomata-ws --config examples/visual_demo/sim.yaml --port {port}",
                13, TextAnchor.UpperLeft);

            // Connect button
            _connectButton = MakeBtn(root, "Connect",
                new Vector2(10, -38), new Vector2(120, 28),
                () =>
                {
                    SetStatus($"Connecting to {host}:{port}…");
                    _simMgr.Connect();
                });

            // Stats (top right)
            _statsLabel = MakeLbl(root,
                new Vector2(0.6f, 1), new Vector2(1, 1),
                new Vector2(0, -8), new Vector2(-10, -80),
                BuildStatsText(), 12, TextAnchor.UpperRight);

            // Agent indicator grid — grey=idle, green=moving
            BuildIndicatorGrid(root);

            // Event log (bottom)
            MakeLbl(root,
                new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(10, 10), new Vector2(-10, 28),
                "EVENT LOG", 10, TextAnchor.LowerLeft);

            _logText = MakeLbl(root,
                new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(10, 30), new Vector2(-10, 230),
                "(waiting for connection…)", 10, TextAnchor.LowerLeft);
        }

        private void BuildIndicatorGrid(Transform root)
        {
            _indicators = new Image[AgentCount];

            const float cell   = 18f;
            const float pad    = 3f;
            const float startX = 10f;
            const float startY = -72f;

            for (int i = 0; i < AgentCount; i++)
            {
                int col = i % GridCols;
                int row = i / GridCols;

                var go = new GameObject($"Ind_{i}");
                go.transform.SetParent(root, false);

                var rt = go.AddComponent<RectTransform>();
                rt.anchorMin        = new Vector2(0, 1);
                rt.anchorMax        = new Vector2(0, 1);
                rt.pivot            = new Vector2(0, 1);
                rt.sizeDelta        = Vector2.one * cell;
                rt.anchoredPosition = new Vector2(
                    startX + col * (cell + pad),
                    startY - row * (cell + pad));

                go.AddComponent<CanvasRenderer>();
                var img = go.AddComponent<Image>();
                img.color      = new Color(0.30f, 0.30f, 0.30f); // dark grey until moving
                _indicators[i] = img;
            }
        }

        // ── UI helpers ────────────────────────────────────────────────────────

        private void SetStatus(string msg)
        {
            if (_statusLabel != null) _statusLabel.text = msg;
        }

        private void UpdateStats()
        {
            if (_statsLabel == null) return;
            _statsLabel.text = BuildStatsText();
        }

        private string BuildStatsText()
        {
            int moving = 0;
            if (_agents != null)
                foreach (var a in _agents)
                    if (a.IsMoving) moving++;

            return
                $"Registered: {_registeredCount} / {AgentCount}\n" +
                $"Ticks:  {_totalTicks}     Decisions:  {_totalDecisions}\n" +
                $"Moving: {moving} / {AgentCount}";
        }

        private void Log(string line)
        {
            _log.Enqueue($"[{DateTime.Now:HH:mm:ss}] {line}");
            while (_log.Count > LogLines) _log.Dequeue();
            if (_logText != null) _logText.text = string.Join("\n", _log);
        }

        // ── Factory helpers ───────────────────────────────────────────────────

        private static Button MakeBtn(Transform parent, string label, Vector2 pos, Vector2 size, Action onClick)
        {
            var go = new GameObject($"Btn_{label}");
            go.transform.SetParent(parent, false);

            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin        = new Vector2(0, 1);
            rt.anchorMax        = new Vector2(0, 1);
            rt.pivot            = new Vector2(0, 1);
            rt.sizeDelta        = size;
            rt.anchoredPosition = pos;

            go.AddComponent<CanvasRenderer>();
            var img   = go.AddComponent<Image>();
            img.color = new Color(0.15f, 0.18f, 0.22f, 0.92f);

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
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

        private static Text MakeLbl(
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

        // ── Utilities ─────────────────────────────────────────────────────────

        private static void ApplyMat(GameObject go, Color color)
        {
            var r = go.GetComponent<Renderer>();
            if (r == null) return;
            r.material = new Material(
                Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit"))
                { color = color };
        }

        private static void SetField(object target, string name, object value)
        {
            const BindingFlags f =
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;
            target.GetType().GetField(name, f)?.SetValue(target, value);
        }

        private static void EnsureEventSystem()
        {
            if (FindFirstObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem");
                es.AddComponent<EventSystem>();
                es.AddComponent<StandaloneInputModule>();
            }
        }
    }
}
