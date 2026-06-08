using System;
using System.Collections.Generic;
using UnityEngine;

namespace Biomata.Integration
{
    /// <summary>
    /// Data component for dynamic world objects that agents should observe —
    /// food sources, items, resource nodes, interactables, etc.
    ///
    /// Attach this to any GameObject you want reported by
    /// <see cref="Observations.NearbyObjectsObservationProvider"/>, then tag
    /// the GameObject with a matching tag (e.g. "BiomataFood", "BiomataItem").
    ///
    /// ── Static metadata ───────────────────────────────────────────────────────
    ///
    /// Use the <b>Object Type</b> field and the <b>Properties</b> list for data
    /// that does not change at runtime (item rarity, food category, object name).
    /// These are serialised once in the Inspector and read each tick.
    ///
    /// ── Dynamic metadata ──────────────────────────────────────────────────────
    ///
    /// Override <see cref="GetObservationProperties"/> in a subclass to inject
    /// runtime state (current food amount, remaining durability, etc.) alongside
    /// the static properties. The provider calls this method once per tick.
    ///
    ///   // In your game project (outside the SDK):
    ///   public class FoodObjectData : BiomataObjectData
    ///   {
    ///       private FoodView _food;
    ///       private void Awake() => _food = GetComponent&lt;FoodView&gt;();
    ///
    ///       public override Dictionary&lt;string, object&gt; GetObservationProperties()
    ///       {
    ///           var props = base.GetObservationProperties();
    ///           if (_food != null)
    ///           {
    ///               props["amount"] = _food.amount;
    ///               IsActive = !_food.IsEmpty;   // auto-filter depleted sources
    ///           }
    ///           return props;
    ///       }
    ///   }
    ///
    /// ── Filtering ─────────────────────────────────────────────────────────────
    ///
    /// Set <see cref="IsActive"/> to <c>false</c> at runtime to hide this object
    /// from observation (depleted food, picked-up items, destroyed nodes).
    /// The provider skips inactive objects without needing to know why.
    /// </summary>
    [AddComponentMenu("Biomata/Object Data")]
    public class BiomataObjectData : MonoBehaviour
    {
        [Tooltip(
            "Semantic category sent to the brain. " +
            "Examples: 'food', 'item', 'resource', 'chest', 'enemy'. " +
            "Passed verbatim as the 'type' field in the observation entry.")]
        [SerializeField] private string objectType = "object";

        [Tooltip(
            "When false this object is hidden from NearbyObjectsObservationProvider. " +
            "Set to false at runtime when the object is depleted, picked up, or destroyed.")]
        [SerializeField] private bool isActive = true;

        [Tooltip(
            "Static key-value metadata included in every observation entry. " +
            "Use for data that never changes at runtime (rarity, category, description). " +
            "For runtime-changing values, override GetObservationProperties() in a subclass.")]
        [SerializeField] private List<PropertyEntry> properties = new();

        /// <summary>Semantic category of this object (e.g. "food", "item").</summary>
        public string ObjectType => objectType;

        /// <summary>
        /// Whether this object is currently visible to observation.
        /// Set to <c>false</c> when the object is depleted or removed from play.
        /// </summary>
        public bool IsActive { get => isActive; set => isActive = value; }

        /// <summary>
        /// Returns the observation properties for this object.
        ///
        /// The base implementation returns the static <see cref="properties"/> list
        /// as a <c>Dictionary&lt;string, object&gt;</c>. Override in a subclass to
        /// inject dynamic runtime values (amount, health, quantity) before returning.
        ///
        /// Called once per tick by <see cref="Observations.NearbyObjectsObservationProvider"/>.
        /// Keep allocations low — consider reusing a cached dictionary.
        /// </summary>
        public virtual Dictionary<string, object> GetObservationProperties()
        {
            var dict = new Dictionary<string, object>(properties.Count);
            foreach (var p in properties)
                if (!string.IsNullOrEmpty(p.key))
                    dict[p.key] = p.value;
            return dict;
        }

        /// <summary>A static string key-value pair declared in the Inspector.</summary>
        [Serializable]
        public class PropertyEntry
        {
            [Tooltip("Key name as it will appear in the observation dictionary sent to Python.")]
            public string key;

            [Tooltip("String value. Subclass and override GetObservationProperties() for non-string or runtime values.")]
            public string value;
        }
    }
}
