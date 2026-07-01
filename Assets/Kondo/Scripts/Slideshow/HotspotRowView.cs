using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

namespace Kondo.Slideshow
{
    /// <summary>
    /// Bottom-row hotspot selection: lays the current slide's hotspots out as named labels along
    /// the bottom of the screen (via a HorizontalLayoutGroup) and relocates each hotspot's hit zone
    /// onto its label. Navigation labels take a fixed width (<see cref="SlideshowStyle.navHotspotWidth"/>)
    /// while Investigation labels flex to fill the space between them. The in-image highlight graphic
    /// stays put (only its dwell indicator moves to the label). Implements <see cref="IHotspotSelector"/>;
    /// the controller keeps owning dwell and firing. Each label is an instance of the per-type row-item
    /// prefab (an editor-authored <see cref="HotspotRowItem"/>) — sized/positioned by the layout group.
    /// </summary>
    public class HotspotRowView : MonoBehaviour, IHotspotSelector
    {
        public SlideshowStyle style;
        [Tooltip("Full-screen-width container the labels are laid out in (holds the HorizontalLayoutGroup).")]
        public RectTransform container;
        [Tooltip("Horizontal layout group on the container that arranges the labels (fixed nav width, flexible investigation).")]
        public HorizontalLayoutGroup layoutGroup;
        [Tooltip("Drives the whole row's show/hide fade.")]
        public CanvasGroup group;
        // FormerlySerializedAs keeps the pre-split single-prefab binding on existing scenes/prefabs.
        [FormerlySerializedAs("rowItemPrefab")]
        [Tooltip("Prefab cloned once per Navigation hotspot to form its row label (background + text + dwell ring). Author it in the editor.")]
        public HotspotRowItem navRowItemPrefab;
        [Tooltip("Prefab cloned once per Investigation hotspot. If unassigned, falls back to the navigation prefab.")]
        public HotspotRowItem investigationRowItemPrefab;

        readonly List<HotspotRowItem> items = new List<HotspotRowItem>();
        readonly List<GameObject> spacers = new List<GameObject>();
        readonly Dictionary<SlideHotspot, HotspotRowItem> map = new Dictionary<SlideHotspot, HotspotRowItem>();
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
            if (navRowItemPrefab == null)
            {
                if (!warnedMissingPrefab)
                {
                    Debug.LogWarning("[HotspotRowView] navRowItemPrefab is not assigned — bottom-row hotspots cannot be " +
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

            ConfigureLayoutGroup();

            for (int i = 0; i < n; i++)
            {
                SlideHotspot hotspot = hotspots[i];
                // Placeholders reserve a flexible gap (keeping the real options spread/aligned) but
                // build no label — a bare flexible LayoutElement in the layout group.
                if (hotspot == null || hotspot.IsPlaceholder)
                {
                    AddPlaceholderSpacer();
                    continue;
                }

                // Investigation hotspots use their own label prefab; fall back to the nav prefab if unassigned.
                HotspotRowItem prefab = hotspot.isInvestigation && investigationRowItemPrefab != null
                    ? investigationRowItemPrefab
                    : navRowItemPrefab;
                HotspotRowItem item = Instantiate(prefab);
                item.transform.SetParent(container, false);
                item.name = $"RowItem_{hotspot.DisplayLabel}";
                item.Init(style, hotspot.DisplayLabel, hotspot.fillDirection);
                // Navigation labels take the style's fixed width; investigation labels flex to fill the rest.
                if (hotspot.isInvestigation)
                    item.SetFlexibleWidth(0f);
                else
                    item.SetFixedWidth(style.navHotspotWidth);

                items.Add(item);
                map[hotspot] = item;

                // The ring now lives on the label; suppress the in-image dwell indicator.
                hotspot.DriveIndicator = false;
                if (hotspot.indicator != null)
                    hotspot.indicator.HideImmediate();
            }

            // Resolve the layout now so the hit-test zones (item centers/widths) are correct this frame.
            LayoutRebuilder.ForceRebuildLayoutImmediate(container);
        }

        /// <summary>Apply the style-driven spacing/margin to the layout group and lock in the sizing behavior.</summary>
        void ConfigureLayoutGroup()
        {
            if (layoutGroup == null)
                return;
            layoutGroup.spacing = style.rowLabelSpacingDesign;
            layoutGroup.padding.bottom = Mathf.RoundToInt(style.rowBottomMarginDesign);
            layoutGroup.childAlignment = TextAnchor.LowerCenter; // sit on the bottom margin
            layoutGroup.childControlWidth = true;   // widths come from each item's LayoutElement
            layoutGroup.childControlHeight = false; // keep each prefab's authored height
            layoutGroup.childForceExpandWidth = false;
            layoutGroup.childForceExpandHeight = false;
        }

        /// <summary>A blank, flexible spacer that reserves a gap where a placeholder hotspot sits.</summary>
        void AddPlaceholderSpacer()
        {
            var go = new GameObject("RowPlaceholder", typeof(RectTransform), typeof(LayoutElement));
            go.transform.SetParent(container, false);
            var le = go.GetComponent<LayoutElement>();
            le.minWidth = 0f;
            le.flexibleWidth = 1f;
            spacers.Add(go);
        }

        void ClearItems()
        {
            foreach (HotspotRowItem item in items)
                if (item != null)
                    Destroy(item.gameObject);
            items.Clear();
            foreach (GameObject spacer in spacers)
                if (spacer != null)
                    Destroy(spacer);
            spacers.Clear();
            map.Clear();
        }

        // The whole row simply shows/hides; per-label opacity is baked into each prefab's authored colors.
        public void SetVisible(bool visible) => targetAlpha = visible ? 1f : 0f;

        public Vector2 ZonePoint(SlideHotspot hotspot)
        {
            if (map.TryGetValue(hotspot, out HotspotRowItem item) && item != null)
            {
                // The layout group drives the item's width, so use its rect center (not the pivot).
                Vector3 worldCenter = item.Rect.TransformPoint(item.Rect.rect.center);
                return RectTransformUtility.WorldToScreenPoint(null, worldCenter);
            }
            return new Vector2(-100000f, -100000f); // never hovered
        }

        public float ZoneRadius(SlideHotspot hotspot)
        {
            // Half the label's own (layout-driven) width, in screen pixels — investigation labels
            // are wider than navigation labels, so each gets a zone matching its footprint.
            if (map.TryGetValue(hotspot, out HotspotRowItem item) && item != null)
                return item.Rect.rect.width * 0.5f * item.Rect.lossyScale.x;
            return 0f;
        }

        public void Tick(IReadOnlyList<SlideHotspot> hotspots, float proximity01, float dt)
        {
            foreach (var kv in map)
            {
                SlideHotspot hotspot = kv.Key;
                HotspotRowItem item = kv.Value;
                if (hotspot == null || item == null)
                    continue;
                // Ring tracks dwell (Select zone only); the label background tracks the highlight
                // so it also brightens on a Hover-zone hover; the secondary fill tracks the active
                // user's approach (hover → select) but only on the label currently being aimed at.
                item.SetProgress(hotspot.Dwell01);
                item.SetHighlight(hotspot.Highlight01);
                item.SetBackgroundFill(hotspot.IsHovered ? proximity01 : 0f);
            }
        }
    }
}
