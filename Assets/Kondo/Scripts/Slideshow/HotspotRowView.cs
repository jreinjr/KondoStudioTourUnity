using System.Collections.Generic;
using UnityEngine;

namespace Kondo.Slideshow
{
    /// <summary>
    /// Bottom-row hotspot selection: lays the current slide's hotspots out as evenly-spaced
    /// named labels along the bottom of the screen and relocates each hotspot's hit zone onto
    /// its label. The in-image highlight graphic stays put (only its dwell indicator moves to
    /// the label). Implements <see cref="IHotspotSelector"/>; the controller keeps owning dwell
    /// and firing. Each label is an instance of <see cref="rowItemPrefab"/> (an editor-authored
    /// <see cref="HotspotRowItem"/> prefab) — positioned and bound per hotspot, not built in code.
    /// </summary>
    public class HotspotRowView : MonoBehaviour, IHotspotSelector
    {
        public SlideshowStyle style;
        [Tooltip("Full-screen-width container the labels are laid out in (design space, bottom-left origin).")]
        public RectTransform container;
        [Tooltip("Drives the whole row's show/hide fade.")]
        public CanvasGroup group;
        [Tooltip("Prefab cloned once per hotspot to form each row label (background + text + dwell ring). Author it in the editor.")]
        public HotspotRowItem rowItemPrefab;

        readonly List<HotspotRowItem> items = new List<HotspotRowItem>();
        readonly Dictionary<SlideHotspot, HotspotRowItem> map = new Dictionary<SlideHotspot, HotspotRowItem>();
        float slotHalfWidthDesign;
        float targetAlpha;
        bool warnedMissingPrefab;

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
            if (rowItemPrefab == null)
            {
                if (!warnedMissingPrefab)
                {
                    Debug.LogWarning("[HotspotRowView] rowItemPrefab is not assigned — bottom-row hotspots cannot be " +
                        "built, so nothing is selectable. Run 'Kondo/Repair Hotspot Row' (or rebuild the rig) to create " +
                        "and assign it.", this);
                    warnedMissingPrefab = true;
                }
                return;
            }

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
                // Placeholders still consume a slot (i advances), so the gap stays reserved and
                // the real options keep their positions — we just build no label there.
                if (hotspot == null || hotspot.IsPlaceholder)
                    continue;
                float cx = slotW * (i + 0.5f);

                HotspotRowItem item = Instantiate(rowItemPrefab);
                item.transform.SetParent(container, false);
                item.name = $"RowItem_{hotspot.DisplayLabel}";
                item.Init(style, hotspot.DisplayLabel, new Vector2(cx, cy), new Vector2(labelW, height));

                items.Add(item);
                map[hotspot] = item;

                // The ring now lives on the label; suppress the in-image dwell indicator.
                hotspot.DriveIndicator = false;
                if (hotspot.indicator != null)
                    hotspot.indicator.HideImmediate();
            }
        }

        void ClearItems()
        {
            foreach (HotspotRowItem item in items)
                if (item != null)
                    Destroy(item.gameObject);
            items.Clear();
            map.Clear();
        }

        public void SetVisible(bool visible) => targetAlpha = visible ? (style != null ? style.rowIdleAlpha : 1f) : 0f;

        public Vector2 ZonePoint(SlideHotspot hotspot)
        {
            if (map.TryGetValue(hotspot, out HotspotRowItem item) && item != null)
                return RectTransformUtility.WorldToScreenPoint(null, item.Rect.position);
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
            foreach (var kv in map)
            {
                SlideHotspot hotspot = kv.Key;
                HotspotRowItem item = kv.Value;
                if (hotspot == null || item == null)
                    continue;
                // Ring tracks dwell (Select zone only); the label background tracks the highlight
                // so it also brightens on a Hover-zone hover.
                item.SetProgress(hotspot.Dwell01);
                item.SetHighlight(style, hotspot.Highlight01);
            }
        }
    }
}
