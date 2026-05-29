#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using Biomata.Integration.Actions;
using UnityEditor;
using UnityEngine;

namespace Biomata.Editor
{
    /// <summary>
    /// Editor validator for the Biomata action manifest.
    ///
    /// Reads BiomataActions.json from Resources and checks that every declared action
    /// has at least one <see cref="ActionHandlerBase"/> subclass in the project whose
    /// <see cref="ActionHandlerBase.DeclaredActionNames"/> (or conventional static
    /// <c>HandledActions</c> field) includes that action name.
    ///
    /// Run from the menu: <b>Biomata &gt; Validate Action Manifest</b>
    ///
    /// ── How coverage is determined ────────────────────────────────────────────
    ///
    /// For each non-abstract <see cref="ActionHandlerBase"/> subclass found in the project:
    ///   1. Checks for a <c>static HandledActions</c> field (HashSet&lt;string&gt;) — the
    ///      convention used by all built-in handlers (Move, Speak, Interact, Idle).
    ///
    /// Custom handlers that use a different naming convention will show as uncovered.
    /// Override <see cref="ActionHandlerBase.DeclaredActionNames"/> in your custom handler
    /// to guarantee detection.
    /// </summary>
    public static class ActionManifestValidator
    {
        [MenuItem("Biomata/Validate Action Manifest")]
        public static void Validate()
        {
            ActionManifestLoader.ClearCache();
            var manifest = ActionManifestLoader.Load();

            if (manifest == null)
            {
                EditorUtility.DisplayDialog(
                    "Biomata — Manifest Not Found",
                    "BiomataActions.json was not found in any Resources folder.\n\n" +
                    "Generate it from simulation/actions.yaml:\n\n" +
                    "  python -c \"\n" +
                    "    from src.config.manifest import ActionManifest\n" +
                    "    ActionManifest.load('simulation/actions.yaml')\\\n" +
                    "      .export_json('Assets/Resources/BiomataActions.json')\n" +
                    "  \"",
                    "OK");
                return;
            }

            var coveredByType = CollectCoveredActions();
            var warnings      = new List<string>();
            var ok            = new List<string>();

            foreach (var action in manifest.actions)
            {
                if (coveredByType.TryGetValue(action.name.ToLowerInvariant(), out var handlerType))
                    ok.Add($"  ✓  {action.name,-24} handled by {handlerType.Name}");
                else
                    warnings.Add($"  ✗  {action.name,-24} no handler found");
            }

            var summary = $"Manifest v{manifest.version} — " +
                          $"{ok.Count} covered, {warnings.Count} uncovered";

            if (warnings.Count == 0)
            {
                Debug.Log($"[Biomata] {summary}\n{string.Join("\n", ok)}");
            }
            else
            {
                foreach (var w in warnings)
                    Debug.LogWarning($"[Biomata] Uncovered action: {w.Trim()}");
                Debug.LogWarning(
                    $"[Biomata] {summary}\n\n" +
                    "To fix: add ActionHandlerBase components to your NPC prefabs, or " +
                    "implement DeclaredActionNames in a custom handler.\n\n" +
                    string.Join("\n", ok) + "\n" +
                    string.Join("\n", warnings));
            }

            EditorUtility.DisplayDialog(
                "Biomata — Manifest Validation",
                warnings.Count == 0
                    ? $"{summary}\n\nAll actions have Unity handlers."
                    : $"{summary}\n\nSee Console for details.",
                "OK");
        }

        // ── Internal ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Map every covered action name → the first handler type that covers it.
        /// </summary>
        private static Dictionary<string, Type> CollectCoveredActions()
        {
            var result = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

            foreach (var type in TypeCache.GetTypesDerivedFrom<ActionHandlerBase>())
            {
                if (type.IsAbstract) continue;

                foreach (var name in GetDeclaredNames(type))
                {
                    var key = name.ToLowerInvariant();
                    if (!result.ContainsKey(key))
                        result[key] = type;
                }
            }

            return result;
        }

        /// <summary>
        /// Extract action name declarations from a handler type without instantiation.
        /// Reflects on the conventional <c>static HandledActions</c> field.
        /// </summary>
        private static IEnumerable<string> GetDeclaredNames(Type type)
        {
            var field = type.GetField(
                "HandledActions",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            if (field?.GetValue(null) is IEnumerable<string> names)
                return names;

            return Array.Empty<string>();
        }
    }
}
#endif
