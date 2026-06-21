using System;
using System.Collections.Generic;
using Biomata.SDK.Models;
using UnityEngine;

namespace Biomata.Integration.Actions
{
    /// <summary>
    /// Loads BiomataActions.json from a Unity Resources folder at runtime.
    ///
    /// The JSON is generated from simulation/actions.yaml:
    ///   python -c "
    ///     from src.config.manifest import ActionManifest
    ///     ActionManifest.load('simulation/actions.yaml') \
    ///       .export_json('Assets/Resources/BiomataActions.json')
    ///   "
    ///
    /// Commit the generated file. Unity reads it via Resources.Load at runtime
    /// and the editor validator reads it via Biomata > Validate Action Manifest.
    ///
    /// Data types for the manifest are in <see cref="Biomata.SDK.Models"/>:
    ///   <see cref="ManifestData"/> and <see cref="ManifestActionEntry"/>.
    /// </summary>
    public static class ActionManifestLoader
    {
        private const string ResourceName = "BiomataActions";

        private static ManifestData _cache;
        private static bool         _fromRpc;   // true when populated via Populate()

        /// <summary>
        /// Seed the manifest from the <c>actions.list</c> RPC — the backend's live action
        /// space. Called by <see cref="Biomata.Integration.Simulation.UnitySimulationManager"/>
        /// right after connect so validation runs against what the backend actually loaded,
        /// not a committed JSON sidecar that may have drifted from the current sim.yaml.
        /// Supersedes any Resources fallback.
        /// </summary>
        public static void Populate(ManifestData data)
        {
            _cache   = data;
            _fromRpc = true;
        }

        /// <summary>True when the cache came from the backend RPC rather than Resources.</summary>
        public static bool IsFromBackend => _fromRpc;

        /// <summary>
        /// Load the manifest. Returns the RPC-populated data when available; otherwise
        /// falls back to Resources/BiomataActions.json (editor / offline). Cached after
        /// first call.
        /// </summary>
        public static ManifestData Load()
        {
            if (_cache != null) return _cache;

            var asset = Resources.Load<TextAsset>(ResourceName);
            if (asset == null)
            {
                Debug.LogWarning(
                    "[Biomata] BiomataActions.json not found in any Resources folder.\n" +
                    "Generate it from simulation/actions.yaml:\n" +
                    "  python -c \"from src.config.manifest import ActionManifest; " +
                    "ActionManifest.load('simulation/actions.yaml')" +
                    ".export_json('Assets/Resources/BiomataActions.json')\"");
                return null;
            }

            try
            {
                _cache = JsonUtility.FromJson<ManifestData>(asset.text);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Biomata] Failed to parse BiomataActions.json: {ex.Message}");
                return null;
            }

            return _cache;
        }

        /// <summary>
        /// Check whether the ActionHandlerBase components on <paramref name="executor"/>'s
        /// GameObject cover all actions declared in the manifest.
        ///
        /// Logs a warning for each uncovered action. Call from Awake or Start on agents
        /// that use manifest-validated action sets.
        /// </summary>
        public static void ValidateCoverage(ActionExecutor executor)
        {
            var manifest = Load();
            if (manifest?.actions == null) return;

            var handlers = executor.GetComponents<ActionHandlerBase>();
            var covered  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in handlers)
                foreach (var n in h.DeclaredActionNames)
                    covered.Add(n);

            foreach (var action in manifest.actions)
            {
                if (!covered.Contains(action.name))
                    Debug.LogWarning(
                        $"[Biomata] '{executor.gameObject.name}': no handler covers manifest " +
                        $"action '{action.name}'. Add an ActionHandlerBase component or override " +
                        $"DeclaredActionNames in a custom handler.");
            }
        }

        /// <summary>
        /// Connect-time negotiation: warn about any backend action that no
        /// <see cref="ActionHandlerBase"/> anywhere in the scene can execute. Catches the
        /// "added an action to sim.yaml but forgot the Unity handler" drift at connect time
        /// instead of via an agent stuck on an unhandled decision.
        ///
        /// Call after <see cref="Populate"/>. Does nothing if the manifest is unavailable.
        /// </summary>
        public static void ValidateScene()
        {
            var manifest = Load();
            if (manifest?.actions == null || manifest.actions.Length == 0) return;

            var executors = UnityEngine.Object.FindObjectsByType<ActionExecutor>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ex in executors)
                foreach (var h in ex.GetComponents<ActionHandlerBase>())
                    foreach (var n in h.DeclaredActionNames)
                        covered.Add(n);

            var source = _fromRpc ? "backend" : "BiomataActions.json";
            foreach (var action in manifest.actions)
            {
                if (!covered.Contains(action.name))
                    Debug.LogWarning(
                        $"[Biomata] {source} declares action '{action.name}' but no handler in the " +
                        "scene covers it. Add an ActionHandlerBase component (or override " +
                        "DeclaredActionNames) on the agents expected to perform it.");
            }
        }

        /// <summary>Reset the in-memory cache. Useful in play-mode tests.</summary>
        public static void ClearCache()
        {
            _cache   = null;
            _fromRpc = false;
        }
    }
}

