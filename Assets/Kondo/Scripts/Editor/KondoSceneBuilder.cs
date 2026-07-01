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
        const string AnimatedCursorPrefabPath = "Assets/Kondo/Prefabs/PointerCursorAnimated.prefab";
        const string BeckonGraphicPrefabPath = "Assets/Kondo/Prefabs/BeckonGraphic.prefab";

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
            PointerCursorView animatedCursorPrefab = EnsureAnimatedCursorPrefab();
            BuildPointingSystem(cursorCanvas, debugCanvas, cursorPrefab, animatedCursorPrefab, statsText);
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
            // AI (CNN) skeleton tracking disabled: its async pose-estimation thread (libcnn-hpe
            // MeanShiftCubicClustersFinder) was hard-crashing the editor/player. The classic
            // skeletonizer covers our arm-pointing needs without that native code path.
            so.FindProperty("useNuitrackAi").boolValue = false;
            so.FindProperty("maxActiveUsers").intValue = 6;
            // Off-main-thread init: Nuitrack-recommended, reduces editor hard-crashes on (re)init
            // and bypasses the synchronous failStart crash-guard. See NuitrackFailStartReset.
            so.FindProperty("asyncInit").boolValue = true;
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

        /// <summary>
        /// Build the procedurally-driven animated cursor prefab: a shared <see cref="BeckonGraphic"/>
        /// (beckoning triangle stack) plus a <see cref="FillGraphic"/> (a bottom→top proximity fill whose
        /// sprites differ per hotspot kind), wired to the <see cref="PointerCursorView"/>. No Animator.
        /// Idempotent: an existing prefab is returned untouched so authored sprites/tuning survive a rebuild.
        /// The user assigns their triangle/fill/stroke sprites on the prefab afterwards.
        /// </summary>
        static PointerCursorView EnsureAnimatedCursorPrefab()
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(AnimatedCursorPrefabPath);
            if (existing != null)
                return existing.GetComponent<PointerCursorView>();

            EnsureFolder("Assets/Kondo", "Prefabs");

            GameObject beckonPrefab = EnsureBeckonGraphicPrefab();

            var root = new GameObject("PointerCursorAnimated",
                typeof(RectTransform), typeof(CanvasGroup), typeof(PointerCursorView));
            var rootRect = (RectTransform)root.transform;
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.zero;
            rootRect.pivot = new Vector2(0.5f, 0.5f);
            rootRect.sizeDelta = new Vector2(80f, 80f);

            // Shared beckon graphic (self-animating triangle stack).
            var beckon = (GameObject)PrefabUtility.InstantiatePrefab(beckonPrefab);
            var beckonRect = (RectTransform)beckon.transform;
            beckonRect.SetParent(root.transform, false);
            beckonRect.anchoredPosition = Vector2.zero;

            // Fill graphic (proximity meter; sprites swap per hotspot kind). Assign sprites on the prefab.
            var fillGo = new GameObject("FillGraphic", typeof(RectTransform), typeof(CanvasGroup), typeof(FillGraphic));
            var fillRect = (RectTransform)fillGo.transform;
            fillRect.SetParent(root.transform, false);
            fillRect.sizeDelta = new Vector2(64f, 64f);
            fillRect.anchoredPosition = Vector2.zero;
            Image fillOutline = MakeStretchImage("Outline", fillRect);
            Image fillFill = MakeStretchImage("Fill", fillRect);
            fillFill.type = Image.Type.Filled;
            fillFill.fillMethod = Image.FillMethod.Vertical;
            fillFill.fillOrigin = (int)Image.OriginVertical.Bottom;
            var fillGraphic = fillGo.GetComponent<FillGraphic>();
            fillGraphic.group = fillGo.GetComponent<CanvasGroup>();
            fillGraphic.outlineImage = fillOutline;
            fillGraphic.fillImage = fillFill;

            var view = root.GetComponent<PointerCursorView>();
            view.canvasGroup = root.GetComponent<CanvasGroup>();
            view.fillGraphic = fillGraphic;

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, AnimatedCursorPrefabPath);
            Object.DestroyImmediate(root);
            return prefab.GetComponent<PointerCursorView>();
        }

        /// <summary>
        /// Build (once) the shared BeckonGraphic prefab: three stacked triangle cells (outline + fill
        /// image each, bottom → top) wired into the <see cref="BeckonGraphic"/> component. Sprites are
        /// left unassigned for the user to drop in. Returned untouched if it already exists.
        /// </summary>
        static GameObject EnsureBeckonGraphicPrefab()
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(BeckonGraphicPrefabPath);
            if (existing != null)
                return existing;

            EnsureFolder("Assets/Kondo", "Prefabs");

            var root = new GameObject("BeckonGraphic", typeof(RectTransform), typeof(BeckonGraphic));
            var rootRect = (RectTransform)root.transform;
            rootRect.sizeDelta = new Vector2(28f, 96f);

            const int maxTriangles = 3;
            const float triSize = 28f;
            const float triSpacing = 4f;
            float step = triSize + triSpacing;
            float bottomY = -(maxTriangles * triSize + (maxTriangles - 1) * triSpacing) * 0.5f + triSize * 0.5f;
            var cells = new RectTransform[maxTriangles];
            var outlines = new Image[maxTriangles];
            var fills = new Image[maxTriangles];
            for (int i = 0; i < maxTriangles; i++)
            {
                var cell = new GameObject($"Triangle{i}", typeof(RectTransform));
                var cellRect = (RectTransform)cell.transform;
                cellRect.SetParent(rootRect, false);
                cellRect.sizeDelta = new Vector2(triSize, triSize);
                cellRect.anchoredPosition = new Vector2(0f, bottomY + i * step); // bottom → top
                // Outline first (behind), fill on top (its opacity is the animated part).
                outlines[i] = MakeStretchImage("Outline", cellRect);
                fills[i] = MakeStretchImage("Fill", cellRect);
                cells[i] = cellRect;
            }

            var beckon = root.GetComponent<BeckonGraphic>();
            beckon.cells = cells;
            beckon.outlines = outlines;
            beckon.fills = fills;

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, BeckonGraphicPrefabPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        /// <summary>A full-stretch, non-raycast UI Image child (sprite unassigned).</summary>
        static Image MakeStretchImage(string name, RectTransform parent)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            var image = go.GetComponent<Image>();
            image.raycastTarget = false;
            return image;
        }

        static void BuildPointingSystem(RectTransform cursorCanvas, RectTransform debugCanvas, PointerCursorView cursorPrefab, PointerCursorView animatedCursorPrefab, Text statsText)
        {
            var go = new GameObject("[Kondo] PointingSystem");
            var calibrator = go.AddComponent<SensorPoseCalibrator>();
            var screen = go.AddComponent<ProjectionScreen>();
            var manager = go.AddComponent<UserPointerManager>();
            var debug = go.AddComponent<DebugVisualizer>();
            var recorder = go.AddComponent<AimCsvRecorder>();

            screen.calibrator = calibrator;

            manager.calibrator = calibrator;
            manager.screen = screen;
            manager.cursorCanvas = cursorCanvas;
            manager.cursorPrefab = cursorPrefab;
            manager.animatedCursorPrefab = animatedCursorPrefab;
            manager.cursorMode = PointerCursorMode.Classic; // classic by default; switch to Animated to use the Animator-driven cursor
            manager.activeUserMode = ActiveUserMode.SingleUser;

            debug.calibrator = calibrator;
            debug.screen = screen;
            debug.pointerManager = manager;
            debug.statsText = statsText;

            recorder.pointerManager = manager;

            BuildSkeletonOverlay(debugCanvas, calibrator, screen, manager);
        }

        /// <summary>
        /// Add/replace the on-screen skeleton + box debug overlay (F2 toggle) on the debug
        /// canvas. Idempotent. Exposed as a menu item so it can be dropped into an already
        /// tuned scene without rebuilding (which would reset on-site pointing values).
        /// </summary>
        [MenuItem("Kondo/Add Skeleton Debug Overlay")]
        public static void AddSkeletonDebugOverlay()
        {
            var manager = Object.FindFirstObjectByType<UserPointerManager>();
            var calibrator = Object.FindFirstObjectByType<SensorPoseCalibrator>();
            var screen = Object.FindFirstObjectByType<ProjectionScreen>();
            var debugCanvasGo = GameObject.Find("[Kondo] DebugCanvas");
            if (manager == null || calibrator == null || screen == null || debugCanvasGo == null)
            {
                Debug.LogError("[KondoSceneBuilder] Could not find the pointing system or [Kondo] DebugCanvas in the open scene. " +
                               "Open the studio tour scene first (or run 'Build Studio Tour Scene' for a fresh one).");
                return;
            }

            BuildSkeletonOverlay((RectTransform)debugCanvasGo.transform, calibrator, screen, manager);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log("[KondoSceneBuilder] Skeleton debug overlay added to [Kondo] DebugCanvas — press F2 at runtime to toggle it.");
        }

        static void BuildSkeletonOverlay(RectTransform debugCanvas, SensorPoseCalibrator calibrator, ProjectionScreen screen, UserPointerManager manager)
        {
            Transform existing = debugCanvas.Find("SkeletonOverlay");
            if (existing != null)
                Object.DestroyImmediate(existing.gameObject);

            var overlayGo = new GameObject("SkeletonOverlay", typeof(RectTransform), typeof(SkeletonDebugOverlay));
            var overlayRect = (RectTransform)overlayGo.transform;
            overlayRect.SetParent(debugCanvas, false);
            overlayRect.anchorMin = Vector2.zero;
            overlayRect.anchorMax = Vector2.one;
            overlayRect.offsetMin = Vector2.zero;
            overlayRect.offsetMax = Vector2.zero;
            var overlay = overlayGo.GetComponent<SkeletonDebugOverlay>();
            overlay.calibrator = calibrator;
            overlay.screen = screen;
            overlay.pointerManager = manager;
            overlay.container = overlayRect;
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
