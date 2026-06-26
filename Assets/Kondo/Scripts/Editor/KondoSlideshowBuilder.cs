using RenderHeads.Media.AVProVideo;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using Kondo.Pointing;
using Kondo.Slideshow;

namespace Kondo.EditorTools
{
    /// <summary>
    /// Builds the slideshow rig (video / slide / blackout canvases + controller) and
    /// ensures the shared style asset and base prefabs exist. Idempotent — re-running
    /// replaces the scene objects but never overwrites existing assets, so customized
    /// prefabs and styles survive.
    /// </summary>
    public static class KondoSlideshowBuilder
    {
        /// <summary>Design/reference resolution shared by every canvas and slide prefab.</summary>
        public static readonly Vector2 DesignResolution = new Vector2(2880f, 2160f);

        const string StylePath = "Assets/Kondo/Slideshow/Styles/DefaultSlideshowStyle.asset";
        const string UIEnvironmentScenePath = "Assets/Kondo/Slideshow/UIPrefabEnvironment.unity";
        const string BasePrefabFolder = "Assets/Kondo/Slideshow/Prefabs/Base";
        const string SlidesPrefabFolder = "Assets/Kondo/Slideshow/Prefabs/Slides";
        const string IndicatorPrefabPath = BasePrefabFolder + "/DwellIndicator.prefab";
        const string HotspotPrefabPath = BasePrefabFolder + "/Hotspot.prefab";
        const string TextBlockPrefabPath = BasePrefabFolder + "/TextBlock.prefab";
        const string FocusMaskPrefabPath = BasePrefabFolder + "/FocusMask.prefab";
        const string SlideTemplatePrefabPath = BasePrefabFolder + "/SlideTemplate.prefab";

        [MenuItem("Kondo/Build Slideshow Rig")]
        public static void BuildMenuItem()
        {
            BuildRig();
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log("[KondoSlideshowBuilder] Slideshow rig built in the open scene (scene not saved).");
        }

        [MenuItem("Kondo/New Slide Prefab")]
        public static void NewSlidePrefab()
        {
            SlideshowStyle style = EnsureStyle();
            EnsureBasePrefabs(style);
            EnsureFolder("Assets/Kondo/Slideshow/Prefabs", "Slides");

            string path = AssetDatabase.GenerateUniqueAssetPath(SlidesPrefabFolder + "/Slide_New.prefab");
            AssetDatabase.CopyAsset(SlideTemplatePrefabPath, path);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Selection.activeObject = prefab;
            EditorGUIUtility.PingObject(prefab);
            Debug.Log("[KondoSlideshowBuilder] Created " + path);
        }

        /// <summary>
        /// Migrate slide prefabs authored before ZoomRoot existed: insert a full-stretch
        /// ZoomRoot and reparent the background/masks/hotspots organizers into it
        /// (Text stays outside so overlay zoom never scales it). Idempotent.
        /// </summary>
        /// <summary>
        /// Create and assign the UI prefab editing environment: a scene with a canvas
        /// matching the show's scaler, so UI prefabs opened in the prefab stage render
        /// inside a real canvas instead of an empty void.
        /// </summary>
        [MenuItem("Kondo/Setup UI Prefab Environment")]
        public static void SetupUIPrefabEnvironment()
        {
            var sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(UIEnvironmentScenePath);
            if (sceneAsset == null)
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                try
                {
                    var go = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler));
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);
                    go.layer = LayerMask.NameToLayer("UI");
                    var canvas = go.GetComponent<Canvas>();
                    canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    var scaler = go.GetComponent<CanvasScaler>();
                    scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                    scaler.referenceResolution = DesignResolution;
                    scaler.matchWidthOrHeight = 0.5f;
                    EditorSceneManager.SaveScene(scene, UIEnvironmentScenePath);
                }
                finally
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
                sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(UIEnvironmentScenePath);
            }

            if (EditorSettings.prefabUIEnvironment != sceneAsset)
                EditorSettings.prefabUIEnvironment = sceneAsset;
            Debug.Log("[KondoSlideshowBuilder] UI prefab editing environment set to " + UIEnvironmentScenePath +
                      " — UI prefabs now open inside a " + DesignResolution.x + "x" + DesignResolution.y + " canvas.");
        }

        [MenuItem("Kondo/Upgrade Slide Prefabs")]
        public static void UpgradeSlidePrefabs()
        {
            int upgraded = 0;
            var paths = new System.Collections.Generic.List<string>();
            if (AssetDatabase.LoadAssetAtPath<GameObject>(SlideTemplatePrefabPath) != null)
                paths.Add(SlideTemplatePrefabPath);
            if (AssetDatabase.IsValidFolder(SlidesPrefabFolder))
                foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { SlidesPrefabFolder }))
                    paths.Add(AssetDatabase.GUIDToAssetPath(guid));

            foreach (string path in paths)
            {
                GameObject contents = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    if (UpgradeSlideContents(contents))
                    {
                        PrefabUtility.SaveAsPrefabAsset(contents, path);
                        upgraded++;
                        Debug.Log("[KondoSlideshowBuilder] Upgraded " + path);
                    }
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
            }
            Debug.Log($"[KondoSlideshowBuilder] Slide prefab upgrade complete — {upgraded} of {paths.Count} prefab(s) modified.");
        }

        static bool UpgradeSlideContents(GameObject root)
        {
            var slide = root.GetComponent<Slide>();
            if (slide == null)
                return false;

            var rootRect = (RectTransform)root.transform;
            bool changed = SetDesignSize(rootRect);

            var canvas = root.GetComponent<Canvas>();
            if (canvas == null)
            {
                canvas = root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                changed = true;
            }

            if (slide.zoomRoot == null)
            {
                var zoomRoot = rootRect.Find("ZoomRoot") as RectTransform;
                if (zoomRoot == null)
                {
                    var go = new GameObject("ZoomRoot", typeof(RectTransform));
                    zoomRoot = (RectTransform)go.transform;
                    FullStretch(zoomRoot, rootRect);
                    zoomRoot.SetAsFirstSibling();
                }

                foreach (string childName in new[] { "BackgroundImage", "BackgroundVideo", "Masks", "Hotspots" })
                {
                    Transform child = rootRect.Find(childName);
                    if (child != null && child.parent != zoomRoot)
                        child.SetParent(zoomRoot, false);
                }

                slide.zoomRoot = zoomRoot;
                changed = true;
            }

            return changed;
        }

        /// <summary>
        /// Author slide roots at the fixed design resolution so the prefab stage shows
        /// real content instead of a collapsed 100x100 rect (there is no canvas to
        /// stretch into there). SlideshowController forces full-stretch on spawn regardless.
        /// </summary>
        static bool SetDesignSize(RectTransform rect)
        {
            var half = new Vector2(0.5f, 0.5f);
            Vector2 design = DesignResolution;
            if (rect.anchorMin == half && rect.anchorMax == half &&
                rect.sizeDelta == design && rect.anchoredPosition == Vector2.zero)
                return false;
            rect.anchorMin = half;
            rect.anchorMax = half;
            rect.pivot = half;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = design;
            return true;
        }

        /// <summary>Rebuild the slideshow scene objects. Called by KondoSceneBuilder.Build() and the menu item.</summary>
        public static void BuildRig()
        {
            foreach (GameObject root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name == "[Kondo] VideoCanvas" || root.name == "[Kondo] SlideCanvas" ||
                    root.name == "[Kondo] BlackoutCanvas" || root.name == "[Kondo] HotspotRowCanvas" ||
                    root.name == "[Kondo] Slideshow")
                    Object.DestroyImmediate(root);
            }

            SlideshowStyle style = EnsureStyle();
            EnsureBasePrefabs(style);
            SetupUIPrefabEnvironment();

            RectTransform videoCanvas = BuildCanvas("[Kondo] VideoCanvas", -20);
            RectTransform slideCanvas = BuildCanvas("[Kondo] SlideCanvas", -10);
            RectTransform blackoutCanvas = BuildCanvas("[Kondo] BlackoutCanvas", -5);

            // Transition video display, occluded by the opaque slide above until fade-out.
            var displayGo = new GameObject("TransitionVideo", typeof(RectTransform), typeof(DisplayUGUI));
            FullStretch((RectTransform)displayGo.transform, videoCanvas);
            var display = displayGo.GetComponent<DisplayUGUI>();
            display.ScaleMode = ScaleMode.StretchToFill;
            display.NoDefaultDisplay = true;
            display.raycastTarget = false;

            // Black overlay for fade-through-black transitions.
            var blackoutGo = new GameObject("Blackout", typeof(RectTransform), typeof(CanvasGroup), typeof(Image));
            FullStretch((RectTransform)blackoutGo.transform, blackoutCanvas);
            var blackoutImage = blackoutGo.GetComponent<Image>();
            blackoutImage.color = Color.black;
            blackoutImage.raycastTarget = false;
            var blackoutGroup = blackoutGo.GetComponent<CanvasGroup>();
            blackoutGroup.alpha = 0f;

            var rig = new GameObject("[Kondo] Slideshow");
            var mediaPlayer = rig.AddComponent<MediaPlayer>();
            mediaPlayer.AutoOpen = false;
            mediaPlayer.AutoStart = false;
            mediaPlayer.Loop = false;
            display.Player = mediaPlayer;

            var transitionVideo = rig.AddComponent<TransitionVideoPlayer>();
            transitionVideo.mediaPlayer = mediaPlayer;
            transitionVideo.display = display;

            var provider = rig.AddComponent<SlideshowPointerProvider>();
            provider.style = style;
            provider.pointerManager = Object.FindFirstObjectByType<UserPointerManager>();
            if (provider.pointerManager == null)
                Debug.LogWarning("[KondoSlideshowBuilder] No UserPointerManager in the scene — skeleton hover will be inactive (mouse still works). Run 'Kondo/Build Studio Tour Scene' for the full rig.");

            var controller = rig.AddComponent<SlideshowController>();
            controller.style = style;
            controller.slideCanvas = slideCanvas;
            controller.blackout = blackoutGroup;
            controller.pointers = provider;
            controller.video = transitionVideo;
            var hud = rig.AddComponent<SlideshowDebugHud>();
            hud.controller = controller;
            hud.pointers = provider;

            // Bottom-row hotspot selection UI (used when the controller's hotspotMode is BottomRow).
            // Above the slide/blackout, below the cursor.
            RectTransform rowCanvas = BuildCanvas("[Kondo] HotspotRowCanvas", -2);
            var rowGo = new GameObject("Row", typeof(RectTransform), typeof(CanvasGroup), typeof(HotspotRowView));
            var rowRect = (RectTransform)rowGo.transform;
            FullStretch(rowRect, rowCanvas);
            var rowView = rowGo.GetComponent<HotspotRowView>();
            rowView.style = style;
            rowView.container = rowRect;
            rowView.group = rowGo.GetComponent<CanvasGroup>();
            rowView.group.alpha = 0f;
            var indicatorPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(IndicatorPrefabPath);
            rowView.indicatorPrefab = indicatorPrefab != null ? indicatorPrefab.GetComponent<DwellIndicator>() : null;
            controller.hotspotRow = rowView;

            controller.startSlidePrefab = FindFirstSlidePrefab();
            if (controller.startSlidePrefab == null)
                Debug.LogWarning("[KondoSlideshowBuilder] No slide prefab found in " + SlidesPrefabFolder +
                                 " — create one via 'Kondo/New Slide Prefab' and assign it as the controller's start slide.");
        }

        static SlideshowStyle EnsureStyle()
        {
            var style = AssetDatabase.LoadAssetAtPath<SlideshowStyle>(StylePath);
            if (style != null)
                return style;
            EnsureFolder("Assets/Kondo/Slideshow", "Styles");
            style = ScriptableObject.CreateInstance<SlideshowStyle>();
            AssetDatabase.CreateAsset(style, StylePath);
            AssetDatabase.SaveAssets();
            return style;
        }

        static void EnsureBasePrefabs(SlideshowStyle style)
        {
            EnsureFolder("Assets/Kondo/Slideshow/Prefabs", "Base");
            GameObject indicator = EnsureDwellIndicatorPrefab(style);
            EnsureHotspotPrefab(style, indicator);
            EnsureTextBlockPrefab(style);
            EnsureFocusMaskPrefab(style);
            EnsureSlideTemplatePrefab(style);
        }

        static GameObject EnsureDwellIndicatorPrefab(SlideshowStyle style)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(IndicatorPrefabPath);
            if (existing != null)
                return existing;

            Sprite knob = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd");

            var root = new GameObject("DwellIndicator", typeof(RectTransform), typeof(CanvasGroup), typeof(DwellIndicator));
            var rootRect = (RectTransform)root.transform;
            rootRect.sizeDelta = new Vector2(96f, 96f);

            Image MakeImage(string name)
            {
                var go = new GameObject(name, typeof(RectTransform), typeof(Image));
                var rect = (RectTransform)go.transform;
                rect.SetParent(root.transform, false);
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
                var image = go.GetComponent<Image>();
                image.sprite = knob;
                image.raycastTarget = false;
                return image;
            }

            Image ring = MakeImage("Ring");
            Image fill = MakeImage("Fill");

            var view = root.GetComponent<DwellIndicator>();
            view.style = style;
            view.group = root.GetComponent<CanvasGroup>();
            view.ringBackground = ring;
            view.fillImage = fill;
            view.ApplyStyle();
            view.group.alpha = 0f;

            return SavePrefab(root, IndicatorPrefabPath);
        }

        static void EnsureHotspotPrefab(SlideshowStyle style, GameObject indicatorPrefab)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(HotspotPrefabPath) != null)
                return;

            Sprite uiSprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");

            var root = new GameObject("Hotspot", typeof(RectTransform), typeof(CanvasGroup), typeof(Image), typeof(SlideHotspot));
            ((RectTransform)root.transform).sizeDelta = new Vector2(400f, 300f);
            var image = root.GetComponent<Image>();
            image.sprite = uiSprite;
            image.type = Image.Type.Sliced;
            image.raycastTarget = false;

            var indicator = (GameObject)PrefabUtility.InstantiatePrefab(indicatorPrefab);
            indicator.transform.SetParent(root.transform, false);

            var hotspot = root.GetComponent<SlideHotspot>();
            hotspot.style = style;
            hotspot.group = root.GetComponent<CanvasGroup>();
            hotspot.image = image;
            hotspot.indicator = indicator.GetComponent<DwellIndicator>();
            hotspot.ApplyStyle();

            SavePrefab(root, HotspotPrefabPath);
        }

        static void EnsureTextBlockPrefab(SlideshowStyle style)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(TextBlockPrefabPath) != null)
                return;

            var root = new GameObject("TextBlock", typeof(RectTransform), typeof(CanvasGroup), typeof(Image),
                typeof(SlideTextBlock), typeof(SlideFadeInElement));
            ((RectTransform)root.transform).sizeDelta = new Vector2(720f, 160f);
            var background = root.GetComponent<Image>();
            background.raycastTarget = false;

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            var textRect = (RectTransform)textGo.transform;
            textRect.SetParent(root.transform, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            var text = textGo.GetComponent<TextMeshProUGUI>();
            text.text = "Sample text";
            text.alignment = TextAlignmentOptions.Center;
            text.raycastTarget = false;

            var block = root.GetComponent<SlideTextBlock>();
            block.style = style;
            block.text = text;
            block.background = background;
            block.ApplyStyle();

            var fade = root.GetComponent<SlideFadeInElement>();
            fade.style = style;
            fade.group = root.GetComponent<CanvasGroup>();

            SavePrefab(root, TextBlockPrefabPath);
        }

        static void EnsureFocusMaskPrefab(SlideshowStyle style)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(FocusMaskPrefabPath) != null)
                return;

            var root = new GameObject("FocusMask", typeof(RectTransform), typeof(CanvasGroup), typeof(Image),
                typeof(SlideFocusMask), typeof(SlideFadeInElement));

            var mask = root.GetComponent<SlideFocusMask>();
            mask.style = style;
            mask.image = root.GetComponent<Image>();
            mask.ApplyStyle(); // forces full-stretch; cutout sprite is assigned per slide

            var fade = root.GetComponent<SlideFadeInElement>();
            fade.style = style;
            fade.group = root.GetComponent<CanvasGroup>();

            SavePrefab(root, FocusMaskPrefabPath);
        }

        static void EnsureSlideTemplatePrefab(SlideshowStyle style)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(SlideTemplatePrefabPath) != null)
                return;

            // The root Canvas makes the prefab render on its own in the prefab stage
            // (Graphics need a Canvas ancestor). World Space keeps the design-size rect
            // editable there; at runtime the slide nests under SlideCanvas, where the
            // render mode is ignored and the parent scaler applies (a CanvasScaler here
            // would be disabled — non-root canvases cannot be scaled).
            var root = new GameObject("SlideTemplate", typeof(RectTransform), typeof(Canvas), typeof(CanvasGroup), typeof(Slide));
            root.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            SetDesignSize((RectTransform)root.transform);

            RectTransform MakeStretchChild(string name, RectTransform parent, params System.Type[] components)
            {
                var go = new GameObject(name, components);
                var rect = go.GetComponent<RectTransform>();
                FullStretch(rect, parent);
                return rect;
            }

            // Everything an overlay hotspot zooms lives under ZoomRoot; Text stays outside.
            RectTransform zoomRoot = MakeStretchChild("ZoomRoot", (RectTransform)root.transform, typeof(RectTransform));

            RectTransform bgImageRect = MakeStretchChild("BackgroundImage", zoomRoot, typeof(RectTransform), typeof(Image));
            var bgImage = bgImageRect.GetComponent<Image>();
            bgImage.color = Color.white; // opaque: occludes the transition video below
            bgImage.raycastTarget = false;

            RectTransform bgVideoRect = MakeStretchChild("BackgroundVideo", zoomRoot, typeof(RectTransform), typeof(MediaPlayer), typeof(DisplayUGUI));
            var bgPlayer = bgVideoRect.GetComponent<MediaPlayer>();
            bgPlayer.AutoOpen = false;
            bgPlayer.AutoStart = false;
            var bgDisplay = bgVideoRect.GetComponent<DisplayUGUI>();
            bgDisplay.Player = bgPlayer;
            bgDisplay.ScaleMode = ScaleMode.StretchToFill;
            bgDisplay.NoDefaultDisplay = true;
            bgDisplay.raycastTarget = false;
            bgDisplay.enabled = false; // default kind is Image

            MakeStretchChild("Masks", zoomRoot, typeof(RectTransform));
            MakeStretchChild("Hotspots", zoomRoot, typeof(RectTransform));
            MakeStretchChild("Text", (RectTransform)root.transform, typeof(RectTransform));

            var slide = root.GetComponent<Slide>();
            slide.style = style;
            slide.canvasGroup = root.GetComponent<CanvasGroup>();
            slide.zoomRoot = zoomRoot;
            slide.backgroundKind = SlideBackgroundKind.Image;
            slide.backgroundImage = bgImage;
            slide.backgroundVideoDisplay = bgDisplay;
            slide.backgroundPlayer = bgPlayer;

            SavePrefab(root, SlideTemplatePrefabPath);
        }

        static Slide FindFirstSlidePrefab()
        {
            if (!AssetDatabase.IsValidFolder(SlidesPrefabFolder))
                return null;
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { SlidesPrefabFolder }))
            {
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                var slide = go != null ? go.GetComponent<Slide>() : null;
                if (slide != null)
                    return slide;
            }
            return null;
        }

        static GameObject SavePrefab(GameObject sceneObject, string path)
        {
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(sceneObject, path);
            Object.DestroyImmediate(sceneObject);
            return prefab;
        }

        static RectTransform BuildCanvas(string name, int sortingOrder)
        {
            // Same scaler settings as the cursor/debug canvases so all coordinates agree.
            // No GraphicRaycaster: the slideshow does its own hit-testing.
            var go = new GameObject(name, typeof(Canvas), typeof(CanvasScaler));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = DesignResolution;
            scaler.matchWidthOrHeight = 0.5f;
            return (RectTransform)go.transform;
        }

        static void FullStretch(RectTransform rect, RectTransform parent)
        {
            if (parent != null)
                rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
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
