using System;
using System.Linq;
using Biomata.SDK.Models;
using UnityEngine;

namespace Biomata.Integration.Simulation
{
    /// <summary>
    /// Provides role definitions to the Unity integration layer.
    ///
    /// Primary source: the <c>roles.list</c> WebSocket RPC, which the
    /// <see cref="UnitySimulationManager"/> calls on connect and feeds here via
    /// <see cref="Populate"/>. No static JSON file is required.
    ///
    /// Fallback: if a <c>BiomataRoles.json</c> asset exists in a Resources folder it
    /// is loaded as a development convenience (e.g. when running in the editor without
    /// a live backend). The RPC result always takes precedence over the JSON.
    /// </summary>
    public static class RoleManifestLoader
    {
        private const string ResourceName = "BiomataRoles";
        private static RolesData _cache;
        private static bool      _fromRpc;   // true when populated via Populate()

        /// <summary>
        /// Seed the manifest from the <c>roles.list</c> RPC response.
        /// Called by <see cref="UnitySimulationManager"/> immediately after connect.
        /// Supersedes any previously loaded JSON fallback.
        /// </summary>
        public static void Populate(RolesData data)
        {
            _cache  = data;
            _fromRpc = true;
        }

        /// <summary>
        /// Load roles. Returns the RPC-populated data when available; falls back to
        /// Resources/<c>BiomataRoles.json</c> for editor / offline use.
        /// Returns null if neither source is available.
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
        public static void ClearCache()
        {
            _cache   = null;
            _fromRpc = false;
        }
    }
}
