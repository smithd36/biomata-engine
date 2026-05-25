// Biomata SDK — Social Village Demo
//
// Flagship showcase: 13 autonomous NPCs living in a low-poly RPG village,
// all driven by OllamaLLMBrain (qwen2.5:14b). Social interactions update
// relationships; villagers greet each other, merchants call out, the scholar
// asks questions, the innkeeper gossips.
//
// Capability breakdown (matches examples/village/sim.yaml):
//   [patrol, authority]  — Guards (Aldric, Berna): navigate + speak only; no socialize
//   [farm]               — Farmer (Edith): navigate + speak only; no socialize
//   [social]             — Villagers, Innkeeper, Traveler, Townsfolk: full social actions
//   [trade, social]      — Merchant (Silas): trade-focused socializer
//   [knowledge, social]  — Scholar (Wren): intellectual socializer
//
// Environment is built at runtime using RPG Poly Pack Lite (RPGPP_LT) prefabs.
// Prefabs are auto-discovered in the editor via AssetDatabase.FindAssets.
// Drop this component on an empty GameObject and press Play.
//
// Agent IDs defined here MUST match examples/village/sim.yaml exactly.
// Agents are pre-declared in the YAML; autoRegister=false on all bridges.
//
// Backend:
//   biomata-ws --config examples/village/sim.yaml --port 8765
//
// Ollama (all agents):
//   ollama serve && ollama pull qwen2.5:14b

using System;
using System.Collections;
using System.Collections.Generic;
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
        public readonly string  CognitionType;   // "Deterministic" | "Social" | "LLM (Ollama)"
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
        public VillageAgentSpec    Spec;
        public Material            Mat;
        public UnityAgentBridge    Bridge;
        public AgentDecisionResult LastDecision;
        public bool                IsMoving;
        public int                 SocialCount;
        public string              LastSocialWith;

        public string State
        {
            get
            {
                if (LastDecision == null) return "Waiting";
                return LastDecision.Action switch
                {
                    "navigate"   => "Moving",
                    "idle"       => "Idle",
                    "interact"   => "Interacting",
                    "speak"      => "Speaking",
                    "socialize"  => "Socializing",
                    _            => "Active",
                };
            }
        }
    }

    internal class AgentClickReceiver : MonoBehaviour
    {
        public Action OnClicked;
        private void OnMouseDown() => OnClicked?.Invoke();
    }

    // ── RPGPP_LT prefab set ───────────────────────────────────────────────────

    [Serializable]
    internal struct VillagePrefabs
    {
        // Buildings
        public GameObject tavern;        // Tavern.prefab
        public GameObject building02;    // rpgpp_lt_building_02 → Market
        public GameObject building03;    // rpgpp_lt_building_03 → Inn / TownHall bg
        public GameObject building04;    // rpgpp_lt_building_04 → Barn/Farm
        public GameObject building05;    // rpgpp_lt_building_05 → Guard posts / houses
        // Key POI props
        public GameObject well;          // rpgpp_lt_well_01
        public GameObject wagon;         // rpgpp_lt_wagon_01
        public GameObject awning;        // rpgpp_lt_awning_standing_01a
        // Vegetation
        public GameObject treePine;      // rpgpp_lt_tree_pine_01
        public GameObject tree01;        // rpgpp_lt_tree_01
        public GameObject tree02;        // rpgpp_lt_tree_02
        public GameObject bush01;        // rpgpp_lt_bush_01
        public GameObject bush02;        // rpgpp_lt_bush_02
        // Rocks
        public GameObject rock01;        // rpgpp_lt_rock_01
        public GameObject rock02;        // rpgpp_lt_rock_02
        public GameObject rockSmall;     // rpgpp_lt_rock_small_01
        // Exterior
        public GameObject fenceA;        // rpgpp_lt_fence_wood_01a
        public GameObject fenceB;        // rpgpp_lt_fence_wood_01b
        public GameObject fenceCorner;   // rpgpp_lt_fence_wood_01_corner_a
        // Props
        public GameObject bench;         // rpgpp_lt_bench_wood_01
        public GameObject barrel01;      // rpgpp_lt_barrel_01
        public GameObject barrel02;      // rpgpp_lt_barrel_02
        public GameObject crate01;       // rpgpp_lt_crate_01
        public GameObject crate02;       // rpgpp_lt_crate_02
        public GameObject sack01;        // rpgpp_lt_sack_01
        public GameObject sack02;        // rpgpp_lt_sack_02
        public GameObject table;         // rpgpp_lt_table_01
        public GameObject bannerA;       // rpgpp_lt_banner_01a
        public GameObject bannerB;       // rpgpp_lt_banner_01b
        public GameObject logWood;       // rpgpp_lt_log_wood_01
        // Sky
        public GameObject sky;           // rpgpp_lt_sky_01
        // Ground material
        public Material   grassMat;      // GrassBase.mat
    }

    // ── Main demo ─────────────────────────────────────────────────────────────

    [AddComponentMenu("Biomata/Samples/Social Village MVP")]
    public class VillageLifeDemo : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("Backend")]
        [SerializeField] private string host = "localhost";
        [SerializeField] private int    port = 8765;

        [Header("Simulation")]
        [Tooltip("Ticks per second. Keep ≤ 0.5 when Ollama agents are active.")]
        [SerializeField] private float tickRate  = 0.2f;
        [SerializeField] private float moveSpeed = 6f;

        // ── 13 village agents (IDs must match sim.yaml) ───────────────────────

        private static readonly VillageAgentSpec[] AgentSpecs =
        {
            // Guards — capabilities: [patrol, authority]  (navigate + speak; no socialize)
            new VillageAgentSpec("guard_001",     "Aldric",  "Guard",      "LLM (Ollama)",
                new Color(1f, 0.22f, 0.10f),    new Vector3(  0f, 1f,  14f)),
            new VillageAgentSpec("guard_002",     "Berna",   "Guard",      "LLM (Ollama)",
                new Color(1f, 0.50f, 0.05f),    new Vector3(  0f, 1f, -14f)),
            // Villagers — capabilities: [social]
            new VillageAgentSpec("villager_001",  "Mira",    "Villager",   "LLM (Ollama)",
                new Color(1f, 0.90f, 0.20f),    new Vector3(  0f, 1f,   0f)),
            new VillageAgentSpec("villager_002",  "Tomas",   "Villager",   "LLM (Ollama)",
                new Color(0.20f, 0.80f, 0.90f), new Vector3(  6f, 1f,   4f)),
            new VillageAgentSpec("villager_003",  "Finn",    "Villager",   "LLM (Ollama)",
                new Color(0.15f, 0.75f, 0.65f), new Vector3( -4f, 1f,   2f)),
            new VillageAgentSpec("villager_004",  "Dalia",   "Villager",   "LLM (Ollama)",
                new Color(0.95f, 0.55f, 0.40f), new Vector3(  4f, 1f,  -4f)),
            // Merchant — capabilities: [trade, social]
            new VillageAgentSpec("merchant_001",  "Silas",   "Merchant",   "LLM (Ollama)",
                new Color(1f, 0.75f, 0.00f),    new Vector3( 14f, 1f,   0f)),
            // Farmer — capabilities: [farm]  (navigate + speak; no socialize)
            new VillageAgentSpec("farmer_001",    "Edith",   "Farmer",     "LLM (Ollama)",
                new Color(0.30f, 0.80f, 0.20f), new Vector3(  2f, 1f, -14f)),
            // Innkeeper — capabilities: [social]
            new VillageAgentSpec("innkeeper_001", "Rogan",   "Innkeeper",  "LLM (Ollama)",
                new Color(0.60f, 0.20f, 0.85f), new Vector3(-10f, 1f,   5f)),
            // Traveler — capabilities: [social]
            new VillageAgentSpec("traveler_001",  "Lyra",    "Traveler",   "LLM (Ollama)",
                new Color(0.90f, 0.90f, 0.90f), new Vector3( -2f, 1f,   2f)),
            // Scholar — capabilities: [knowledge, social]
            new VillageAgentSpec("scholar_001",   "Wren",    "Scholar",    "LLM (Ollama)",
                new Color(0.75f, 0.65f, 1.00f), new Vector3( -6f, 1f,   4f)),
            // Townsfolk — capabilities: [social]
            new VillageAgentSpec("townsfolk_001", "Bram",    "Townsfolk",  "LLM (Ollama)",
                new Color(0.20f, 0.40f, 0.90f), new Vector3(  2f, 1f,   2f)),
            new VillageAgentSpec("townsfolk_002", "Nessa",   "Townsfolk",  "LLM (Ollama)",
                new Color(0.90f, 0.40f, 0.65f), new Vector3(-10f, 1f,   7f)),
        };

        // ── Runtime state ─────────────────────────────────────────────────────

        private UnitySimulationManager  _simMgr;
        private VillageAgentRecord[]    _agents;
        private int                     _selectedIdx;
        private VillagePrefabs          _pf;

        private readonly Dictionary<string, VillageAgentRecord> _agentById = new();

        private bool  _autoTicking;
        private bool  _paused;
        private float _tickAccum;
        private float _tickStartTime;
        private float _lastTickMs;
        private int   _totalTicks;
        private int   _totalDecisions;
        private int   _totalEvents;
        private int   _totalSocializations;

        // ── UI references ─────────────────────────────────────────────────────

        private Text   _statusText;
        private Text   _metricsText;
        private Text   _inspectorText;
        private Text   _eventLogText;
        private Text   _agentStatusText;
        private Button _connectBtn;
        private Button _disconnectBtn;
        private Button _startTickBtn;
        private Button _stopTickBtn;

        private readonly Queue<string> _eventLog = new Queue<string>();
        private const int MaxLogLines = 16;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void Awake()
        {
#if UNITY_EDITOR
            AutoLoadPrefabs();
#endif
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

        // ── Prefab auto-discovery (editor only) ───────────────────────────────

#if UNITY_EDITOR
        private void AutoLoadPrefabs()
        {
            _pf.tavern      = FindPrefab("Tavern");
            _pf.building02  = FindPrefab("rpgpp_lt_building_02");
            _pf.building03  = FindPrefab("rpgpp_lt_building_03");
            _pf.building04  = FindPrefab("rpgpp_lt_building_04");
            _pf.building05  = FindPrefab("rpgpp_lt_building_05");
            _pf.well        = FindPrefab("rpgpp_lt_well_01");
            _pf.wagon       = FindPrefab("rpgpp_lt_wagon_01");
            _pf.awning      = FindPrefab("rpgpp_lt_awning_standing_01a");
            _pf.treePine    = FindPrefab("rpgpp_lt_tree_pine_01");
            _pf.tree01      = FindPrefab("rpgpp_lt_tree_01");
            _pf.tree02      = FindPrefab("rpgpp_lt_tree_02");
            _pf.bush01      = FindPrefab("rpgpp_lt_bush_01");
            _pf.bush02      = FindPrefab("rpgpp_lt_bush_02");
            _pf.rock01      = FindPrefab("rpgpp_lt_rock_01");
            _pf.rock02      = FindPrefab("rpgpp_lt_rock_02");
            _pf.rockSmall   = FindPrefab("rpgpp_lt_rock_small_01");
            _pf.fenceA      = FindPrefab("rpgpp_lt_fence_wood_01a");
            _pf.fenceB      = FindPrefab("rpgpp_lt_fence_wood_01b");
            _pf.fenceCorner = FindPrefab("rpgpp_lt_fence_wood_01_corner_a");
            _pf.bench       = FindPrefab("rpgpp_lt_bench_wood_01");
            _pf.barrel01    = FindPrefab("rpgpp_lt_barrel_01");
            _pf.barrel02    = FindPrefab("rpgpp_lt_barrel_02");
            _pf.crate01     = FindPrefab("rpgpp_lt_crate_01");
            _pf.crate02     = FindPrefab("rpgpp_lt_crate_02");
            _pf.sack01      = FindPrefab("rpgpp_lt_sack_01");
            _pf.sack02      = FindPrefab("rpgpp_lt_sack_02");
            _pf.table       = FindPrefab("rpgpp_lt_table_01");
            _pf.bannerA     = FindPrefab("rpgpp_lt_banner_01a");
            _pf.bannerB     = FindPrefab("rpgpp_lt_banner_01b");
            _pf.logWood     = FindPrefab("rpgpp_lt_log_wood_01");
            _pf.sky         = FindPrefab("rpgpp_lt_sky_01");
            _pf.grassMat    = FindMaterial("GrassBase");

            // Log how many were found
            int found = CountLoaded();
            Debug.Log($"[VillageDemo] RPGPP_LT: {found} prefabs/materials auto-loaded.");
        }

        private static GameObject FindPrefab(string name)
        {
            var guids = UnityEditor.AssetDatabase.FindAssets($"{name} t:Prefab");
            if (guids.Length == 0) return null;
            var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]);
            return UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }

        private static Material FindMaterial(string name)
        {
            var guids = UnityEditor.AssetDatabase.FindAssets($"{name} t:Material");
            if (guids.Length == 0) return null;
            var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]);
            return UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(path);
        }

        private int CountLoaded()
        {
            int n = 0;
            if (_pf.tavern     != null) n++;
            if (_pf.building02 != null) n++;
            if (_pf.building03 != null) n++;
            if (_pf.building04 != null) n++;
            if (_pf.building05 != null) n++;
            if (_pf.well       != null) n++;
            if (_pf.wagon      != null) n++;
            if (_pf.treePine   != null) n++;
            if (_pf.tree01     != null) n++;
            if (_pf.tree02     != null) n++;
            if (_pf.fenceA     != null) n++;
            if (_pf.bench      != null) n++;
            if (_pf.barrel01   != null) n++;
            if (_pf.sky        != null) n++;
            if (_pf.grassMat   != null) n++;
            return n;
        }
#endif

        // ── Scene building ────────────────────────────────────────────────────

        private void BuildScene()
        {
            BuildLighting();
            BuildGround();
            BuildSky();
            BuildPaths();
            BuildTownSquare();
            BuildWell();
            BuildMarket();
            BuildTavern();
            BuildFarm();
            BuildNorthGate();
            BuildSouthGate();
            BuildTreeBorder();
        }

        // ── Lighting + sky ────────────────────────────────────────────────────

        private void BuildLighting()
        {
            var sunGO = new GameObject("Sun");
            var sun   = sunGO.AddComponent<Light>();
            sun.type      = LightType.Directional;
            sun.intensity = 1.3f;
            sun.color     = new Color(1.0f, 0.95f, 0.82f);
            sunGO.transform.eulerAngles = new Vector3(52f, 30f, 0f);

            RenderSettings.ambientMode      = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor  = new Color(0.52f, 0.70f, 0.90f);
            RenderSettings.ambientGroundColor = new Color(0.20f, 0.24f, 0.18f);
        }

        private void BuildSky()
        {
            if (_pf.sky != null)
            {
                var sky = Instantiate(_pf.sky, Vector3.zero, Quaternion.identity);
                sky.name = "SkySphere";
                // Sky domes in RPGPP_LT are typically ~100 units; scale up to cover village
                sky.transform.localScale = Vector3.one * 3f;
                DisableColliders(sky);
            }
        }

        // ── Ground ────────────────────────────────────────────────────────────

        private void BuildGround()
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.position   = new Vector3(0f, -0.02f, 0f);
            ground.transform.localScale = Vector3.one * 5.5f;

            if (_pf.grassMat != null)
                ground.GetComponent<Renderer>().material = _pf.grassMat;
            else
                ApplyColor(ground, new Color(0.33f, 0.44f, 0.24f));
        }

        // ── Paths ─────────────────────────────────────────────────────────────

        private void BuildPaths()
        {
            // N-S main road through town
            MakePath(new Vector3(0f, 0f, 0f), new Vector3(1.8f, 1f, 11f));
            // E-W market road
            MakePath(new Vector3(7f, 0f, 0f), new Vector3(8f, 1f, 1.8f));
            // Tavern path
            MakePath(new Vector3(-5f, 0f, 2.5f), new Vector3(6f, 1f, 1.5f),
                Quaternion.Euler(0f, 27f, 0f));
            // Farm path
            MakePath(new Vector3(1f, 0f, -7f), new Vector3(1.5f, 1f, 7.5f));
        }

        private void MakePath(Vector3 pos, Vector3 scale, Quaternion? rot = null)
        {
            var p = GameObject.CreatePrimitive(PrimitiveType.Plane);
            p.name = "Path";
            p.transform.position   = pos;
            p.transform.localScale = scale;
            p.transform.rotation   = rot ?? Quaternion.identity;
            ApplyColor(p, new Color(0.52f, 0.47f, 0.38f));
            Destroy(p.GetComponent<Collider>());
        }

        // ── POI: Town Square ──────────────────────────────────────────────────

        private void BuildTownSquare()
        {
            // Paved plaza disc
            var plaza = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            plaza.name = "TownSquarePlaza";
            plaza.transform.position   = new Vector3(0f, -0.01f, 0f);
            plaza.transform.localScale = new Vector3(3f, 0.01f, 3f);
            ApplyColor(plaza, new Color(0.55f, 0.52f, 0.46f));
            Destroy(plaza.GetComponent<Collider>());

            // Town Hall building set back from square
            var dir03 = FaceCenter(new Vector3(-2.5f, 0f, -2.5f));
            Spawn(_pf.building03, new Vector3(-2.5f, 0f, -2.5f), dir03, "TownHall");

            // Benches around the square
            Spawn(_pf.bench, new Vector3( 1.25f, 0f,  0.5f), Quaternion.Euler(0, -90, 0), "Bench_E");
            Spawn(_pf.bench, new Vector3(-1.25f, 0f, -0.5f), Quaternion.Euler(0,  90, 0), "Bench_W");

            // Banner pole at square center
            Spawn(_pf.bannerA, new Vector3(0f, 0f, 0f), Quaternion.identity, "Banner_Square");

            // Small rocks for decoration
            Spawn(_pf.rockSmall, new Vector3( 0.75f, 0f,  1.25f), Quaternion.Euler(0, 35f, 0), "Rock_Sq1");
            Spawn(_pf.rockSmall, new Vector3(-0.75f, 0f, -1.25f), Quaternion.Euler(0, 80f, 0), "Rock_Sq2");
        }

        // ── POI: Well ─────────────────────────────────────────────────────────

        private void BuildWell()
        {
            // Well prefab is the POI anchor — place it exactly at the sim coordinate
            if (!Spawn(_pf.well, new Vector3(6f, 0f, 4f), Quaternion.identity, "Well"))
            {
                // Fallback: stone cylinder
                var w = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                w.name = "WellFallback";
                w.transform.position   = new Vector3(6f, 0.55f, 4f);
                w.transform.localScale = new Vector3(1.0f, 0.55f, 1.0f);
                ApplyColor(w, new Color(0.40f, 0.35f, 0.28f));
            }

            // Small prop cluster near well (offsets halved from POI centre 6,4)
            Spawn(_pf.barrel01, new Vector3(6.5f,  0f, 3.75f), Quaternion.Euler(0, 20f, 0), "Barrel_Well1");
            Spawn(_pf.rock01,   new Vector3(5.6f,  0f, 4.5f),  Quaternion.Euler(0, 55f, 0), "Rock_Well");
            Spawn(_pf.bush01,   new Vector3(6.75f, 0f, 4.5f),  Quaternion.identity,          "Bush_Well");
        }

        // ── POI: Market ───────────────────────────────────────────────────────

        private void BuildMarket()
        {
            // Main market building (offset halved from POI centre 14,0)
            Spawn(_pf.building02, new Vector3(15f, 0f, 0.5f), FaceCenter(new Vector3(15f, 0f, 0.5f)), "MarketBuilding");

            // Market stall with awning
            Spawn(_pf.awning, new Vector3(13.5f, 0f,  0f), Quaternion.Euler(0, 180f, 0), "MarketAwning");
            Spawn(_pf.table,  new Vector3(13.5f, 0f,  0f), Quaternion.Euler(0, 90f, 0),  "MarketTable");

            // Goods and crates
            Spawn(_pf.barrel01, new Vector3(13.0f,  0f,  0.75f), Quaternion.Euler(0,  15f, 0), "Barrel_M1");
            Spawn(_pf.barrel02, new Vector3(13.25f, 0f, -0.5f),  Quaternion.Euler(0, -20f, 0), "Barrel_M2");
            Spawn(_pf.crate01,  new Vector3(12.75f, 0f,  0.5f),  Quaternion.Euler(0,  40f, 0), "Crate_M1");
            Spawn(_pf.crate02,  new Vector3(13.0f,  0f, -0.75f), Quaternion.Euler(0, -45f, 0), "Crate_M2");
            Spawn(_pf.sack01,   new Vector3(13.75f, 0f,  0.75f), Quaternion.identity,           "Sack_M1");
            Spawn(_pf.sack02,   new Vector3(13.5f,  0f, -0.6f),  Quaternion.Euler(0, 60f, 0),  "Sack_M2");

            // Merchant wagon nearby
            Spawn(_pf.wagon, new Vector3(14f, 0f, -2f), Quaternion.Euler(0, 90f, 0), "Wagon_Market");

            // Market banner
            Spawn(_pf.bannerB, new Vector3(14f, 0f, 1.25f), Quaternion.identity, "Banner_Market");
        }

        // ── POI: Tavern ───────────────────────────────────────────────────────

        private void BuildTavern()
        {
            // The Tavern prefab is the primary POI structure
            var tavernRot = FaceCenter(new Vector3(-10f, 0f, 5f));
            if (!Spawn(_pf.tavern, new Vector3(-10f, 0f, 5f), tavernRot, "Tavern"))
            {
                // Fallback if tavern prefab missing
                Spawn(_pf.building03, new Vector3(-10f, 0f, 5f), tavernRot, "TavernFallback");
            }

            // Outdoor seating (offsets halved from POI centre -10,5)
            Spawn(_pf.bench,    new Vector3(-9.0f,  0f, 4.5f),  Quaternion.Euler(0, -60f, 0), "Bench_Tav1");
            Spawn(_pf.bench,    new Vector3(-9.0f,  0f, 5.75f), Quaternion.Euler(0,  60f, 0), "Bench_Tav2");
            Spawn(_pf.table,    new Vector3(-9.0f,  0f, 5.1f),  Quaternion.Euler(0,  30f, 0), "Table_Tav");

            // Barrels outside
            Spawn(_pf.barrel01, new Vector3(-11.25f, 0f, 4.25f), Quaternion.Euler(0, 10f, 0), "Barrel_Tav1");
            Spawn(_pf.barrel02, new Vector3(-11.0f,  0f, 5.75f), Quaternion.Euler(0, 30f, 0), "Barrel_Tav2");
            Spawn(_pf.bannerA,  new Vector3(-11.0f,  0f, 5.0f),  Quaternion.Euler(0, 90f, 0), "Banner_Tav");

            // Nearby trees for atmosphere
            Spawn(_pf.tree02, new Vector3(-12.0f, 0f, 6.5f), Quaternion.Euler(0, 20f, 0),  "Tree_Tav1");
            Spawn(_pf.tree02, new Vector3(-11.5f, 0f, 3.5f), Quaternion.Euler(0, 110f, 0), "Tree_Tav2");
        }

        // ── POI: Farm ─────────────────────────────────────────────────────────

        private void BuildFarm()
        {
            // Main barn building (offset halved from POI centre 2,-14)
            Spawn(_pf.building04, new Vector3(2f, 0f, -15.25f),
                Quaternion.Euler(0, 0f, 0), "Barn");

            // Crop field: flat colored patch
            var field = GameObject.CreatePrimitive(PrimitiveType.Plane);
            field.name = "CropField";
            field.transform.position   = new Vector3(0f, 0f, -13.75f);
            field.transform.localScale = new Vector3(0.75f, 1f, 0.6f);
            ApplyColor(field, new Color(0.38f, 0.52f, 0.22f));
            Destroy(field.GetComponent<Collider>());

            // Farm props
            Spawn(_pf.sack01,  new Vector3(1.25f, 0f, -13.5f),  Quaternion.Euler(0, 20f, 0),  "Sack_Farm1");
            Spawn(_pf.sack02,  new Vector3(0.75f, 0f, -13.75f), Quaternion.Euler(0, -30f, 0), "Sack_Farm2");
            Spawn(_pf.crate01, new Vector3(2.75f, 0f, -13.75f), Quaternion.Euler(0, 45f, 0),  "Crate_Farm");
            Spawn(_pf.logWood, new Vector3(3.0f,  0f, -14.5f),  Quaternion.Euler(0, 90f, 0),  "Log_Farm");

            // Fence perimeter around farm
            BuildFarmFence();
        }

        private void BuildFarmFence()
        {
            // Fence bounds halved around farm centre (2, -14):
            //   x: -0.5 to 4.5   z: -16 to -13
            for (int i = 0; i <= 4; i += 2)
                Spawn(_pf.fenceA, new Vector3(i - 0.5f, 0f, -13f), Quaternion.identity, $"Fence_N{i}");

            for (int i = 0; i <= 4; i += 2)
                Spawn(_pf.fenceA, new Vector3(i - 0.5f, 0f, -16f), Quaternion.identity, $"Fence_S{i}");

            for (int j = -16; j <= -13; j += 2)
                Spawn(_pf.fenceB, new Vector3(-0.5f, 0f, j), Quaternion.Euler(0, 90f, 0), $"Fence_W{j}");

            for (int j = -16; j <= -13; j += 2)
                Spawn(_pf.fenceB, new Vector3(4.5f, 0f, j), Quaternion.Euler(0, 90f, 0), $"Fence_E{j}");

            // Corners
            Spawn(_pf.fenceCorner, new Vector3(-0.5f, 0f, -13f), Quaternion.identity,           "FC_NW");
            Spawn(_pf.fenceCorner, new Vector3( 4.5f, 0f, -13f), Quaternion.Euler(0, 90f, 0),  "FC_NE");
            Spawn(_pf.fenceCorner, new Vector3(-0.5f, 0f, -16f), Quaternion.Euler(0, 270f, 0), "FC_SW");
            Spawn(_pf.fenceCorner, new Vector3( 4.5f, 0f, -16f), Quaternion.Euler(0, 180f, 0), "FC_SE");

            // Farm trees
            Spawn(_pf.treePine, new Vector3(-1.5f, 0f, -13.5f), Quaternion.Euler(0, 40f, 0),  "Tree_Farm1");
            Spawn(_pf.treePine, new Vector3( 5.0f, 0f, -14.5f), Quaternion.Euler(0, 130f, 0), "Tree_Farm2");
        }

        // ── POI: Gates ────────────────────────────────────────────────────────

        private void BuildNorthGate()
        {
            // Guard post buildings flanking road at North Gate (0, 14) — offsets halved
            Spawn(_pf.building05, new Vector3(-1.5f, 0f, 14f), Quaternion.Euler(0, 90f, 0),  "GatePost_NW");
            Spawn(_pf.building05, new Vector3( 1.5f, 0f, 14f), Quaternion.Euler(0, -90f, 0), "GatePost_NE");
            Spawn(_pf.bannerA,    new Vector3(-1.5f, 0f, 14.5f), Quaternion.Euler(0, 90f, 0),  "Banner_N1");
            Spawn(_pf.bannerA,    new Vector3( 1.5f, 0f, 14.5f), Quaternion.Euler(0, -90f, 0), "Banner_N2");

            // Rocks flanking entry
            Spawn(_pf.rock02, new Vector3(-1f, 0f, 13f), Quaternion.Euler(0, 20f, 0), "Rock_NG1");
            Spawn(_pf.rock02, new Vector3( 1f, 0f, 13f), Quaternion.Euler(0, 60f, 0), "Rock_NG2");
        }

        private void BuildSouthGate()
        {
            Spawn(_pf.building05, new Vector3(-1.5f, 0f, -14f), Quaternion.Euler(0, 90f, 0),  "GatePost_SW");
            Spawn(_pf.building05, new Vector3( 1.5f, 0f, -14f), Quaternion.Euler(0, -90f, 0), "GatePost_SE");
            Spawn(_pf.bannerB,    new Vector3(-1.5f, 0f, -14.5f), Quaternion.Euler(0, 90f, 0),  "Banner_S1");
            Spawn(_pf.bannerB,    new Vector3( 1.5f, 0f, -14.5f), Quaternion.Euler(0, -90f, 0), "Banner_S2");

            Spawn(_pf.rock01, new Vector3(-1f, 0f, -13f), Quaternion.Euler(0, 10f, 0), "Rock_SG1");
            Spawn(_pf.rock01, new Vector3( 1f, 0f, -13f), Quaternion.Euler(0, 75f, 0), "Rock_SG2");
        }

        // ── Tree border ───────────────────────────────────────────────────────

        private void BuildTreeBorder()
        {
            // West border
            SpawnTree(-20f,   8f);
            SpawnTree(-20f,   0f);
            SpawnTree(-19f,  -5f);
            SpawnTree(-18f, -10f);

            // East border
            SpawnTree(20f,  8f);
            SpawnTree(19f,  3f);
            SpawnTree(20f, -4f);
            SpawnTree(18f, -9f);

            // North border (around gate)
            SpawnTree(-8f,  19f);
            SpawnTree(-4f,  20f);
            SpawnTree( 4f,  20f);
            SpawnTree( 9f,  19f);
            SpawnTree(13f,  17f);

            // South border (around gate)
            SpawnTree(-9f,  -19f);
            SpawnTree(-4f,  -20f);
            SpawnTree( 4f,  -20f);
            SpawnTree(10f,  -19f);

            // Scattered bushes mid-village
            Spawn(_pf.bush01, new Vector3( 9f, 0f,  8f), Quaternion.Euler(0, 40f, 0),  "Bush1");
            Spawn(_pf.bush02, new Vector3(-6f, 0f, -3f), Quaternion.Euler(0, 120f, 0), "Bush2");
            Spawn(_pf.bush01, new Vector3( 5f, 0f, -7f), Quaternion.Euler(0, 200f, 0), "Bush3");
            Spawn(_pf.bush02, new Vector3(-8f, 0f,  9f), Quaternion.Euler(0,  70f, 0), "Bush4");
            Spawn(_pf.bush01, new Vector3(10f, 0f, -3f), Quaternion.Euler(0, 160f, 0), "Bush5");
        }

        private void SpawnTree(float x, float z)
        {
            // Alternate between three tree types for variety
            float hash = Mathf.Abs(x * 13.7f + z * 7.3f) % 3f;
            var prefab = hash < 1f ? _pf.treePine : (hash < 2f ? _pf.tree01 : _pf.tree02);
            float rot  = Mathf.Abs(x * 31f + z * 17f) % 360f;
            Spawn(prefab, new Vector3(x, 0f, z), Quaternion.Euler(0f, rot, 0f), $"Tree_{x}_{z}");
        }

        // ── Prefab spawn helpers ──────────────────────────────────────────────

        private bool Spawn(GameObject prefab, Vector3 pos, Quaternion rot, string label)
        {
            if (prefab == null) return false;
            var go   = Instantiate(prefab, pos, rot);
            go.name  = label;
            DisableColliders(go);
            return true;
        }

        private static void DisableColliders(GameObject go)
        {
            foreach (var col in go.GetComponentsInChildren<Collider>())
                col.enabled = false;
        }

        // Returns rotation facing the village centre (origin) from pos.
        private static Quaternion FaceCenter(Vector3 pos)
        {
            var dir = new Vector3(-pos.x, 0f, -pos.z);
            return dir.sqrMagnitude > 0.01f
                ? Quaternion.LookRotation(dir.normalized)
                : Quaternion.identity;
        }

        // ── Camera ────────────────────────────────────────────────────────────

        private void SetupCamera()
        {
            var cam = Camera.main;
            if (cam == null) return;

            // Slightly elevated south-west angle for a good village overview
            cam.transform.position = new Vector3(-10f, 24f, -22f);
            cam.transform.LookAt(new Vector3(2f, 0f, 2f));
            cam.fieldOfView    = 50f;
            cam.backgroundColor = new Color(0.46f, 0.64f, 0.85f);
            cam.clearFlags     = CameraClearFlags.SolidColor;
            cam.farClipPlane   = 200f;
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
                _agentById[spec.AgentId] = rec;

                var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                go.name               = spec.DisplayName;
                go.transform.position = spec.StartPosition;
                go.transform.localScale = new Vector3(0.85f, 0.85f, 0.85f);

                var mat = new Material(
                    Shader.Find("Universal Render Pipeline/Lit")
                    ?? Shader.Find("Standard"))
                { color = spec.BaseColor };
                go.GetComponent<Renderer>().material = mat;
                rec.Mat = mat;

                // Integration components
                go.AddComponent<TransformObservationProvider>();
                go.AddComponent<ObservationCollector>();

                var mover = go.AddComponent<MoveActionHandler>();
                mover.Configure(moveSpeed, arrivalThreshold: 0.8f);

                go.AddComponent<InteractActionHandler>();

                var speaker = go.AddComponent<SpeakActionHandler>();
                speaker.Configure(logToConsole: true);

                go.AddComponent<ActionExecutor>();

                var bridge = go.AddComponent<UnityAgentBridge>();
                bridge.Configure(spec.AgentId, spec.DisplayName, autoRegister: false);
                rec.Bridge = bridge;

                var idx     = i;
                var clicker = go.AddComponent<AgentClickReceiver>();
                clicker.OnClicked = () => SelectAgent(idx);

                bridge.OnDecisionReceived += d =>
                {
                    rec.LastDecision = d;
                    LogDecision($"[{spec.DisplayName}] {d.Action}: {d.OutcomeText}");
                    UpdateInspectorIfSelected(idx);

                    if (d.Action == "socialize")
                        HandleSocialize(rec, d);
                };

                bridge.OnActionStarted += action =>
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

                speaker.OnSpeak += (agentId, text) =>
                {
                    StartCoroutine(FlashColor(rec.Mat, new Color(1f, 1f, 0.2f), 2.5f, spec.BaseColor));
                    LogEvent($"[{spec.DisplayName}] \"{text}\"");
                };

                BuildNameLabel(go, spec.DisplayName, spec.Role, spec.BaseColor);
            }
        }

        // ── Socialize handling ────────────────────────────────────────────────

        private void HandleSocialize(VillageAgentRecord actor, AgentDecisionResult decision)
        {
            _totalSocializations++;

            string targetId = "";
            string message  = decision.OutcomeText ?? "";

            if (decision.Parameters != null)
            {
                if (decision.Parameters.TryGetValue("target_id", out var tid))
                    targetId = tid?.ToString() ?? "";
                if (decision.Parameters.TryGetValue("message", out var msg))
                    message = msg?.ToString() ?? message;
            }

            _agentById.TryGetValue(targetId, out var targetRec);
            string targetName = targetRec?.Spec.DisplayName ?? targetId;

            if (targetRec != null && actor.Bridge != null && targetRec.Bridge != null)
            {
                var dir = targetRec.Bridge.transform.position - actor.Bridge.transform.position;
                dir.y = 0;
                if (dir.sqrMagnitude > 0.001f)
                    actor.Bridge.transform.rotation = Quaternion.LookRotation(dir);
            }

            actor.SocialCount++;
            actor.LastSocialWith = targetName;

            StartCoroutine(FlashColor(actor.Mat, new Color(1f, 0.6f, 0.85f), 3f, actor.Spec.BaseColor));

            string truncMsg = message.Length > 40 ? message[..37] + "…" : message;
            LogEvent($"[{actor.Spec.DisplayName}→{targetName}] \"{truncMsg}\"");

            UpdateInspectorIfSelected(Array.FindIndex(_agents, r => r == actor));
        }

        // ── Billboard + name labels ───────────────────────────────────────────

        internal class BillboardLabel : MonoBehaviour
        {
            private Camera _cam;
            private void Start()      { _cam = Camera.main; }
            private void LateUpdate()
            {
                if (_cam == null) return;
                transform.forward = _cam.transform.forward;
            }
        }

        private static void BuildNameLabel(GameObject npc, string displayName, string role, Color color)
        {
            var go = new GameObject("NameLabel");
            go.transform.SetParent(npc.transform, worldPositionStays: false);
            go.transform.localPosition = new Vector3(0f, 1.6f, 0f);
            go.transform.localScale    = Vector3.one * 0.01f;

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode  = RenderMode.WorldSpace;
            canvas.worldCamera = Camera.main;
            go.GetComponent<RectTransform>().sizeDelta = new Vector2(220f, 70f);
            go.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 1;
            go.AddComponent<BillboardLabel>();

            var nameGO = new GameObject("Name");
            nameGO.transform.SetParent(go.transform, false);
            var nameRt = nameGO.AddComponent<RectTransform>();
            nameRt.anchorMin = new Vector2(0, 0.5f);
            nameRt.anchorMax = Vector2.one;
            nameRt.offsetMin = Vector2.zero;
            nameRt.offsetMax = Vector2.zero;
            var nameT = nameGO.AddComponent<Text>();
            nameT.text      = displayName;
            nameT.color     = color;
            nameT.font      = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                           ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            nameT.fontSize  = 28;
            nameT.alignment = TextAnchor.UpperCenter;

            var roleGO = new GameObject("Role");
            roleGO.transform.SetParent(go.transform, false);
            var roleRt = roleGO.AddComponent<RectTransform>();
            roleRt.anchorMin = Vector2.zero;
            roleRt.anchorMax = new Vector2(1, 0.5f);
            roleRt.offsetMin = Vector2.zero;
            roleRt.offsetMax = Vector2.zero;
            var roleT = roleGO.AddComponent<Text>();
            roleT.text      = role;
            roleT.color     = new Color(color.r * 0.7f, color.g * 0.7f, color.b * 0.7f, 0.85f);
            roleT.font      = nameT.font;
            roleT.fontSize  = 20;
            roleT.alignment = TextAnchor.LowerCenter;
        }

        // ── Simulation manager ────────────────────────────────────────────────

        private void CreateSimManager()
        {
            var go = new GameObject("SimulationManager");

            _simMgr = go.AddComponent<UnitySimulationManager>();
            _simMgr.Configure(host, port, tickRate: 0.001f, autoConnect: false);

            _simMgr.OnConnected       += HandleConnected;
            _simMgr.OnDisconnected    += HandleDisconnected;
            _simMgr.OnTickComplete    += HandleTickComplete;
            _simMgr.OnTickError       += ex => LogEvent($"[tick error] {ex?.Message}");
            _simMgr.OnSimulationEvent += HandleSimEvent;

            var vizGO = new GameObject("EventVisualizer");
            var viz   = vizGO.AddComponent<EventVisualizer>();
            viz.Configure(showOverlay: false);
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
            SetStatus("Disconnected — press Connect to start");
            _connectBtn?.gameObject.SetActive(true);
            _disconnectBtn?.gameObject.SetActive(false);
            _startTickBtn?.gameObject.SetActive(false);
            _stopTickBtn?.gameObject.SetActive(false);
            _autoTicking = false;
            _paused      = false;
            foreach (var rec in _agents)
            {
                rec.IsMoving       = false;
                rec.Mat.color      = rec.Spec.BaseColor;
                rec.LastDecision   = null;
                rec.LastSocialWith = null;
            }
            LogEvent("[system] Disconnected");
            UpdateInspector();
            UpdateAgentStatusText();
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
            if (ev.EventType == "action_completed") return;
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
                    sb.AppendLine($"Outcome: {Truncate(d.OutcomeText, 44)}");
                if (d.Parameters?.Count > 0)
                {
                    var ps = new List<string>();
                    foreach (var kv in d.Parameters)
                        ps.Add($"{kv.Key}={kv.Value}");
                    sb.AppendLine($"Params:  {Truncate(string.Join(", ", ps), 44)}");
                }
            }
            else
            {
                sb.AppendLine("(no decisions yet)");
            }

            sb.AppendLine();
            if (rec.SocialCount > 0)
            {
                sb.AppendLine($"Socializations: {rec.SocialCount}");
                if (!string.IsNullOrEmpty(rec.LastSocialWith))
                    sb.AppendLine($"Last social: {rec.LastSocialWith}");
                sb.AppendLine();
            }

            sb.AppendLine($"[{_selectedIdx + 1}/{_agents.Length}]  click agent or use Prev / Next");
            _inspectorText.text = sb.ToString();
        }

        // ── Metrics ───────────────────────────────────────────────────────────

        private void UpdateMetricsText()
        {
            if (_metricsText == null) return;
            int moving = 0, socializing = 0;
            if (_agents != null)
                foreach (var a in _agents)
                {
                    if (a.IsMoving) moving++;
                    if (a.LastDecision?.Action == "socialize") socializing++;
                }
            _metricsText.text =
                $"Agents:       {_agents?.Length ?? 0}  ({moving} moving)\n" +
                $"Tick:         {_totalTicks}\n" +
                $"Duration:     {(_totalTicks > 0 ? $"{_lastTickMs:F0} ms" : "-")}\n" +
                $"FPS:          {1f / Time.smoothDeltaTime:F0}\n" +
                $"Decisions:    {_totalDecisions}\n" +
                $"Events:       {_totalEvents}\n" +
                $"Socializations: {_totalSocializations}";
        }

        private void SetStatus(string msg) { if (_statusText != null) _statusText.text = msg; }

        // ── Log management ────────────────────────────────────────────────────

        private void LogEvent(string line)
        {
            _eventLog.Enqueue($"[{DateTime.Now:HH:mm:ss}] {line}");
            while (_eventLog.Count > MaxLogLines) _eventLog.Dequeue();
            if (_eventLogText != null)
                _eventLogText.text = string.Join("\n", _eventLog);
        }

        private void LogDecision(string _)
        {
            UpdateAgentStatusText();
        }

        private void UpdateAgentStatusText()
        {
            if (_agentStatusText == null || _agents == null) return;
            var sb = new System.Text.StringBuilder();
            foreach (var rec in _agents)
            {
                var d = rec.LastDecision;
                string action  = d != null ? d.Action  : "waiting";
                string outcome = d != null ? Truncate(d.OutcomeText ?? "", 80) : "";
                sb.AppendLine($"<color=#{ColorToHex(rec.Spec.BaseColor)}>{rec.Spec.DisplayName}</color> [{action}] {outcome}");
            }
            _agentStatusText.text = sb.ToString();
        }

        private static string ColorToHex(Color c)
        {
            return $"{ToByte(c.r):X2}{ToByte(c.g):X2}{ToByte(c.b):X2}";
        }

        private static int ToByte(float f) => Mathf.Clamp(Mathf.RoundToInt(f * 255f), 0, 255);

        // ── Coroutines ────────────────────────────────────────────────────────

        private IEnumerator FlashColor(Material mat, Color flash, float duration, Color restore)
        {
            if (mat == null) yield break;
            mat.color = flash;
            yield return new WaitForSeconds(duration);
            if (mat != null) mat.color = restore;
        }

        private IEnumerator ReconnectAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            _simMgr.Connect();
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

            // Status bar (top)
            var statusBg = MakePanel(root,
                new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(0, -32), new Vector2(0, 0));
            SetPanelColor(statusBg, new Color(0.04f, 0.06f, 0.10f, 0.92f));
            _statusText = MakeLbl(statusBg, Vector2.zero, Vector2.one,
                new Vector2(12, 2), new Vector2(-12, -2),
                $"Press Connect — backend: biomata-ws --config examples/village/sim.yaml --port {port}",
                12, TextAnchor.MiddleLeft);

            // ── Left panel (controls + metrics) ──────────────────────────────

            var leftBg = MakePanel(root,
                new Vector2(0, 1), new Vector2(0, 1),
                new Vector2(0, -450), new Vector2(252, -32));
            SetPanelColor(leftBg, new Color(0.04f, 0.05f, 0.08f, 0.90f));

            MakeLbl(leftBg, new Vector2(0,1), new Vector2(1,1),
                new Vector2(10,-6), new Vector2(-10,-24),
                "BIOMATA  SOCIAL  VILLAGE", 13, TextAnchor.UpperCenter);

            MakeLbl(leftBg, new Vector2(0,1), new Vector2(1,1),
                new Vector2(10,-28), new Vector2(-10,-42), "CONNECTION", 10, TextAnchor.UpperLeft);

            _connectBtn = MakeBtn(leftBg, "Connect",
                new Vector2(10, -46), new Vector2(112, 24),
                () => { SetStatus($"Connecting to {host}:{port}..."); _simMgr.Connect(); });
            _disconnectBtn = MakeBtn(leftBg, "Disconnect",
                new Vector2(130, -46), new Vector2(112, 24),
                () => _simMgr.Disconnect());
            _disconnectBtn.gameObject.SetActive(false);

            MakeLbl(leftBg, new Vector2(0,1), new Vector2(1,1),
                new Vector2(10,-76), new Vector2(-10,-90), "SIMULATION", 10, TextAnchor.UpperLeft);

            _startTickBtn = MakeBtn(leftBg, "Start Auto", new Vector2(10,-94), new Vector2(108,24), () =>
            {
                _autoTicking = true; _paused = false; _tickAccum = 0f;
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

            MakeBtn(leftBg, "Force Tick", new Vector2(126,-94), new Vector2(116,24), () =>
            {
                if (_simMgr.IsConnected && !_paused) DispatchTick();
            });

            MakeBtn(leftBg, "Pause",  new Vector2(10, -124), new Vector2(72, 24), () =>
            {
                _paused = true;
                LogEvent("[system] Paused");
            });
            MakeBtn(leftBg, "Resume", new Vector2(86, -124), new Vector2(72, 24), () =>
            {
                _paused = false;
                LogEvent("[system] Resumed");
            });
            MakeBtn(leftBg, "Reset",  new Vector2(162,-124), new Vector2(80, 24), () =>
            {
                _autoTicking = false; _paused = false;
                _totalTicks = 0; _totalDecisions = 0; _totalEvents = 0; _totalSocializations = 0;
                _eventLog.Clear();
                LogEvent("[system] Reconnecting...");
                _simMgr.Disconnect();
                StartCoroutine(ReconnectAfterDelay(1.2f));
            });

            MakeLbl(leftBg, new Vector2(0,1), new Vector2(1,1),
                new Vector2(10,-158), new Vector2(-10,-172), "METRICS", 10, TextAnchor.UpperLeft);

            _metricsText = MakeLbl(leftBg, new Vector2(0,1), new Vector2(1,1),
                new Vector2(10,-174), new Vector2(-10,-370), "Connecting...", 11, TextAnchor.UpperLeft);

            // ── Right panel (agent inspector) ─────────────────────────────────

            var rightBg = MakePanel(root,
                new Vector2(1, 1), new Vector2(1, 1),
                new Vector2(-278, -380), new Vector2(0, -32));
            SetPanelColor(rightBg, new Color(0.04f, 0.05f, 0.08f, 0.90f));

            MakeLbl(rightBg, new Vector2(0,1), new Vector2(1,1),
                new Vector2(10,-6), new Vector2(-10,-22),
                "AGENT  INSPECTOR", 11, TextAnchor.UpperCenter);

            _inspectorText = MakeLbl(rightBg, new Vector2(0,1), new Vector2(1,1),
                new Vector2(10,-26), new Vector2(-10,-280),
                "Click an agent or use Prev / Next", 11, TextAnchor.UpperLeft);
            _inspectorText.horizontalOverflow = HorizontalWrapMode.Wrap;

            MakeBtn(rightBg, "< Prev", new Vector2(10,-282), new Vector2(124,24), () =>
            {
                _selectedIdx = (_selectedIdx - 1 + _agents.Length) % _agents.Length;
                UpdateInspector();
            });
            MakeBtn(rightBg, "Next >", new Vector2(142,-282), new Vector2(126,24), () =>
            {
                _selectedIdx = (_selectedIdx + 1) % _agents.Length;
                UpdateInspector();
            });

            // ── Bottom panel (logs + agent status) ───────────────────────────

            var botBg = MakePanel(root,
                new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(0, 0), new Vector2(0, 260));
            SetPanelColor(botBg, new Color(0.03f, 0.04f, 0.07f, 0.92f));

            // Event log: left 30% of bottom panel (left control panel covers 0–252px, so
            // visible region is roughly 252–576px — narrow but readable for timestamped lines).
            MakeLbl(botBg, new Vector2(0,1), new Vector2(0.2f,1),
                new Vector2(10,-4), new Vector2(-4,-20), "SOCIAL + EVENT LOG", 10, TextAnchor.UpperLeft);
            _eventLogText = MakeLbl(
                botBg,
                new Vector2(0,1),
                new Vector2(0,1),
                new Vector2(10,-22),
                new Vector2(380,-8),
                "(waiting for connection)", 9, TextAnchor.LowerLeft);
            _eventLogText.verticalOverflow = VerticalWrapMode.Truncate;

            // Agent decisions: right 70% of bottom panel, stopping 282px from the right edge
            // so text doesn't render underneath the right inspector panel (278px wide).
            MakeLbl(
                botBg,
                new Vector2(0,1),
                new Vector2(1,1),
                new Vector2(420,-4),
                new Vector2(-282,-20),
                "AGENT DECISIONS",
                10,
                TextAnchor.UpperLeft);
            _agentStatusText = MakeLbl(
                botBg,
                new Vector2(0,1),
                new Vector2(1,1),
                new Vector2(400,-22),
                new Vector2(-282,-8),
                "(waiting for decisions)", 9, TextAnchor.UpperLeft);
            _agentStatusText.verticalOverflow   = VerticalWrapMode.Overflow;
            _agentStatusText.horizontalOverflow = HorizontalWrapMode.Wrap;
            UpdateAgentStatusText();

            UpdateInspector();
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
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(0, 1);
            rt.pivot     = new Vector2(0, 1);
            rt.sizeDelta = size;
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

        private static void ApplyColor(GameObject go, Color color)
        {
            var r = go.GetComponent<Renderer>();
            if (r == null) return;
            var shader = Shader.Find("Universal Render Pipeline/Lit")
                      ?? Shader.Find("Standard");
            var mat    = new Material(shader) { color = color };
            r.material = mat;
        }

        private static void EnsureEventSystem()
        {
            if (FindFirstObjectByType<EventSystem>() != null) return;
            var es = new GameObject("EventSystem");
            es.AddComponent<EventSystem>();
            es.AddComponent<StandaloneInputModule>();
        }

        private static string Truncate(string s, int max) =>
            s == null ? "" : (s.Length <= max ? s : s[..max] + "…");
    }
}
