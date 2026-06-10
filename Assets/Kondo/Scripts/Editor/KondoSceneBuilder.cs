using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Kondo.Core;
using Kondo.Debugging;
using Kondo.Pointing;
using Kondo.UI;

namespace Kondo.EditorTools
{
    /// <summary>
    /// Builds the KondoStudioTour scene: camera, NuitrackScripts (AI on), cursor canvas,
    /// pointing system, and debug overlay. Idempotent — re-running replaces the
    /// previously built objects, so it doubles as a repair tool.
    /// </summary>
    public static class KondoSceneBuilder
    {
        const string ScenePath = "Assets/Scenes/KondoStudioTour.unity";
        const string NuitrackPrefabPath = "Assets/NuitrackSDK/Nuitrack/Prefabs/NuitrackScripts.prefab";
        const string CursorPrefabPath = "Assets/Kondo/Prefabs/PointerCursor.prefab";

        [MenuItem("Kondo/Build Studio Tour Scene")]
        public static void Build()
        {
            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            foreach (GameObject root in scene.GetRootGameObjects())
                if (root.name.StartsWith("[Kondo]") || root.name.StartsWith("NuitrackScripts"))
                    Object.DestroyImmediate(root);

            BuildCamera();
            BuildNuitrack();
            RectTransform cursorCanvas = BuildCanvas("[Kondo] CursorCanvas", 0);
            RectTransform debugCanvas = BuildCanvas("[Kondo] DebugCanvas", 100);
            Text statsText = BuildStatsText(debugCanvas);
            PointerCursorView cursorPrefab = EnsureCursorPrefab();
            BuildPointingSystem(cursorCanvas, cursorPrefab, statsText);
            KondoSlideshowBuilder.BuildRig();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[KondoSceneBuilder] Scene built and saved: " + ScenePath);
        }

        static void BuildCamera()
        {
            var go = new GameObject("[Kondo] Main Camera");
            go.tag = "MainCamera";
            var cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.cullingMask = 0; // nothing in world space; UI is overlay
            go.AddComponent<AudioListener>();
            cam.GetUniversalAdditionalCameraData(); // force-add URP camera data
            go.transform.position = new Vector3(0f, 1.5f, 5f);
            go.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
        }

        static void BuildNuitrack()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(NuitrackPrefabPath);
            if (prefab == null)
            {
                Debug.LogError("[KondoSceneBuilder] NuitrackScripts prefab not found at " + NuitrackPrefabPath);
                return;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var manager = instance.GetComponent<NuitrackManager>();
            var so = new SerializedObject(manager);
            so.FindProperty("useNuitrackAi").boolValue = true;
            so.FindProperty("maxActiveUsers").intValue = 6;
            // Floor plane (our sensor-pose source) only updates via the user tracker.
            so.FindProperty("userTrackerModuleOn").boolValue = true;
            so.FindProperty("skeletonTrackerModuleOn").boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static RectTransform BuildCanvas(string name, int sortingOrder)
        {
            var go = new GameObject(name, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = KondoSlideshowBuilder.DesignResolution;
            scaler.matchWidthOrHeight = 0.5f;
            return (RectTransform)go.transform;
        }

        static Text BuildStatsText(RectTransform debugCanvas)
        {
            var go = new GameObject("StatsText", typeof(RectTransform), typeof(Text), typeof(Outline));
            var rect = (RectTransform)go.transform;
            rect.SetParent(debugCanvas, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(14f, -14f);
            rect.sizeDelta = new Vector2(1200f, 1000f);

            var text = go.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 20;
            text.color = new Color(0.4f, 1f, 0.6f);
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            go.GetComponent<Outline>().effectColor = new Color(0f, 0f, 0f, 0.8f);
            return text;
        }

        static PointerCursorView EnsureCursorPrefab()
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(CursorPrefabPath);
            if (existing != null)
                return existing.GetComponent<PointerCursorView>();

            EnsureFolder("Assets/Kondo", "Prefabs");

            Sprite knob = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd");

            var root = new GameObject("PointerCursor", typeof(RectTransform), typeof(CanvasGroup), typeof(PointerCursorView));
            var rootRect = (RectTransform)root.transform;
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.zero;
            rootRect.pivot = new Vector2(0.5f, 0.5f);
            rootRect.sizeDelta = new Vector2(80f, 80f);

            (RectTransform rect, Image image) MakeCircle(string name, float size)
            {
                var go = new GameObject(name, typeof(RectTransform), typeof(Image));
                var rect = (RectTransform)go.transform;
                rect.SetParent(root.transform, false);
                rect.sizeDelta = new Vector2(size, size);
                var image = go.GetComponent<Image>();
                image.sprite = knob;
                image.raycastTarget = false;
                return (rect, image);
            }

            (RectTransform ringRect, Image ringImage) = MakeCircle("Ring", 80f);
            (RectTransform dotRect, Image dotImage) = MakeCircle("Dot", 28f);

            var view = root.GetComponent<PointerCursorView>();
            view.ringRect = ringRect;
            view.ringImage = ringImage;
            view.dotRect = dotRect;
            view.dotImage = dotImage;
            view.canvasGroup = root.GetComponent<CanvasGroup>();

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, CursorPrefabPath);
            Object.DestroyImmediate(root);
            return prefab.GetComponent<PointerCursorView>();
        }

        static void BuildPointingSystem(RectTransform cursorCanvas, PointerCursorView cursorPrefab, Text statsText)
        {
            var go = new GameObject("[Kondo] PointingSystem");
            var calibrator = go.AddComponent<SensorPoseCalibrator>();
            var screen = go.AddComponent<ProjectionScreen>();
            var manager = go.AddComponent<UserPointerManager>();
            var debug = go.AddComponent<DebugVisualizer>();

            screen.calibrator = calibrator;

            manager.calibrator = calibrator;
            manager.screen = screen;
            manager.cursorCanvas = cursorCanvas;
            manager.cursorPrefab = cursorPrefab;

            debug.calibrator = calibrator;
            debug.screen = screen;
            debug.pointerManager = manager;
            debug.statsText = statsText;
        }

        static void EnsureFolder(string parent, string child)
        {
            if (!AssetDatabase.IsValidFolder(parent))
            {
                int slash = parent.LastIndexOf('/');
                EnsureFolder(parent.Substring(0, slash), parent.Substring(slash + 1));
            }
            if (!AssetDatabase.IsValidFolder($"{parent}/{child}"))
                AssetDatabase.CreateFolder(parent, child);
        }
    }
}
