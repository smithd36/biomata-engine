using System.Collections.Generic;
using UnityEngine;

namespace Biomata.Integration.Observations
{
    /// <summary>
    /// Base class for all observation data sources.
    ///
    /// Attach any number of concrete providers to the same GameObject as
    /// <see cref="ObservationCollector"/> to compose the observation dictionary
    /// sent to the backend each tick.
    ///
    /// ── Implementing a custom provider ───────────────────────────────────────
    ///
    /// 1. Subclass and add <c>[AddComponentMenu("Biomata/Observations/YourName")]</c>
    ///    so designers can find it in the Add Component menu.
    ///
    /// 2. Declare <c>[SerializeField]</c> fields for every knob the designer needs.
    ///    Use <c>[Tooltip]</c> so the Inspector is self-documenting.
    ///
    /// 3. Override <see cref="Populate"/> and write key-value pairs into the
    ///    <c>observation</c> dictionary.  Key names appear verbatim in the Python
    ///    agent's observation dict.  All values must be JSON-serializable:
    ///    <c>string</c>, <c>double</c>, <c>bool</c>, <c>long</c>,
    ///    <c>List&lt;object&gt;</c>, or <c>Dictionary&lt;string, object&gt;</c>.
    ///
    /// 4. Use <c>Awake()</c> for one-time initialization — component lookups,
    ///    tag-based scene scans, physics layer resolution, etc.
    ///    Keep <see cref="Populate"/> allocation-free where possible (reuse lists).
    ///
    /// 5. Add <c>OnDrawGizmosSelected()</c> when the provider is spatial so designers
    ///    can see its radius or coverage in the Scene view.
    ///
    /// ── Contracts guaranteed by <see cref="ObservationCollector"/> ───────────
    ///
    /// • <see cref="Populate"/> is called once per tick, immediately before the
    ///   tick RPC is sent to the backend.
    /// • Providers are called in component order; later providers may overwrite
    ///   keys written by earlier ones (intentional — use it for layering defaults).
    /// • <see cref="ObservationCollector.SetData"/> keys always win; they are
    ///   applied after all providers.
    /// • Providers with <c>isActiveAndEnabled == false</c> are skipped.
    ///
    /// See <see cref="TransformObservationProvider"/>, <see cref="TimeObservationProvider"/>,
    /// and <see cref="POIObservationProvider"/> for reference implementations.
    /// </summary>
    public abstract class ObservationProviderBase : MonoBehaviour
    {
        /// <summary>
        /// Write this provider's data into <paramref name="observation"/>.
        /// Called once per simulation tick by <see cref="ObservationCollector"/>.
        /// </summary>
        public abstract void Populate(Dictionary<string, object> observation);

        /// <summary>
        /// The fixed top-level observation keys this provider writes — its half of the
        /// observation contract (see <see cref="ObservationKeys"/>). Used by the editor
        /// validator (<c>Biomata &gt; Validate Observation Contract</c>) to surface the keys
        /// an agent emits and to flag two providers colliding on the same key.
        ///
        /// Default: empty. Override in providers with a stable key set. Providers whose keys
        /// are configurable or per-element dynamic (POIs, nearby objects, needs) may return
        /// empty — they opt out of static collision checking.
        /// </summary>
        public virtual IReadOnlyCollection<string> DeclaredObservationKeys =>
            System.Array.Empty<string>();
    }
}
