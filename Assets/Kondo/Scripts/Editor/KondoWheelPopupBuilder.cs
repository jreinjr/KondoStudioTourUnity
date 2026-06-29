using RenderHeads.Media.AVProVideo;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Kondo.Slideshow;

namespace Kondo.EditorTools
{
    /// <summary>
    /// Adds a centered 720x480 windowed popup video to Slide_Wheel, driven by a new
    /// ShowOverlay hotspot. Idempotent — re-running reuses the existing popup/hotspot
    /// instead of duplicating them. The popup lives outside ZoomRoot so the overlay
    /// zoom leaves the window unscaled.
    /// </summary>
    public static class KondoWheelPopupBuilder
    {
        const string SlidePath = "Assets/Kondo/Slideshow/Prefabs/Slides/Slide_Wheel.prefab";
        const string HotspotPrefabPath = "Assets/Kondo/Slideshow/Prefabs/Base/Hotspot.prefab";
        const string PopupName = "PopupVideo";
        const string HotspotName = "WheelVideoHotspot";
        static readonly Vector2 PopupSize = new Vector2(720f, 480f);

        [MenuItem("Kondo/Add Wheel Popup Video")]
        public static void AddWheelPopupVideo()
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(SlidePath);
            try
            {
                var slide = contents.GetComponent<Slide>();
                if (slide == null)
                {
                    Debug.LogError($"[KondoWheelPopupBuilder] {SlidePath} has no Slide component.");
                    return;
                }

                SlidePopupVideoElement popup = EnsurePopup(contents.transform, slide.style);
                SlideHotspot hotspot = EnsureHotspot(contents.transform, slide.style, popup);

                PrefabUtility.SaveAsPrefabAsset(contents, SlidePath);
                Debug.Log($"[KondoWheelPopupBuilder] Slide_Wheel now has '{popup.name}' (720x480 centered) " +
                          $"driven by ShowOverlay hotspot '{hotspot.name}'. Set the popup's videoPath in the Inspector.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        /// <summary>Centered popup video window, a sibling of ZoomRoot/Text (so it never zooms), drawn on top.</summary>
        static SlidePopupVideoElement EnsurePopup(Transform root, SlideshowStyle style)
        {
            Transform existing = root.Find(PopupName);
            if (existing != null)
                return existing.GetComponent<SlidePopupVideoElement>();

            var go = new GameObject(PopupName, typeof(RectTransform), typeof(CanvasGroup), typeof(SlidePopupVideoElement));
            var rect = (RectTransform)go.transform;
            rect.SetParent(root, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = PopupSize;
            rect.SetAsLastSibling(); // on top of the slide content

            // Black backing so the window reads as a panel before the first frame uploads.
            var bgGo = new GameObject("Background", typeof(RectTransform), typeof(Image));
            FullStretch((RectTransform)bgGo.transform, rect);
            var bg = bgGo.GetComponent<Image>();
            bg.color = Color.black;
            bg.raycastTarget = false;

            // MediaPlayer + DisplayUGUI share one GameObject (same pairing as the slide background).
            var videoGo = new GameObject("Video", typeof(RectTransform), typeof(MediaPlayer), typeof(DisplayUGUI));
            FullStretch((RectTransform)videoGo.transform, rect);
            var player = videoGo.GetComponent<MediaPlayer>();
            player.AutoOpen = false;
            player.AutoStart = false;
            player.Loop = true;
            var display = videoGo.GetComponent<DisplayUGUI>();
            display.Player = player;
            display.ScaleMode = ScaleMode.StretchToFill;
            display.NoDefaultDisplay = true;
            display.raycastTarget = false;

            var element = go.GetComponent<SlidePopupVideoElement>();
            element.style = style;
            element.group = go.GetComponent<CanvasGroup>();
            element.group.alpha = 0f; // overlay-only: hidden until the hotspot fires
            element.player = player;
            element.display = display;
            element.loop = true;
            return element;
        }

        /// <summary>Full-screen ShowOverlay hotspot whose overlay reveals the popup video.</summary>
        static SlideHotspot EnsureHotspot(Transform root, SlideshowStyle style, SlidePopupVideoElement popup)
        {
            Transform container = root.Find("ZoomRoot/Hotspots");
            if (container == null)
            {
                Debug.LogError("[KondoWheelPopupBuilder] No ZoomRoot/Hotspots container — run 'Kondo/Upgrade Slide Prefabs' first.");
                return null;
            }

            Transform existing = container.Find(HotspotName);
            SlideHotspot hotspot;
            if (existing != null)
            {
                hotspot = existing.GetComponent<SlideHotspot>();
            }
            else
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(HotspotPrefabPath);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                instance.name = HotspotName;
                var rect = (RectTransform)instance.transform;
                FullStretch(rect, (RectTransform)container);
                hotspot = instance.GetComponent<SlideHotspot>();
            }

            hotspot.style = style;
            hotspot.action = HotspotAction.ShowOverlay;
            hotspot.point = new Vector2(0.5f, 0.5f); // center of the slide
            hotspot.label = "Wheel Video";
            hotspot.overlayElements = new SlideFadeInElement[] { popup };
            return hotspot;
        }

        static void FullStretch(RectTransform rect, RectTransform parent)
        {
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
