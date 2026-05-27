// Biomata SDK — NPC Status Display (Production Integration Sample)
//
// Optional per-NPC component that drives visible material feedback from
// BiomataAgent action events.  Copy and modify this as a starting point for
// your own effects: animation triggers, audio cues, particle bursts, UI bars.
//
// Place alongside BiomataAgent on any NPC GameObject.  No code changes needed —
// configure colours and log behaviour from the Inspector.

using Biomata.Integration;
using UnityEngine;

namespace Biomata.Samples
{
    [AddComponentMenu("Biomata/Samples/NPC Status Display")]
    [RequireComponent(typeof(BiomataAgent))]
    public class NpcStatusDisplay : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("Logging")]
        [Tooltip("Log each received decision to the Unity Console.")]
        [SerializeField] private bool logDecisions = true;

        [Tooltip("Log each started / completed action to the Unity Console.")]
        [SerializeField] private bool logActions = false;

        [Header("Material Feedback")]
        [Tooltip(
            "Tint the NPC's material while the navigate action is running. " +
            "Set alpha to 0 to disable.")]
        [SerializeField] private Color movingColor    = new Color(0.40f, 0.80f, 1.00f);

        [Tooltip("Tint applied during the speak action.")]
        [SerializeField] private Color speakingColor  = new Color(1.00f, 1.00f, 0.30f);

        [Tooltip("Tint applied during the interact action.")]
        [SerializeField] private Color interactColor  = new Color(0.50f, 1.00f, 0.50f);

        // ── Private state ─────────────────────────────────────────────────────

        private BiomataAgent _agent;
        private Material     _mat;       // per-instance material; destroyed in OnDestroy
        private Color        _baseColor;

        // ── Unity lifecycle ───────────────────────────────────────────────────

        private void Awake()
        {
            _agent = GetComponent<BiomataAgent>();

            // Grab the first renderer found on this GO or its children.
            var r = GetComponentInChildren<Renderer>();
            if (r != null)
            {
                _mat       = r.material; // material property creates a per-instance copy
                _baseColor = _mat.color;
            }
        }

        private void Start()
        {
            _agent.OnDecisionReceived += d =>
            {
                if (logDecisions)
                    Debug.Log($"[{_agent.DisplayName}] decision: {d.Action}  "{d.OutcomeText}"");
            };

            _agent.OnActionStarted += action =>
            {
                if (logActions)
                    Debug.Log($"[{_agent.DisplayName}] action started: {action}");

                if (_mat == null) return;
                _mat.color = action switch
                {
                    "navigate" => movingColor,
                    "speak"    => speakingColor,
                    "interact" => interactColor,
                    _          => _baseColor,
                };
            };

            _agent.OnActionCompleted += action =>
            {
                if (logActions)
                    Debug.Log($"[{_agent.DisplayName}] action completed: {action}");

                if (_mat != null) _mat.color = _baseColor;
            };
        }

        private void OnDestroy()
        {
            // Release the per-instance material created by the material property accessor.
            if (_mat != null) Destroy(_mat);
        }
    }
}
