using System;
using System.Collections.Generic;
using UnityEngine;

namespace Biomata.Integration.World
{
    /// <summary>
    /// Global semantic registry mapping string IDs to Unity world objects.
    ///
    /// This is the grounding layer between:
    /// - Python symbolic references ("food_01")
    /// - Unity scene objects (Transforms / GameObjects)
    ///
    /// Used by:
    /// - MoveActionHandler (navigation targets)
    /// - Interact/Eat actions
    /// - Observation enrichment (optional future use)
    /// </summary>
    public class WorldObjectRegistry : MonoBehaviour
    {
        public static WorldObjectRegistry Instance { get; private set; }

        // ── Core storage ────────────────────────────────────────────────────────
        private readonly Dictionary<string, Transform> _objects = new();
        private readonly Dictionary<string, GameObject> _gameObjects = new();

        // Optional reverse lookup (debug / tooling)
        private readonly Dictionary<GameObject, string> _reverse = new();

        // ── Lifecycle ───────────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[WorldObjectRegistry] Duplicate instance detected. Destroying.");
                Destroy(gameObject);
                return;
            }

            Instance = this;
        }

        // ── Registration API ────────────────────────────────────────────────────

        /// <summary>
        /// Register a world object under a semantic ID.
        /// Called by FoodView, POIs, spawned agents, etc.
        /// </summary>
        public void Register(string id, GameObject obj)
        {
            if (string.IsNullOrWhiteSpace(id) || obj == null)
                return;

            _gameObjects[id] = obj;
            _objects[id]     = obj.transform;
            _reverse[obj]    = id;
        }

        /// <summary>
        /// Register directly via Transform.
        /// </summary>
        public void Register(string id, Transform t)
        {
            if (string.IsNullOrWhiteSpace(id) || t == null)
                return;

            _objects[id] = t;
            _gameObjects[id] = t.gameObject;
            _reverse[t.gameObject] = id;
        }

        /// <summary>
        /// Remove an object from registry.
        /// Safe to call on destroy.
        /// </summary>
        public void Unregister(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return;

            if (_gameObjects.TryGetValue(id, out var obj))
                _reverse.Remove(obj);

            _gameObjects.Remove(id);
            _objects.Remove(id);
        }

        // ── Lookup API ──────────────────────────────────────────────────────────

        public bool TryGetTransform(string id, out Transform t)
            => _objects.TryGetValue(id, out t);

        public bool TryGetGameObject(string id, out GameObject obj)
            => _gameObjects.TryGetValue(id, out obj);

        public Transform GetTransform(string id)
        {
            _objects.TryGetValue(id, out var t);
            return t;
        }

        public GameObject GetGameObject(string id)
        {
            _gameObjects.TryGetValue(id, out var obj);
            return obj;
        }

        // ── Utility ─────────────────────────────────────────────────────────────

        public bool Contains(string id)
            => _objects.ContainsKey(id);

        public string GetId(GameObject obj)
        {
            _reverse.TryGetValue(obj, out var id);
            return id;
        }

        public void Clear()
        {
            _objects.Clear();
            _gameObjects.Clear();
            _reverse.Clear();
        }
    }
}