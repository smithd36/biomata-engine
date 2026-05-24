// Biomata SDK — Village Life Demo
//
// Flagship showcase: 10 autonomous NPCs living in a primitive village,
// driven by a hybrid cognition backend (deterministic + Ollama LLM).
//
// Scene is built entirely at runtime — no prefabs, no assets required.
// Drop this component on an empty GameObject and press Play.
//
// Agent IDs defined here MUST match examples/village/sim.yaml exactly.
// Agents are pre-declared in the YAML; autoRegister=false on all bridges.
//
// Backend:
//   biomata-ws --config examples/village/sim.yaml --port 8765
//
// Ollama (for 3 LLM agents):
//   ollama serve
//   ollama pull qwen2.5:14b

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Biomata.Integration;
using Biomata.Integration.Actions;
using Biomata.Integration.Observations;
using Biomata.SDK.Models;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Biomata.Samples
{
    // ── Agent data types ──────────────────────────────────────────────────────

    internal readonly struct VillageAgentSpec
    {
        public readonly string  AgentId;
        public readonly string  DisplayName;
        public readonly string  Role;
        public readonly string  CognitionType;  // "Deterministic" | "LLM (Ollama)"
        public readonly Color   BaseColor;
        public readonly Vector3 StartPosition;

        public VillageAgentSpec(
            string agentId, string displayName, string role, string cognitionType,
            Color baseColor, Vector3 startPosition)
        {
            AgentId       = agentId;
            DisplayName   = displayName;
            Role          = role;
            CognitionType = cognitionType;
            BaseColor     = baseColor;
            StartPosition = startPosition;
        }
    }

    internal class VillageAgentRecord
    {
        public VillageAgentSpec     Spec;
        public Material             Mat;
        public UnityAgentBridge     Bridge;
        public AgentDecisionResult  LastDecision;
        public bool                 IsMoving;

        public string State
        {
            get
            {
                if (LastDecision == null) return "Waiting";
                return LastDecision.Action switch
                {
                    "navigate" => "Moving",
                    "idle"     => "Idle",
                    "interact" => "Interacting",
                    "speak"    => "Speaking",
                    _          => "Active",
                };
            }
        }
    }

    // Helper: fires OnClicked from a collider's OnMouseDown.
    internal class AgentClickReceiver : MonoBehaviour
    {
        public Action OnClicked;
        private void OnMouseDown() => OnClicked?.Invoke();
    }

    // ── Main demo ─────────────────────────────────────────────────────────────

    [AddComponentMenu("Biomata/Samples/Village Life Demo")]
    public class VillageLifeDemo : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("Backend")]
        [SerializeField] private string host = "localhost";
        [SerializeField] private int    port = 8765;

        [Header("Simulation")]
        [Tooltip("Ticks per second. Keep ≤ 0.5 when Ollama agents are active.")]
        [SerializeField] private float  tickRate  = 0.2f;
        [SerializeField] private float  moveSpeed = 6f;

        // ── 10 village agents (IDs must match sim.yaml) ───────────────────────

        private static readonly VillageAgentSpec[] AgentSpecs =
        {
            new VillageAgentSpec("guard_001",     "Aldric",  "Guard",      "Deterministic",
                new Color(1f, 0.22f, 0.10f),  new Vector3(  0f, 1f,  14f)),
            new VillageAgentSpec("guard_002",     "Berna",   "Guard",      "Deterministic",
                new Color(1f, 0.50f, 0.05f),  new Vector3(  0f, 1f, -14f)),
            new VillageAgentSpec("villager_001",  "Mira",    "Villager",   "Deterministic",
                new Color(1f, 0.90f, 0.20f),  new Vector3(  0f, 1f,   0f)),
            new VillageAgentSpec("villager_002",  "Tomas",   "Villager",   "Deterministic",
                new Color(0.20f, 0.80f, 0.90f), new Vector3(6f, 1f,   4f)),
            new VillageAgentSpec("merchant_001",  "Silas",   "Merchant",   "LLM (Ollama)",
                new Color(1f, 0.75f, 0.00f),  new Vector3( 14f, 1f,   0f)),
            new VillageAgentSpec("farmer_001",    "Edith",   "Farmer",     "Deterministic",
                new Color(0.30f, 0.80f, 0.20f), new Vector3(2f, 1f, -14f)),
            new VillageAgentSpec("innkeeper_001", "Rogan",   "Innkeeper",  "LLM (Ollama)",
                new Color(0.60f, 0.20f, 0.85f), new Vector3(-10f, 1f, 5f)),
            new VillageAgentSpec("traveler_001",  "Lyra",    "Traveler",   "LLM (Ollama)",
                new Color(0.90f, 0.90f, 0.90f), new Vector3(-2f, 1f,  2f)),
            new VillageAgentSpec("townsfolk_001", "Bram",    "Townsfolk",  "Deterministic",
                new Color(0.20f, 0.40f, 0.90f), new Vector3( 2f, 1f,  2f)),
            new VillageAgentSpec("townsfolk_002", "Nessa",   "Townsfolk",  "Deterministic",
                new Color(0.90f, 0.40f, 0.65f), new Vector3(-10f, 1f, 7f)),
        };

        // ── Runtime state ─────────────────────────────────────────────────────

        private UnitySimulationManager  _simMgr;
        private VillageAgentRecord[]    _agents;
        private int                     _selectedIdx;

        private bool  _autoTicking;
        private bool  _paused;
        private float _tickAccum;
        private float _tickStartTime;
        private float _lastTickMs;
        private int   _totalTicks;
        private int   _totalDecisions;
        private int   _totalEvents;

        // ── UI references ─────────────────────────────────────────────────────

        private Text   _statusText;
        private Text   _metricsText;
        private Text   _inspectorText;
        private Text   _eventLogText;
        private Text   _decisionLogText;
        private Button _connectBtn;
        private Button _disconnectBtn;
        private Button _startTickBtn;
        private Button _stopTickBtn;

        private readonly Queue<string> _eventLog    = new Queue<string>();
        private readonly Queue<string> _decisionLog = new Queue<string>();
        private const int MaxLogLines = 14;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void Awake()
        {
            BuildScene();
            SetupCamera();
        }

        private void Start()
        {
            EnsureEventSystem();
            CreateSimManager();
            CreateAgentObjects();
            BuildHUD();
        }

        private void Update()
        {
            UpdateMetricsText();

            if (_paused || !_autoTicking || !(_simMgr?.IsConnected == true)) return;

            _tickAccum += Time.deltaTime;
            float interval = tickRate > 0f ? 1f / tickRate : 9999f;
            if (_tickAccum < interval) return;

            _tickAccum = 0f;
            DispatchTick();
        }

        // ── Scene building ────────────────────────────────────────────────────

        private void BuildScene()
        {
            // Ground
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.position   = new Vector3(0f, -0.01f, 0f);
            ground.transform.localScale = Vector3.one * 4.8f;
            ApplyMat(ground, new Color(0.28f, 0.34f, 0.22f));

            // Roads (flat planes, slightly raised)
            MakeRoad(new Vector3(0f, 0.005f,  0f), new Vector3(0.35f, 1f, 4.8f)); // N-S
            MakeRoad(new Vector3(0f, 0.005f,  0f), new Vector3(4.8f,  1f, 0.35f)); // E-W

            // Buildings
            MakeBuilding("TownHall",   new Vector3( 0f, 1.5f,  0f), new Vector3(8f, 3f, 6f), new Color(0.30f, 0.20f, 0.12f));
            MakeBuilding("Market",     new Vector3(14f, 1.0f,  0f), new Vector3(5f, 2f, 5f), new Color(0.70f, 0.60f, 0.38f));
            MakeBuilding("Tavern",     new Vector3(-10f, 2.0f, 5f), new Vector3(7f, 4f, 6f), new Color(0.38f, 0.18f, 0.10f));
            MakeBuilding("Barn",       new Vector3( 2f, 1.5f,-14f), new Vector3(8f, 3f, 6f), new Color(0.52f, 0.32f, 0.14f));
            MakeBuilding("HouseA",     new Vector3(-6f, 1.0f, -6f), new Vector3(4f, 2f, 4f), new Color(0.48f, 0.48f, 0.52f));
            MakeBuilding("HouseB",     new Vector3( 8f, 1.0f, -8f), new Vector3(4f, 2f, 4f), new Color(0.58f, 0.58f, 0.62f));
            MakeBuilding("Blacksmith", new Vector3(-14f, 1.5f,-4f), new Vector3(4f, 3f, 4f), new Color(0.24f, 0.22f, 0.20f));
            MakeBuilding("PostNorth",  new Vector3( 0f, 0.75f,15f), new Vector3(2f, 1.5f, 2f), new Color(0.44f, 0.40f, 0.36f));
            MakeBuilding("PostSouth",  new Vector3( 0f, 0.75f,-15f), new Vector3(2f, 1.5f, 2f), new Color(0.44f, 0.40f, 0.36f));

            // POI markers (flat cylinders)
            MakePoi("TownSquare", new Vector3(  0f, 0.05f,  0f), 3.0f, new Color(0.70f, 0.68f, 0.60f, 0.25f));
            MakePoi("Well",       new Vector3(  6f, 0.05f,  4f), 1.0f, new Color(0.30f, 0.55f, 0.95f, 0.30f));
            MakePoi("Market",     new Vector3( 14f, 0.05f,  0f), 2.0f, new Color(0.90f, 0.75f, 0.20f, 0.28f));
            MakePoi("Tavern",     new Vector3(-10f, 0.05f,  5f), 2.0f, new Color(0.75f, 0.20f, 0.10f, 0.28f));
            MakePoi("Farm",       new Vector3(  2f, 0.05f,-14f), 2.0f, new Color(0.30f, 0.70f, 0.20f, 0.28f));

            // Well cylinder visual
            var well = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            well.name = "WellVisual";
            well.transform.position   = new Vector3(6f, 0.6f, 4f);
            well.transform.localScale = new Vector3(0.9f, 0.6f, 0.9f);
            ApplyMat(well, new Color(0.35f, 0.30f, 0.25f));

            // Lighting
            var sunGO = new GameObject("Sun");
            var sun   = sunGO.AddComponent<Light>();
            sun.type                    = LightType.Directional;
            sun.intensity               = 1.1f;
            sun.color                   = new Color(1f, 0.94f, 0.85f);
            sunGO.transform.eulerAngles = new Vector3(48f, 34f, 0f);
            RenderSettings.ambientIntensity = 1.0f;
        }

        private void MakeRoad(Vector3 pos, Vector3 scale)
        {
            var r = GameObject.CreatePrimitive(PrimitiveType.Plane);
            r.name = "Road";
            r.transform.position   = pos;
            r.transform.localScale = scale;
            ApplyMat(r, new Color(0.36f, 0.35f, 0.32f));
            Destroy(r.GetComponent<Collider>());
        }

        private void MakeBuilding(string label, Vector3 pos, Vector3 scale, Color color)
        {
            var b = GameObject.CreatePrimitive(PrimitiveType.Cube);
            b.name = label;
            b.transform.position   = pos;
            b.transform.localScale = scale;
            ApplyMat(b, color);
        }

        private static void BuildWorldLabel(GameObject parent, string label, Color color)
        {
            var go = new GameObject("Label");
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = new Vector3(0f, 1.2f, 0f);
            go.transform.localScale = Vector3.one * 0.01f;

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = Camera.main;

            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(220f, 60f);

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 1;

            go.AddComponent<BillboardLabel>();

            var textGO = new GameObject("T");
            textGO.transform.SetParent(go.transform, false);

            var textRt = textGO.AddComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = Vector2.zero;
            textRt.offsetMax = Vector2.zero;

            var t = textGO.AddComponent<Text>();
            t.text = label;
            t.color = color;
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            t.fontSize = 28;
            t.alignment = TextAnchor.MiddleCenter;
        }

        private void MakePoi(string label, Vector3 pos, float radius, Color color)
        {
            var p = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            p.name = $"POI_{label}";
            p.transform.position   = pos;
            p.transform.localScale = new Vector3(radius, 0.01f, radius);
            ApplyMat(p, color);
            BuildWorldLabel(p, label, Color.white);
            Destroy(p.GetComponent<Collider>());
        }

        // ── Camera ────────────────────────────────────────────────────────────

        private void SetupCamera()
        {
            var cam = Camera.main;
            if (cam == null) return;
            cam.transform.position = new Vector3(-18f, 18f, -18f);
            cam.transform.LookAt(new Vector3(0f, 2f, 0f));
            cam.fieldOfView = 55f;
            cam.backgroundColor     = new Color(0.55f, 0.72f, 0.92f);
            cam.clearFlags          = CameraClearFlags.SolidColor;
        }

        // ── Agent GameObjects ─────────────────────────────────────────────────

        private void CreateAgentObjects()
        {
            _agents = new VillageAgentRecord[AgentSpecs.Length];

            for (int i = 0; i < AgentSpecs.Length; i++)
            {
                var spec = AgentSpecs[i];
                var rec  = new VillageAgentRecord { Spec = spec };
                _agents[i] = rec;

                // NPC capsule
                var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                go.name             = spec.DisplayName;
                go.transform.position   = spec.StartPosition;
                go.transform.localScale = new Vector3(0.85f, 0.85f, 0.85f);
                var mat = new Material(
                    Shader.Find("Universal Render Pipeline/Lit")
                    ?? Shader.Find("Standard"))
                {
                    color = spec.BaseColor
                };
                go.GetComponent<Renderer>().material = mat;
                rec.Mat = mat;

                // Integration components
                go.AddComponent<TransformObservationProvider>();
                go.AddComponent<ObservationCollector>();

                var mover = go.AddComponent<MoveActionHandler>();
                SetField(mover, "moveSpeed",        moveSpeed);
                SetField(mover, "arrivalThreshold", 0.8f);

                go.AddComponent<InteractActionHandler>();
                var speaker = go.AddComponent<SpeakActionHandler>();
                SetField(speaker, "logToConsole", true);

                go.AddComponent<ActionExecutor>();

                var bridge = go.AddComponent<UnityAgentBridge>();
                SetField(bridge, "agentId",      spec.AgentId);
                SetField(bridge, "agentName",    spec.DisplayName);
                SetField(bridge, "autoRegister", false);
                rec.Bridge = bridge;

                // Click-to-select
                var idx     = i;
                var clicker = go.AddComponent<AgentClickReceiver>();
                clicker.OnClicked = () => SelectAgent(idx);

                // Track decisions for inspector
                bridge.OnDecisionReceived += d =>
                {
                    rec.LastDecision = d;
                    LogDecision($"[{spec.DisplayName}] {d.Action}: {d.OutcomeText}");
                    UpdateInspectorIfSelected(idx);
                };

                // Moving state for material color feedback
                bridge.OnActionStarted   += action =>
                {
                    rec.IsMoving = action == "navigate";
                    if (action == "navigate")
                        rec.Mat.color = Color.Lerp(spec.BaseColor, Color.white, 0.35f);
                };
                bridge.OnActionCompleted += _ =>
                {
                    rec.IsMoving  = false;
                    rec.Mat.color = spec.BaseColor;
                };

                // Speak flash (yellow)
                speaker.OnSpeak += (agentId, text) =>
                {
                    StartCoroutine(FlashColor(rec.Mat, new Color(1f, 1f, 0.2f), 2.5f, spec.BaseColor));
                    LogEvent($"[{spec.DisplayName}] \"{text}\"");
                };

                // Floating name label
                BuildNameLabel(go, spec.DisplayName, spec.BaseColor);
            }
        }

        internal class BillboardLabel : MonoBehaviour
        {
            private Camera _cam;

            private void Start()
            {
                _cam = Camera.main;
            }

            private void LateUpdate()
            {
                if (_cam == null) return;
                transform.forward = _cam.transform.forward;
            }
        }

        private static void BuildNameLabel(GameObject npc, string label, Color color)
        {
            var go = new GameObject("NameLabel");
            go.transform.SetParent(npc.transform, worldPositionStays: false);
            go.transform.localPosition = new Vector3(0f, 1.5f, 0f);
            go.transform.localScale = Vector3.one * 0.01f;

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode  = RenderMode.WorldSpace;
            canvas.worldCamera = Camera.main;

            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(200f, 60f);
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 1;
            go.AddComponent<BillboardLabel>();

            var textGO = new GameObject("T");
            textGO.transform.SetParent(go.transform, worldPositionStays: false);
            var textRt = textGO.AddComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = Vector2.zero;
            textRt.offsetMax = Vector2.zero;

            var t = textGO.AddComponent<Text>();
            t.text      = label;
            t.color     = color;
            t.font      = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                       ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            t.fontSize  = 28;
            t.alignment = TextAnchor.MiddleCenter;
        }

        // ── Simulation manager ────────────────────────────────────────────────

        private void CreateSimManager()
        {
            var go = new GameObject("SimulationManager");
            go.SetActive(false);

            _simMgr = go.AddComponent<UnitySimulationManager>();
            SetField(_simMgr, "host",        host);
            SetField(_simMgr, "port",        port);
            SetField(_simMgr, "tickRate",    0.001f);  // demo drives ticks manually
            SetField(_simMgr, "autoConnect", false);

            go.SetActive(true);

            _simMgr.OnConnected       += HandleConnected;
            _simMgr.OnDisconnected    += HandleDisconnected;
            _simMgr.OnTickComplete    += HandleTickComplete;
            _simMgr.OnTickError       += ex => LogEvent($"[tick error] {ex?.Message}");
            _simMgr.OnSimulationEvent += HandleSimEvent;

            // Speech bubbles from EventVisualizer (status overlay disabled — we have our own)
            var vizGO = new GameObject("EventVisualizer");
            var viz   = vizGO.AddComponent<EventVisualizer>();
            SetField(viz, "showOverlay", false);
        }

        // ── Connection & tick handlers ────────────────────────────────────────

        private void HandleConnected()
        {
            SetStatus($"Connected to {host}:{port}  |  {AgentSpecs.Length} agents active");
            _connectBtn?.gameObject.SetActive(false);
            _disconnectBtn?.gameObject.SetActive(true);
            _startTickBtn?.gameObject.SetActive(true);
            _stopTickBtn?.gameObject.SetActive(false);
            LogEvent("[system] Connected");
        }

        private void HandleDisconnected()
        {
            SetStatus($"Disconnected — press Connect to start");
            _connectBtn?.gameObject.SetActive(true);
            _disconnectBtn?.gameObject.SetActive(false);
            _startTickBtn?.gameObject.SetActive(false);
            _stopTickBtn?.gameObject.SetActive(false);
            _autoTicking = false;
            _paused      = false;
            foreach (var rec in _agents)
            {
                rec.IsMoving     = false;
                rec.Mat.color    = rec.Spec.BaseColor;
                rec.LastDecision = null;
            }
            LogEvent("[system] Disconnected");
            UpdateInspector();
        }

        private void HandleTickComplete(TickResult result)
        {
            _lastTickMs      = (Time.realtimeSinceStartup - _tickStartTime) * 1000f;
            _totalTicks      = result.Tick;
            _totalDecisions += result.Decisions?.Count ?? 0;
            LogEvent($"[tick {result.Tick}] {result.Decisions?.Count ?? 0} decisions  {_lastTickMs:F0} ms");
        }

        private void HandleSimEvent(SimulationEvent ev)
        {
            _totalEvents++;
            if (ev.EventType is "tick_start" or "tick_end") return;
            LogEvent($"[{ev.EventType}] {ev.AgentId}");
        }

        // ── Tick management ───────────────────────────────────────────────────

        private void DispatchTick()
        {
            _tickStartTime = Time.realtimeSinceStartup;
            _simMgr.ForceTick();
        }

        // ── Inspector ─────────────────────────────────────────────────────────

        private void SelectAgent(int idx)
        {
            _selectedIdx = idx;
            UpdateInspector();
        }

        private void UpdateInspectorIfSelected(int idx)
        {
            if (idx == _selectedIdx) UpdateInspector();
        }

        private void UpdateInspector()
        {
            if (_inspectorText == null || _agents == null) return;

            var rec = _agents[_selectedIdx];
            var d   = rec.LastDecision;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"<b>{rec.Spec.DisplayName}</b>  [{rec.Spec.AgentId}]");
            sb.AppendLine($"Role:      {rec.Spec.Role}");
            sb.AppendLine($"Cognition: {rec.Spec.CognitionType}");
            sb.AppendLine($"State:     {rec.State}");
            sb.AppendLine();

            if (d != null)
            {
                sb.AppendLine($"Last action:  {d.Action}");
                if (!string.IsNullOrEmpty(d.OutcomeText))
                    sb.AppendLine($"Outcome: {Truncate(d.OutcomeText, 42)}");
                if (d.Parameters?.Count > 0)
                {
                    var ps = new List<string>();
                    foreach (var kv in d.Parameters)
                        ps.Add($"{kv.Key}={kv.Value}");
                    sb.AppendLine($"Params:  {Truncate(string.Join(", ", ps), 42)}");
                }
            }
            else
            {
                sb.AppendLine("(no decisions yet)");
            }

            sb.AppendLine();
            sb.AppendLine($"[{_selectedIdx + 1}/{_agents.Length}]  click agent or use Prev/Next");
            _inspectorText.text = sb.ToString();
        }

        // ── Metrics ───────────────────────────────────────────────────────────

        private void UpdateMetricsText()
        {
            if (_metricsText == null) return;
            int moving = 0;
            if (_agents != null) foreach (var a in _agents) if (a.IsMoving) moving++;

            _metricsText.text =
                $"Agents:    {_agents?.Length ?? 0}  ({moving} moving)\n" +
                $"Tick:      {_totalTicks}\n" +
                $"Duration:  {(_totalTicks > 0 ? $"{_lastTickMs:F0} ms" : "-")}\n" +
                $"FPS:       {1f / Time.smoothDeltaTime:F0}\n" +
                $"Decisions: {_totalDecisions}\n" +
                $"Events:    {_totalEvents}";
        }

        private void SetStatus(string msg)
        {
            if (_statusText != null) _statusText.text = msg;
        }

        // ── Log management ────────────────────────────────────────────────────

        private void LogEvent(string line)
        {
            _eventLog.Enqueue($"[{DateTime.Now:HH:mm:ss}] {line}");
            while (_eventLog.Count > MaxLogLines) _eventLog.Dequeue();
            if (_eventLogText != null)
                _eventLogText.text = string.Join("\n", _eventLog);
        }

        private void LogDecision(string line)
        {
            _decisionLog.Enqueue($"[{DateTime.Now:HH:mm:ss}] {line}");
            while (_decisionLog.Count > MaxLogLines) _decisionLog.Dequeue();
            if (_decisionLogText != null)
                _decisionLogText.text = string.Join("\n", _decisionLog);
        }

        // ── Coroutines ────────────────────────────────────────────────────────

        private IEnumerator FlashColor(Material mat, Color flash, float duration, Color restore)
        {
            if (mat == null) yield break;
            mat.color = flash;
            yield return new WaitForSeconds(duration);
            if (mat != null) mat.color = restore;
        }

        // ── HUD ───────────────────────────────────────────────────────────────

        private void BuildHUD()
        {
            var canvasGO = new GameObject("HUDCanvas");
            canvasGO.transform.SetParent(transform, worldPositionStays: false);

            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            canvasGO.AddComponent<GraphicRaycaster>();

            var root = canvas.transform;

            // Status bar (top strip)
            var statusBg = MakePanel(root,
                new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(0, -32), new Vector2(0, 0));
            SetPanelColor(statusBg, new Color(0.04f, 0.06f, 0.10f, 0.92f));

            _statusText = MakeLbl(statusBg,
                Vector2.zero, Vector2.one,
                new Vector2(12, 2), new Vector2(-12, -2),
                $"Press Connect — backend: biomata-ws --config examples/village/sim.yaml --port {port}",
                12, TextAnchor.MiddleLeft);

            // ── Left panel (controls + metrics) ──────────────────────────────

            var leftBg = MakePanel(root,
                new Vector2(0, 1), new Vector2(0, 1),
                new Vector2(0, -420), new Vector2(248, -32));
            SetPanelColor(leftBg, new Color(0.04f, 0.05f, 0.08f, 0.90f));

            MakeLbl(leftBg, new Vector2(0,1), new Vector2(1,1),
                new Vector2(10,-6), new Vector2(-10,-26),
                "BIOMATA  VILLAGE  LIFE", 13, TextAnchor.UpperCenter);

            MakeLbl(leftBg, new Vector2(0,1), new Vector2(1,1),
                new Vector2(10,-28), new Vector2(-10,-42),
                "CONNECTION", 10, TextAnchor.UpperLeft);

            _connectBtn = MakeBtn(leftBg, "Connect",
                new Vector2(10, -46), new Vector2(110, 24),
                () => { SetStatus($"Connecting to {host}:{port}..."); _simMgr.Connect(); });
            _disconnectBtn = MakeBtn(leftBg, "Disconnect",
                new Vector2(126, -46), new Vector2(112, 24),
                () => _simMgr.Disconnect());
            _disconnectBtn.gameObject.SetActive(false);

            MakeLbl(leftBg, new Vector2(0,1), new Vector2(1,1),
                new Vector2(10,-76), new Vector2(-10,-90),
                "SIMULATION", 10, TextAnchor.UpperLeft);

            _startTickBtn = MakeBtn(leftBg, "Start Auto", new Vector2(10,-94), new Vector2(108,24), () =>
            {
                _autoTicking = true;
                _paused      = false;
                _tickAccum   = 0f;
                _startTickBtn.gameObject.SetActive(false);
                _stopTickBtn.gameObject.SetActive(true);
                LogEvent("[system] Auto-tick started");
            });
            _startTickBtn.gameObject.SetActive(false);

            _stopTickBtn = MakeBtn(leftBg, "Stop Auto", new Vector2(10,-94), new Vector2(108,24), () =>
            {
                _autoTicking = false;
                _startTickBtn.gameObject.SetActive(true);
                _stopTickBtn.gameObject.SetActive(false);
                LogEvent("[system] Auto-tick stopped");
            });
            _stopTickBtn.gameObject.SetActive(false);

            MakeBtn(leftBg, "Force Tick", new Vector2(126,-94), new Vector2(112,24), () =>
            {
                if (_simMgr.IsConnected && !_paused) DispatchTick();
            });

            MakeBtn(leftBg, "Pause",  new Vector2(10, -124), new Vector2(72, 24), () =>
            {
                _paused = true;
                LogEvent("[system] Paused (local — no ticks sent)");
            });
            MakeBtn(leftBg, "Resume", new Vector2(86, -124), new Vector2(72, 24), () =>
            {
                _paused = false;
                LogEvent("[system] Resumed");
            });
            MakeBtn(leftBg, "Reset",  new Vector2(162,-124), new Vector2(76, 24), () =>
            {
                _autoTicking = false; _paused = false;
                _totalTicks = 0; _totalDecisions = 0; _totalEvents = 0;
                _eventLog.Clear(); _decisionLog.Clear();
                LogEvent("[system] Reconnecting...");
                _simMgr.Disconnect();
                StartCoroutine(ReconnectAfterDelay(1.2f));
            });

            MakeLbl(leftBg, new Vector2(0,1), new Vector2(1,1),
                new Vector2(10,-158), new Vector2(-10,-172),
                "METRICS", 10, TextAnchor.UpperLeft);

            _metricsText = MakeLbl(leftBg, new Vector2(0,1), new Vector2(1,1),
                new Vector2(10,-174), new Vector2(-10,-340),
                "Connecting...", 11, TextAnchor.UpperLeft);

            // ── Right panel (agent inspector) ─────────────────────────────────

            var rightBg = MakePanel(root,
                new Vector2(1, 1), new Vector2(1, 1),
                new Vector2(-268, -340), new Vector2(0, -32));
            SetPanelColor(rightBg, new Color(0.04f, 0.05f, 0.08f, 0.90f));

            MakeLbl(rightBg, new Vector2(0,1), new Vector2(1,1),
                new Vector2(10,-6), new Vector2(-10,-22),
                "AGENT  INSPECTOR", 11, TextAnchor.UpperCenter);

            _inspectorText = MakeLbl(rightBg, new Vector2(0,1), new Vector2(1,1),
                new Vector2(10,-26), new Vector2(-10,-240),
                "Click an agent or use Prev / Next", 11, TextAnchor.UpperLeft);
            _inspectorText.horizontalOverflow = HorizontalWrapMode.Wrap;

            MakeBtn(rightBg, "< Prev", new Vector2(10,-242), new Vector2(118,24), () =>
            {
                _selectedIdx = (_selectedIdx - 1 + _agents.Length) % _agents.Length;
                UpdateInspector();
            });
            MakeBtn(rightBg, "Next >", new Vector2(136,-242), new Vector2(118,24), () =>
            {
                _selectedIdx = (_selectedIdx + 1) % _agents.Length;
                UpdateInspector();
            });

            // ── Bottom panel (logs) ───────────────────────────────────────────

            var botBg = MakePanel(root,
                new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(0, 0), new Vector2(0, 200));
            SetPanelColor(botBg, new Color(0.03f, 0.04f, 0.07f, 0.92f));

            // Event log (left half)
            MakeLbl(botBg, new Vector2(0,1), new Vector2(0.5f,1),
                new Vector2(10,-4), new Vector2(-4,-20),
                "EVENT LOG", 10, TextAnchor.UpperLeft);
            _eventLogText = MakeLbl(botBg, new Vector2(0,1), new Vector2(0.5f,1),
                new Vector2(10,-22), new Vector2(-4,-8),
                "(waiting for connection)", 9, TextAnchor.LowerLeft);
            _eventLogText.verticalOverflow = VerticalWrapMode.Truncate;

            // Decision log (right half)
            MakeLbl(botBg, new Vector2(0.5f,1), new Vector2(1,1),
                new Vector2(4,-4), new Vector2(-10,-20),
                "DECISION LOG", 10, TextAnchor.UpperLeft);
            _decisionLogText = MakeLbl(botBg, new Vector2(0.5f,1), new Vector2(1,1),
                new Vector2(4,-22), new Vector2(-10,-8),
                "(waiting for decisions)", 9, TextAnchor.LowerLeft);
            _decisionLogText.verticalOverflow = VerticalWrapMode.Truncate;

            // Initial inspector state
            UpdateInspector();
        }

        private IEnumerator ReconnectAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            _simMgr.Connect();
        }

        // ── UI factory helpers ────────────────────────────────────────────────

        private static RectTransform MakePanel(
            Transform parent, Vector2 ancMin, Vector2 ancMax, Vector2 offMin, Vector2 offMax)
        {
            var go = new GameObject("Panel");
            go.transform.SetParent(parent, false);

            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = ancMin;
            rt.anchorMax = ancMax;
            rt.offsetMin = offMin;
            rt.offsetMax = offMax;

            go.AddComponent<CanvasRenderer>();
            go.AddComponent<Image>();
            return rt;
        }

        private static void SetPanelColor(RectTransform rt, Color color) =>
            rt.GetComponent<Image>().color = color;

        private static Button MakeBtn(
            RectTransform parent, string label, Vector2 anchoredPos, Vector2 size, Action onClick)
        {
            var go = new GameObject($"Btn_{label}");
            go.transform.SetParent(parent, false);

            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin        = new Vector2(0, 1);
            rt.anchorMax        = new Vector2(0, 1);
            rt.pivot            = new Vector2(0, 1);
            rt.sizeDelta        = size;
            rt.anchoredPosition = anchoredPos;

            go.AddComponent<CanvasRenderer>();
            var img = go.AddComponent<Image>();
            img.color = new Color(0.12f, 0.16f, 0.22f, 0.95f);

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
            t.fontSize  = 11;
            t.alignment = TextAnchor.MiddleCenter;
            return btn;
        }

        private static Text MakeLbl(
            RectTransform parent,
            Vector2 ancMin, Vector2 ancMax,
            Vector2 offMin, Vector2 offMax,
            string content, int size, TextAnchor align)
        {
            var go = new GameObject("Lbl");
            go.transform.SetParent(parent, false);

            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = ancMin;
            rt.anchorMax = ancMax;
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

            var shader =
                Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Standard");

            var mat = new Material(shader);

            // URP transparency
            if (shader.name.Contains("Universal"))
            {
                mat.SetFloat("_Surface", 1); // Transparent
                mat.SetFloat("_Blend", 0);
                mat.SetFloat("_ZWrite", 0);

                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.renderQueue = 3000;
            }

            mat.color = color;
            r.material = mat;
        }

        private static void SetField(object target, string name, object value)
        {
            const BindingFlags f =
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
            target.GetType().GetField(name, f)?.SetValue(target, value);
        }

        private static void EnsureEventSystem()
        {
            if (FindFirstObjectByType<EventSystem>() != null) return;
            var es = new GameObject("EventSystem");
            es.AddComponent<EventSystem>();
            es.AddComponent<StandaloneInputModule>();
        }

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s[..max] + "…";
    }
}
