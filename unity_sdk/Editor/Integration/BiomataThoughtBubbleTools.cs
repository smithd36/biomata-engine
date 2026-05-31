using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace Biomata.Integration.Editor
{
    /// <summary>
    /// Scene-level editor tools for managing Thought Bubble UI on BiomataAgents.
    ///
    /// Menu: Biomata → Tools → (Add | Remove | Update) Thought Bubbles
    ///
    /// If <c>Assets/Prefabs/ThoughtBubble.prefab</c> exists the Add command instantiates it;
    /// otherwise a World-Space Canvas with a background Image and TextMeshPro label is
    /// generated procedurally. Either way an <see cref="AgentThoughtBubble"/> component
    /// is expected on the root and its TMP_Text reference is wired automatically.
    /// </summary>
    public static class BiomataThoughtBubbleTools
    {
        private const string PrefabPath      = "Assets/Prefabs/ThoughtBubble.prefab";
        private const string BubbleChildName = "ThoughtBubble";
        private const string HeightPrefKey   = "Biomata.ThoughtBubbleHeight";
        private const float  DefaultHeight   = 2f;

        // ── Add ───────────────────────────────────────────────────────────────────

        [MenuItem("Biomata/Tools/Add Thought Bubbles To Agents")]
        private static void AddThoughtBubbles()
        {
            var agents = FindAllBiomataAgents();
            if (agents.Count == 0)
            {
                Debug.Log("[BiomataThoughtBubbleTools] No BiomataAgent found in the open scene(s).");
                return;
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            int added   = 0;
            int skipped = 0;

            foreach (var agent in agents)
            {
                if (FindThoughtBubble(agent) != null) { skipped++; continue; }

                Undo.IncrementCurrentGroup();
                Undo.SetCurrentGroupName("Add Thought Bubble");

                if (prefab != null)
                    InstantiateFromPrefab(prefab, agent);
                else
                    GenerateProcedurally(agent);

                added++;
            }

            if (added > 0)
                MarkScenesDirty(agents);

            string prefabNote = prefab != null ? " (from prefab)" : " (procedural)";
            Debug.Log($"[BiomataThoughtBubbleTools] Added: {added}{prefabNote}, already had bubble: {skipped}.");
        }

        // ── Remove ────────────────────────────────────────────────────────────────

        [MenuItem("Biomata/Tools/Remove Thought Bubbles")]
        private static void RemoveThoughtBubbles()
        {
            var agents = FindAllBiomataAgents();
            int removed = 0;

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Remove Thought Bubbles");

            foreach (var agent in agents)
            {
                var bubble = FindThoughtBubble(agent);
                if (bubble == null) continue;
                Undo.DestroyObjectImmediate(bubble.gameObject);
                removed++;
            }

            if (removed > 0)
                MarkScenesDirty(agents);

            Debug.Log($"[BiomataThoughtBubbleTools] Removed {removed} thought bubble(s).");
        }

        // ── Update heights ────────────────────────────────────────────────────────

        [MenuItem("Biomata/Tools/Update Thought Bubble Heights")]
        private static void UpdateThoughtBubbleHeights()
        {
            HeightPromptWindow.Show(
                EditorPrefs.GetFloat(HeightPrefKey, DefaultHeight),
                ApplyHeightToAll);
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        private static void ApplyHeightToAll(float height)
        {
            EditorPrefs.SetFloat(HeightPrefKey, height);

            var agents = FindAllBiomataAgents();
            int updated = 0;

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Update Thought Bubble Heights");

            foreach (var agent in agents)
            {
                var bubble = FindThoughtBubble(agent);
                if (bubble == null) continue;
                Undo.RecordObject(bubble, "Update Thought Bubble Height");
                bubble.localPosition = new Vector3(0f, height, 0f);
                updated++;
            }

            if (updated > 0)
                MarkScenesDirty(agents);

            Debug.Log($"[BiomataThoughtBubbleTools] Moved {updated} thought bubble(s) to y = {height}.");
        }

        private static void InstantiateFromPrefab(GameObject prefab, BiomataAgent agent)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, agent.transform);
            Undo.RegisterCreatedObjectUndo(instance, "Add Thought Bubble");

            instance.name = BubbleChildName;
            var t = instance.transform;
            t.localPosition = new Vector3(0f, EditorPrefs.GetFloat(HeightPrefKey, DefaultHeight), 0f);
            t.localRotation = Quaternion.identity;
            t.localScale    = Vector3.one;

            // Wire TMP_Text if the prefab has an AgentThoughtBubble but no label assigned.
            var atb = instance.GetComponent<AgentThoughtBubble>();
            if (atb != null)
            {
                var tmp = instance.GetComponentInChildren<TMP_Text>();
                if (tmp != null)
                {
                    Undo.RecordObject(atb, "Wire Thought Bubble Label");
                    atb.WireLabel(tmp);
                }
            }
        }

        private static void GenerateProcedurally(BiomataAgent agent)
        {
            float  height      = EditorPrefs.GetFloat(HeightPrefKey, DefaultHeight);
            string defaultText = !string.IsNullOrEmpty(agent.DisplayName)
                ? agent.DisplayName
                : agent.gameObject.name;

            // ── Root: ThoughtBubble (World-Space Canvas) ──────────────────────────

            var bubbleGo = new GameObject(BubbleChildName);
            Undo.RegisterCreatedObjectUndo(bubbleGo, "Add Thought Bubble");
            bubbleGo.transform.SetParent(agent.transform, worldPositionStays: false);
            bubbleGo.transform.localPosition = new Vector3(0f, height, 0f);
            bubbleGo.transform.localRotation = Quaternion.identity;
            bubbleGo.transform.localScale    = Vector3.one;

            var canvas = bubbleGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            var canvasRt = bubbleGo.GetComponent<RectTransform>();
            canvasRt.sizeDelta  = new Vector2(300f, 100f);
            canvasRt.localScale = new Vector3(0.01f, 0.01f, 0.01f);

            // ── Background Image ──────────────────────────────────────────────────

            var bgGo = new GameObject("Background");
            Undo.RegisterCreatedObjectUndo(bgGo, "Add Thought Bubble Background");
            bgGo.transform.SetParent(bubbleGo.transform, worldPositionStays: false);

            var bgImage = bgGo.AddComponent<Image>();
            bgImage.color = new Color(0.05f, 0.05f, 0.05f, 0.70f);

            var bgRt = bgGo.GetComponent<RectTransform>();
            bgRt.anchorMin = Vector2.zero;
            bgRt.anchorMax = Vector2.one;
            bgRt.offsetMin = Vector2.zero;
            bgRt.offsetMax = Vector2.zero;

            // ── Label (TextMeshProUGUI) ────────────────────────────────────────────

            var labelGo = new GameObject("Label");
            Undo.RegisterCreatedObjectUndo(labelGo, "Add Thought Bubble Label");
            labelGo.transform.SetParent(bubbleGo.transform, worldPositionStays: false);

            var tmp = labelGo.AddComponent<TextMeshProUGUI>();
            tmp.text             = defaultText;
            tmp.alignment        = TextAlignmentOptions.Center;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin      = 8f;
            tmp.fontSizeMax      = 36f;
            tmp.color            = Color.white;

            var labelRt = labelGo.GetComponent<RectTransform>();
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = new Vector2(6f,  4f);
            labelRt.offsetMax = new Vector2(-6f, -4f);

            // ── AgentThoughtBubble — wire TMP reference ───────────────────────────

            var thoughtBubble = bubbleGo.AddComponent<AgentThoughtBubble>();
            thoughtBubble.WireLabel(tmp);
        }

        private static List<BiomataAgent> FindAllBiomataAgents() =>
            new List<BiomataAgent>(Object.FindObjectsByType<BiomataAgent>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None));

        private static Transform FindThoughtBubble(BiomataAgent agent) =>
            agent.transform.Find(BubbleChildName);

        private static void MarkScenesDirty(List<BiomataAgent> agents)
        {
            var seen = new HashSet<int>();
            foreach (var a in agents)
            {
                var scene = a.gameObject.scene;
                if (scene.IsValid() && seen.Add(scene.handle))
                    EditorSceneManager.MarkSceneDirty(scene);
            }
        }

        // ── Height prompt window ──────────────────────────────────────────────────

        private class HeightPromptWindow : EditorWindow
        {
            private float                _height;
            private System.Action<float> _onApply;

            public static void Show(float currentHeight, System.Action<float> onApply)
            {
                var window = CreateInstance<HeightPromptWindow>();
                window.titleContent = new GUIContent("Thought Bubble Height");
                window.minSize      = new Vector2(260f, 72f);
                window.maxSize      = new Vector2(260f, 72f);
                window._height      = currentHeight;
                window._onApply     = onApply;
                window.ShowUtility();
            }

            private void OnGUI()
            {
                EditorGUILayout.Space(6);
                _height = EditorGUILayout.FloatField("Local Y position", _height);
                EditorGUILayout.Space(4);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Apply"))   { _onApply?.Invoke(_height); Close(); }
                    if (GUILayout.Button("Cancel"))  { Close(); }
                }
            }
        }
    }
}
