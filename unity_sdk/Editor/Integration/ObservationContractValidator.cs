#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using Biomata.Integration;
using Biomata.Integration.Observations;
using UnityEditor;
using UnityEngine;

namespace Biomata.Editor
{
    /// <summary>
    /// Editor validator for the observation contract (see <see cref="ObservationKeys"/> and
    /// <c>docs/observation_contract.md</c>).
    ///
    /// For every agent in the open scene(s) it gathers the sibling
    /// <see cref="ObservationProviderBase"/> components, reads each provider's
    /// <see cref="ObservationProviderBase.DeclaredObservationKeys"/>, and:
    ///   • logs the full declared key set per agent (discoverability — what the brain receives);
    ///   • warns when two providers on the same agent declare the same key (a silent overwrite).
    ///
    /// Providers with dynamic/configurable keys (POIs, nearby objects, needs) declare nothing
    /// and are skipped — this checks only the fixed engine-contract keys where both ends must
    /// agree exactly. Run from <b>Biomata &gt; Validate Observation Contract</b>.
    /// </summary>
    public static class ObservationContractValidator
    {
        [MenuItem("Biomata/Validate Observation Contract")]
        public static void Validate()
        {
            var collectors = Object.FindObjectsByType<ObservationCollector>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            if (collectors.Length == 0)
            {
                EditorUtility.DisplayDialog(
                    "Biomata — Observation Contract",
                    "No agents with an ObservationCollector found in the open scene(s).",
                    "OK");
                return;
            }

            int collisions = 0;
            var report = new List<string>();

            foreach (var collector in collectors)
            {
                var providers = collector.GetComponents<ObservationProviderBase>();
                var owner     = new Dictionary<string, string>();   // key → first declaring provider

                foreach (var p in providers)
                {
                    foreach (var key in p.DeclaredObservationKeys)
                    {
                        if (owner.TryGetValue(key, out var firstOwner))
                        {
                            collisions++;
                            Debug.LogWarning(
                                $"[Biomata] '{collector.name}': observation key '{key}' is declared by " +
                                $"both {firstOwner} and {p.GetType().Name} — the later provider silently " +
                                "overwrites the earlier. Rename one key or remove the duplicate provider.",
                                collector);
                        }
                        else
                        {
                            owner[key] = p.GetType().Name;
                        }
                    }
                }

                var keys = owner.Keys.OrderBy(k => k);
                report.Add($"  {collector.name}: {(owner.Count == 0 ? "(no declared keys)" : string.Join(", ", keys))}");
            }

            Debug.Log(
                $"[Biomata] Observation contract — {collectors.Length} agent(s), {collisions} collision(s).\n" +
                string.Join("\n", report));

            EditorUtility.DisplayDialog(
                "Biomata — Observation Contract",
                collisions == 0
                    ? $"{collectors.Length} agent(s) checked. No key collisions.\n\nSee Console for the per-agent key report."
                    : $"{collisions} key collision(s) across {collectors.Length} agent(s).\n\nSee Console for details.",
                "OK");
        }
    }
}
#endif
