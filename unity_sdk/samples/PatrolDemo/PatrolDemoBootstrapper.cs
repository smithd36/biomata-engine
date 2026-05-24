// Biomata SDK — Patrol Demo
//
// Smallest visual proof that Biomata can drive visible gameplay behaviour.
// Drop this component on an empty GameObject and press Play.
//
// The demo builds an entire scene at runtime — floor, waypoint markers, two
// NPC capsules, an overhead camera, a HUD, and the simulation manager — with
// no prefab or scene authoring required.
//
// What it proves
// ──────────────
//   Python WaypointBrain → navigate engine_command
//     → MoveActionHandler → visible Transform movement in Unity
//
// Prerequisites
// ─────────────
//   Python backend running:
//     biomata-ws --config examples/patrol/sim.yaml --port 8765
//
//   Default host/port: localhost:8765 — override in the Inspector.
//
// Agent IDs defined here MUST match examples/patrol/sim.yaml exactly.

using System.Collections.Generic;
using System.Reflection;
using Biomata.Integration;
using Biomata.Integration.Actions;
using Biomata.Integration.Observations;
using UnityEngine;
using UnityEngine.UI;

namespace Biomata.Samples
{
    [AddComponentMenu("Biomata/Samples/Patrol Demo")]
    public class PatrolDemoBootstrapper : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("Backend Connection")]
        [SerializeField] private string host = "localhost";
        [SerializeField] private int    port = 8765;

        [Header("Tick Rate")]
        [Tooltip("Simulation ticks per second driven by UnitySimulationManager.")]
        [SerializeField] private float tickRate = 2f;

        // ── Patrol data (mirrors examples/patrol/sim.yaml) ────────────────────

        private static readonly AgentSpec[] Agents =
        {
            new AgentSpec(
                agentId:   "scout_001",
                agentName: "Scout",
                color:     new Color(0.25f, 0.55f, 1f),
                start:     new Vector3(0f, 0f, 0f),
                waypoints: new[]
                {
                    new Vector3(0f, 0f, 0f),
                    new Vector3(8f, 0f, 0f),
                    new Vector3(8f, 0f, 8f),
                    new Vector3(0f, 0f, 8f),
                }
            ),
            new AgentSpec(
                agentId:   "guard_001",
                agentName: "Guard",
                color:     new Color(1f, 0.4f, 0.3f),
                start:     new Vector3(4f, 0f, -4f),
                waypoints: new[]
                {
                    new Vector3( 4f, 0f, -4f),
                    new Vector3(-4f, 0f, -4f),
                    new Vector3(-4f, 0f,  4f),
                    new Vector3( 4f, 0f,  4f),
                }
            ),
        };

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private Text _statusLabel;

        private void Awake()
        {
            BuildEnvironment();
            BuildCamera();
            BuildHUD();
        }

        private void Start()
        {
            var mgr = BuildSimulationManager();
            BuildEventVisualizer(mgr);
            BuildNPCs();
        }

        // ── Scene construction ────────────────────────────────────────────────

        private void BuildEnvironment()
        {
            // Floor
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor";
            floor.transform.position   = new Vector3(2f, -0.01f, 2f);
            floor.transform.localScale = new Vector3(2.4f, 1f, 2.4f);
            SetColor(floor, new Color(0.3f, 0.31f, 0.32f));

            // Waypoint markers for each agent
            foreach (var spec in Agents)
            {
                var markerColor = new Color(spec.Color.r, spec.Color.g, spec.Color.b, 0.6f);
                for (var i = 0; i < spec.Waypoints.Length; i++)
                {
                    var wp = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    wp.name = $"WP_{spec.AgentId}_{i}";
                    wp.transform.position   = spec.Waypoints[i] + new Vector3(0f, 0.15f, 0f);
                    wp.transform.localScale = new Vector3(0.35f, 0.15f, 0.35f);
                    SetColor(wp, markerColor);
                    Destroy(wp.GetComponent<Collider>());
                }
            }
        }

        private void BuildCamera()
        {
            // Move the main camera to an overhead-ish angle covering the whole patrol area.
            var cam = Camera.main;
            if (cam == null) return;

            cam.transform.position = new Vector3(2f, 16f, -10f);
            cam.transform.LookAt(new Vector3(2f, 0f, 3f));
            cam.backgroundColor    = new Color(0.08f, 0.08f, 0.1f);
            cam.clearFlags         = CameraClearFlags.SolidColor;
        }

        private UnitySimulationManager BuildSimulationManager()
        {
            var go = new GameObject("SimulationManager");
            go.SetActive(false);

            var mgr = go.AddComponent<UnitySimulationManager>();

            // Set private serialized fields before Awake fires.
            SetField(mgr, "host",        host);
            SetField(mgr, "port",        port);
            SetField(mgr, "tickRate",    tickRate);
            SetField(mgr, "autoConnect", true);

            go.SetActive(true);

            mgr.OnConnected    += () => SetStatus("Connected — NPCs patrolling");
            mgr.OnDisconnected += () => SetStatus("Disconnected");
            mgr.OnTickError    += ex  => SetStatus($"Tick error: {ex?.Message}");

            SetStatus($"Connecting to {host}:{port}…");
            return mgr;
        }

        private static void BuildEventVisualizer(UnitySimulationManager mgr)
        {
            var go = new GameObject("EventVisualizer");
            go.AddComponent<EventVisualizer>();
        }

        private void BuildNPCs()
        {
            foreach (var spec in Agents)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                go.name = spec.AgentName;
                go.transform.position = spec.Start + new Vector3(0f, 1f, 0f);
                SetColor(go, spec.Color);

                // Add integration components in dependency order.
                // Keep the GO active — Awake fires per-AddComponent but each
                // component only needs things added before it.
                go.AddComponent<TransformObservationProvider>();
                go.AddComponent<ObservationCollector>();
                go.AddComponent<MoveActionHandler>();
                go.AddComponent<ActionExecutor>();

                var bridge = go.AddComponent<UnityAgentBridge>();
                SetField(bridge, "agentId",    spec.AgentId);
                SetField(bridge, "agentName",  spec.AgentName);
                // IdleBrain is registered server-side in the patrol sim.yaml;
                // the brain class field here is informational only (registration
                // uses the value from the YAML config on the Python side).
                SetField(bridge, "brainClass", "src.plugins.builtin.idle_brain.brain.IdleBrain");

                // Name label above the capsule (world-space canvas).
                BuildNameLabel(go, spec.AgentName, spec.Color);
            }
        }

        private static void BuildNameLabel(GameObject npc, string label, Color color)
        {
            var go = new GameObject("NameLabel");
            go.transform.SetParent(npc.transform, worldPositionStays: false);
            go.transform.localPosition = new Vector3(0f, 1.4f, 0f);

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode     = RenderMode.WorldSpace;
            canvas.worldCamera    = Camera.main;

            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(2f, 0.5f);

            go.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 10;

            var textGO = new GameObject("Text");
            textGO.transform.SetParent(go.transform, worldPositionStays: false);
            var textRt = textGO.AddComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = Vector2.zero;
            textRt.offsetMax = Vector2.zero;

            var text = textGO.AddComponent<Text>();
            text.text      = label;
            text.color     = color;
            text.font      = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                          ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize  = 3;
            text.alignment = TextAnchor.MiddleCenter;
        }

        // ── Status HUD ────────────────────────────────────────────────────────

        private void BuildHUD()
        {
            var canvasGO = new GameObject("HUDCanvas");
            canvasGO.transform.SetParent(transform, worldPositionStays: false);

            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGO.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            canvasGO.AddComponent<GraphicRaycaster>();

            _statusLabel = MakeLabel(
                canvas.transform,
                new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(10f, -10f), new Vector2(-10f, -40f),
                "Initialising…", 16, TextAnchor.UpperLeft
            );

            MakeLabel(
                canvas.transform,
                new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(10f, -36f), new Vector2(-10f, -58f),
                "Press F2 to toggle overlay  |  biomata-ws --config examples/patrol/sim.yaml --port 8765",
                11, TextAnchor.UpperLeft
            );
        }

        private void SetStatus(string msg)
        {
            if (_statusLabel != null)
                _statusLabel.text = $"[Patrol Demo]  {msg}";
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static void SetColor(GameObject go, Color color)
        {
            var r = go.GetComponent<Renderer>();
            if (r == null) return;
            var mat = new Material(Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit"))
            {
                color = color,
            };
            r.material = mat;
        }

        private static void SetField(object target, string fieldName, object value)
        {
            const BindingFlags flags =
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;
            var field = target.GetType().GetField(fieldName, flags);
            field?.SetValue(target, value);
        }

        private static Text MakeLabel(
            Transform  parent,
            Vector2    anchorMin,   Vector2 anchorMax,
            Vector2    offsetMin,   Vector2 offsetMax,
            string     content,     int fontSize, TextAnchor alignment)
        {
            var go = new GameObject("Label");
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

        // ── AgentSpec ─────────────────────────────────────────────────────────

        private readonly struct AgentSpec
        {
            public readonly string    AgentId;
            public readonly string    AgentName;
            public readonly Color     Color;
            public readonly Vector3   Start;
            public readonly Vector3[] Waypoints;

            public AgentSpec(
                string agentId, string agentName,
                Color color, Vector3 start, Vector3[] waypoints)
            {
                AgentId   = agentId;
                AgentName = agentName;
                Color     = color;
                Start     = start;
                Waypoints = waypoints;
            }
        }
    }
}
