using System;
using System.Linq;
using UnityEngine;

namespace Biomata.Integration.Simulation
{
    // ── Wire types (match BiomataRoles.json shape) ────────────────────────────

    [Serializable]
    public class RoleEntry
    {
        public string   name;
        public string[] capabilities;
        public string[] observations;
        public string   brain_provider; // Python-side provider shorthand; advisory for Unity
        public string   brain_class;    // Fully-qualified Python class path, if any
    }

    [Serializable]
    public class RolesData
    {
        public string     version;
        public RoleEntry[] roles;
    }

    /// <summary>
    /// Loads BiomataRoles.json from a Unity Resources folder.
    ///
    /// The JSON is generated from the <c>roles:</c> block in sim.yaml:
    ///   python -c "
    ///     from src.config.schema import SimConfig
    ///     from src.config.roles import export_roles_json
    ///     import yaml
    ///     cfg = SimConfig.model_validate(yaml.safe_load(open('sim.yaml')))
    ///     export_roles_json(cfg.roles, 'Assets/Resources/BiomataRoles.json')
    ///   "
    ///
    /// Re-run whenever you add, remove, or rename a role in sim.yaml.
    /// Commit the generated file alongside your Unity project.
    /// </summary>
    public static class RoleManifestLoader
    {
        private const string ResourceName = "BiomataRoles";
        private static RolesData _cache;

        /// <summary>
        /// Load roles from Resources. Returns null if the file is not found.
        /// Result is cached after first call; call <see cref="ClearCache"/> to force reload.
        /// </summary>
        public static RolesData Load()
        {
            if (_cache != null) return _cache;

            var asset = Resources.Load<TextAsset>(ResourceName);
            if (asset == null) return null;

            try
            {
                _cache = JsonUtility.FromJson<RolesData>(asset.text);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Biomata] Failed to parse BiomataRoles.json: {ex.Message}");
                return null;
            }
            return _cache;
        }

        /// <summary>
        /// Find a role entry by name. Returns null if not found or manifest missing.
        /// Case-sensitive match.
        /// </summary>
        public static RoleEntry FindRole(string roleName)
        {
            var data = Load();
            if (data?.roles == null) return null;
            return Array.Find(data.roles, r => r.name == roleName);
        }

        /// <summary>True if the manifest has been loaded successfully.</summary>
        public static bool IsLoaded => Load() != null;

        /// <summary>All declared role names, or an empty array if not loaded.</summary>
        public static string[] RoleNames()
        {
            var data = Load();
            if (data?.roles == null) return Array.Empty<string>();
            return data.roles.Select(r => r.name).ToArray();
        }

        /// <summary>Reset the in-memory cache. Useful in play-mode tests or after re-import.</summary>
        public static void ClearCache() => _cache = null;
    }
}
