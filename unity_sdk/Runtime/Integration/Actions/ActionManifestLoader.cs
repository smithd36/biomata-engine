using System;
using System.Collections.Generic;
using UnityEngine;

namespace Biomata.Integration.Actions
{
    // ── Wire types (match BiomataActions.json shape) ──────────────────────────

    [Serializable]
    public class ManifestActionEntry
    {
        public string   name;
        public string   description;
        public string   kind;
        public string[] required_capabilities;
        // 'parameters' is omitted — it is metadata for humans and Python validation only.
    }

    [Serializable]
    public class ManifestData
    {
        public string               version;
        public ManifestActionEntry[] actions;
    }

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
    /// </summary>
    public static class ActionManifestLoader
    {
        private const string ResourceName = "BiomataActions";

        private static ManifestData _cache;

        /// <summary>
        /// Load the manifest from Resources. Returns null if not found.
        /// Result is cached after first call.
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

        /// <summary>Reset the in-memory cache. Useful in play-mode tests.</summary>
        public static void ClearCache() => _cache = null;
    }
}
