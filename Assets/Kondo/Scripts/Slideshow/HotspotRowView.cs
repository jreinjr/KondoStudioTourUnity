using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Kondo.Slideshow
{
    /// <summary>
    /// Bottom-row hotspot selection: lays the current slide's hotspots out as evenly-spaced
    /// named labels along the bottom of the screen and relocates each hotspot's hit zone onto
    /// its label. The in-image highlight graphic stays put (only its dwell indicator moves to
    /// the label). Implements <see cref="IHotspotSelector"/>; the controller keeps owning dwell
    /// and firing. Label items are built in code from <see cref="SlideshowStyle"/>.
    /// </summary>
    public class HotspotRowView : MonoBehaviour, IHotspotSelector
    {
        public SlideshowStyle style;
        [Tooltip("Full-screen-width container the labels are laid out in (design space, bottom-left origin).")]
        public RectTransform container;
        [Tooltip("Drives the whole row's show/hide fade.")]
        public CanvasGroup group;
        [Tooltip("Base DwellIndicator prefab cloned onto each label for the radial dwell ring.")]
        public DwellIndicator indicatorPrefab;

        class Item
        {
            public SlideHotspot hotspot;
            public RectTransform rect;
            public Image background;
            public DwellIndicator indicator;
        }

        readonly List<Item> items = new List<Item>();
        readonly Dictionary<SlideHotspot, Item> map = new Dictionary<SlideHotspot, Item>();
        float slotHalfWidthDesign;
        float targetAlpha;

        void Awake()
        {
            if (group != null)
                group.alpha = 0f;
        }

        void Update()
        {
            if (group == null)
                return;
            float fade = style != null ? style.rowFadeSeconds : 0.25f;
            if (!Mathf.Approximately(group.alpha, targetAlpha))
                group.alpha = Mathf.MoveTowards(group.alpha, targetAlpha, Time.deltaTime / Mathf.Max(fade, 1e-3f));
        }

        public void OnSlideChanged(Slide slide)
        {
            ClearItems();
            if (slide == null || container == null || style == null)
                return;

            var hotspots = slide.Hotspots;
            int n = hotspots != null ? hotspots.Count : 0;
            if (n == 0)
                return;

            float width = container.rect.width;
            if (width < 1f)
                width = 2880f; // design width fallback if layout hasn't resolved yet
            float height = style.rowHeightDesign;
            float slotW = width / n;
            float labelW = Mathf.Max(10f, slotW - style.rowLabelSpacingDesign);
            float cy = style.rowBottomMarginDesign + height * 0.5f;
            slotHalfWidthDesign = slotW * 0.5f;

            for (int i = 0; i < n; i++)
            {
                SlideHotspot hotspot = hotspots[i];
                if (hotspot == null)
                    continue;
                float cx = slotW * (i + 0.5f);
                Item item = BuildItem(hotspot, new Vector2(cx, cy), new Vector2(labelW, height));
                items.Add(item);
                map[hotspot] = item;

                // The ring now lives on the label; suppress the in-image dwell indicator.
                hotspot.DriveIndicator = false;
                if (hotspot.indicator != null)
                    hotspot.indicator.HideImmediate();
            }
        }

        Item BuildItem(SlideHotspot hotspot, Vector2 anchoredPos, Vector2 size)
        {
            var go = new GameObject($"RowItem_{hotspot.DisplayLabel}", typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(container, false);
            rect.anchorMin = Vector2.zero; // bottom-left of the container
            rect.anchorMax = Vector2.zero;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = anchoredPos;

            var bg = go.GetComponent<Image>();
            bg.color = style.rowLabelBg;
            bg.raycastTarget = false;

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            var textRect = (RectTransform)textGo.transform;
            textRect.SetParent(rect, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(16f, 0f);
            textRect.offsetMax = new Vector2(-size.y, 0f); // leave room for the ring on the right
            var tmp = textGo.GetComponent<TextMeshProUGUI>();
            tmp.text = hotspot.DisplayLabel;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = style.rowLabelColor;
            tmp.fontSize = style.rowFontSize;
            tmp.enableAutoSizing = false;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.raycastTarget = false;
            if (style.rowFont != null)
                tmp.font = style.rowFont;

            DwellIndicator indicator = null;
            if (indicatorPrefab != null)
            {
                indicator = Instantiate(indicatorPrefab, rect);
                var indRect = (RectTransform)indicator.transform;
                indRect.anchorMin = new Vector2(1f, 0.5f);
                indRect.anchorMax = new Vector2(1f, 0.5f);
                indRect.pivot = new Vector2(1f, 0.5f);
                float s = size.y * 0.6f;
                indRect.sizeDelta = new Vector2(s, s);
                indRect.anchoredPosition = new Vector2(-size.y * 0.2f, 0f);
                if (indicator.style == null)
                    indicator.style = style;
                indicator.HideImmediate();
            }

            return new Item { hotspot = hotspot, rect = rect, background = bg, indicator = indicator };
        }

        void ClearItems()
        {
            foreach (Item item in items)
                if (item.rect != null)
                    Destroy(item.rect.gameObject);
            items.Clear();
            map.Clear();
        }

        public void SetVisible(bool visible) => targetAlpha = visible ? (style != null ? style.rowIdleAlpha : 1f) : 0f;

        public Vector2 ZonePoint(SlideHotspot hotspot)
        {
            if (map.TryGetValue(hotspot, out Item item) && item.rect != null)
                return RectTransformUtility.WorldToScreenPoint(null, item.rect.position);
            return new Vector2(-100000f, -100000f); // never hovered
        }

        public float ZoneRadius(SlideHotspot hotspot)
        {
            if (container == null)
                return 0f;
            return slotHalfWidthDesign * container.lossyScale.x;
        }

        public void Tick(IReadOnlyList<SlideHotspot> hotspots, float dt)
        {
            foreach (Item item in items)
            {
                if (item.hotspot == null)
                    continue;
                // Ring tracks dwell (Select zone only); the label background tracks the highlight
                // so it also brightens on a Hover-zone hover.
                if (item.indicator != null)
                    item.indicator.SetProgress(item.hotspot.Dwell01);
                if (item.background != null)
                    item.background.color = Color.Lerp(style.rowLabelBg, style.rowLabelHoverBg, item.hotspot.Highlight01);
            }
        }
    }
}
