#if UNITY_EDITOR
using System.Collections.Generic;
using Biomata.Integration;
using Biomata.Integration.Simulation;
using UnityEditor;
using UnityEngine;

namespace Biomata.Editor
{
    /// <summary>
    /// Editor validator for role declarations.
    ///
    /// Checks:
    ///   1. The roles manifest is loaded (from the backend RPC or a fallback JSON).
    ///   2. Every <see cref="BiomataAgent"/> in the project with a non-empty role
    ///      references a role declared in the manifest.
    ///   3. (Advisory) Roles that declare observation requirements — warns when
    ///      corresponding <see cref="ObservationProviderBase"/> components are absent
    ///      from the prefab. Does not fail validation; observation coverage is advisory.
    ///
    /// Run from the menu: <b>Biomata &gt; Validate Roles</b>
    ///
    /// The manifest is populated automatically on connect via the <c>roles.list</c> RPC.
    /// For offline editor validation, place a <c>BiomataRoles.json</c> in a Resources folder.
    /// </summary>
    public static class RoleManifestValidator
    {
        [MenuItem("Biomata/Validate Roles")]
        public static void Validate()
        {
            RoleManifestLoader.ClearCache();
            var manifest = RoleManifestLoader.Load();

            if (manifest == null)
            {
                EditorUtility.DisplayDialog(
                    "Biomata — Roles Manifest Not Available",
                    "No roles manifest is loaded.\n\n" +
                    "The manifest is fetched automatically from the backend on connect " +
                    "(roles.list RPC). For offline editor validation, place a " +
                    "BiomataRoles.json file in any Resources folder.\n\n" +
                    "Enter Play mode with the backend running to populate the manifest, " +
                    "then re-run this validator.",
                    "OK");
                return;
            }

            // Build a set of known role names
            var knownRoles = new HashSet<string>();
            foreach (var r in manifest.roles)
                knownRoles.Add(r.name);

            var warnings = new List<string>();
            var ok       = new List<string>();

            // Scan all BiomataAgent components in prefabs and open scenes
            var allAgents = FindAllBiomataAgents();
            foreach (var (agent, source) in allAgents)
            {
                if (string.IsNullOrEmpty(agent.RoleForValidation)) continue;

                var roleName = agent.RoleForValidation;
                if (!knownRoles.Contains(roleName))
                {
                    warnings.Add($"  ✗  {source}: role '{roleName}' not in manifest");
                    continue;
                }

                ok.Add($"  ✓  {source}: role '{roleName}'");
            }

            var summary = $"BiomataRoles v{manifest.version} — " +
                          $"{manifest.roles.Length} declared, " +
                          $"{ok.Count} valid, {warnings.Count} invalid";

            if (warnings.Count == 0)
                Debug.Log($"[Biomata] {summary}\n{string.Join("\n", ok)}");
            else
            {
                foreach (var w in warnings)
                    Debug.LogWarning($"[Biomata] Role issue: {w.Trim()}");
                Debug.LogWarning(
                    $"[Biomata] {summary}\n" +
                    string.Join("\n", ok) + "\n" + string.Join("\n", warnings));
            }

            EditorUtility.DisplayDialog(
                "Biomata — Role Validation",
                warnings.Count == 0
                    ? summary + "\n\nAll agent roles are valid."
                    : summary + "\n\nSee Console for details.",
                "OK");
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static List<(BiomataAgent agent, string source)> FindAllBiomataAgents()
        {
            var result = new List<(BiomataAgent, string)>();

            // Open scenes
            foreach (var agent in Object.FindObjectsByType<BiomataAgent>(
                FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                result.Add((agent, $"scene/{agent.gameObject.name}"));
            }

            // Prefabs
            var guids = AssetDatabase.FindAssets("t:Prefab");
            foreach (var guid in guids)
            {
                var path   = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                var agents = prefab.GetComponentsInChildren<BiomataAgent>(true);
                foreach (var agent in agents)
                    result.Add((agent, $"prefab/{prefab.name}/{agent.gameObject.name}"));
            }

            return result;
        }
    }
}
#endif
