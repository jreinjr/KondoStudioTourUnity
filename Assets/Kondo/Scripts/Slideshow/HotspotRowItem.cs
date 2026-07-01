using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Kondo.Slideshow
{
    /// <summary>Direction the row label's secondary background fill grows as the user approaches select range.</summary>
    public enum RowFillDirection
    {
        LeftToRight,
        RightToLeft,
        BottomToTop,
    }

    /// <summary>
    /// One label in the bottom hotspot-selection row. Instantiated per hotspot by
    /// <see cref="HotspotRowView"/> from an editor-authored prefab: the prefab owns the
    /// appearance — the base <see cref="background"/> color (idle) and <see cref="hoverColor"/>,
    /// the secondary <see cref="fillBackground"/> (a proximity-driven fill; its color is authored
    /// on that image), the label text color, the item height (its RectTransform), and any extra
    /// decoration — while the row's HorizontalLayoutGroup positions and widths it via this item's
    /// <see cref="layoutElement"/> (navigation labels get a fixed width, investigation labels flex
    /// to fill). Font and font size still come from <see cref="SlideshowStyle"/>. This is what lets
    /// the navigation and investigation prefabs look different.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public class HotspotRowItem : MonoBehaviour
    {
        [Tooltip("This item's own RectTransform (the slot). Its authored height is the row height; its width is driven by the layout group.")]
        public RectTransform rectTransform;
        [Tooltip("Drives this item's width in the row's HorizontalLayoutGroup (fixed for navigation, flexible for investigation).")]
        public LayoutElement layoutElement;
        [Tooltip("Base label background; its authored color is the idle color, tinted toward Hover Color on highlight.")]
        public Image background;
        [Tooltip("Background color at full dwell/highlight (the idle color is the background Image's own authored color).")]
        public Color hoverColor = new Color(0.2f, 0.45f, 0.7f, 0.9f);
        [Tooltip("Secondary background fill layered over the base. Its fillAmount tracks the active user's " +
                 "hover→select proximity; its color is authored on this image. Use a Filled-type Image.")]
        public Image fillBackground;
        [Tooltip("Direction the secondary fill grows as the user approaches select range.")]
        public RowFillDirection fillDirection = RowFillDirection.LeftToRight;
        [Tooltip("Label text (the hotspot's display name).")]
        public TextMeshProUGUI label;
        [Tooltip("Radial dwell ring, shown on the right of the label.")]
        public DwellIndicator indicator;

        // The background's authored idle color, captured at Init so highlight can lerp back to it.
        Color idleColor = Color.white;

        public RectTransform Rect => rectTransform != null ? rectTransform : (rectTransform = (RectTransform)transform);

        void Awake() => ConfigureFillBackground();

        /// <summary>
        /// Set the label text and fit the height-derived layout (the text's right margin and the
        /// ring's size/offset). Colors and item height are authored on the prefab; slot width and
        /// position are driven by the row's layout group. Font/size come from the style.
        /// </summary>
        public void Init(SlideshowStyle style, string text)
        {
            float height = Rect.rect.height; // prefab-authored height

            ConfigureFillBackground();
            if (background != null)
            {
                idleColor = background.color; // prefab-authored idle color
                background.raycastTarget = false;
            }

            if (label != null)
            {
                label.text = text;
                if (style != null)
                {
                    label.fontSize = style.rowFontSize;
                    if (style.rowFont != null)
                        label.font = style.rowFont;
                }
                // Leave room for the ring on the right (the ring rides the right edge).
                var labelRect = (RectTransform)label.transform;
                labelRect.offsetMax = new Vector2(-height, labelRect.offsetMax.y);
                label.raycastTarget = false;
            }

            if (indicator != null)
            {
                var indRect = (RectTransform)indicator.transform;
                float s = height * 0.6f;
                indRect.sizeDelta = new Vector2(s, s);
                indRect.anchoredPosition = new Vector2(-height * 0.2f, 0f);
                if (indicator.style == null && style != null)
                    indicator.style = style;
                indicator.HideImmediate();
            }
        }

        /// <summary>Fixed-width slot (navigation labels): the layout group gives it exactly this width.</summary>
        public void SetFixedWidth(float width)
        {
            if (layoutElement == null)
                return;
            layoutElement.minWidth = width;
            layoutElement.preferredWidth = width;
            layoutElement.flexibleWidth = 0f;
        }

        /// <summary>Flexible slot (investigation labels): fills the space left over between the fixed navigation labels.</summary>
        public void SetFlexibleWidth(float minWidth)
        {
            if (layoutElement == null)
                return;
            layoutElement.minWidth = minWidth;
            layoutElement.preferredWidth = minWidth;
            layoutElement.flexibleWidth = 1f;
        }

        /// <summary>Drive the dwell ring (0..1; hidden at 0).</summary>
        public void SetProgress(float dwell01)
        {
            if (indicator != null)
                indicator.SetProgress(dwell01);
        }

        /// <summary>Tint the base background between the prefab's authored idle color and <see cref="hoverColor"/>.</summary>
        public void SetHighlight(float highlight01)
        {
            if (background != null)
                background.color = Color.Lerp(idleColor, hoverColor, highlight01);
        }

        /// <summary>Set the secondary background fill level (0..1) — the active user's hover→select proximity.</summary>
        public void SetBackgroundFill(float proximity01)
        {
            if (fillBackground != null)
                fillBackground.fillAmount = Mathf.Clamp01(proximity01);
        }

        /// <summary>Apply the fill method/origin from <see cref="fillDirection"/> to the secondary fill image.</summary>
        void ConfigureFillBackground()
        {
            if (fillBackground == null)
                return;
            fillBackground.type = Image.Type.Filled;
            switch (fillDirection)
            {
                case RowFillDirection.LeftToRight:
                    fillBackground.fillMethod = Image.FillMethod.Horizontal;
                    fillBackground.fillOrigin = (int)Image.OriginHorizontal.Left;
                    break;
                case RowFillDirection.RightToLeft:
                    fillBackground.fillMethod = Image.FillMethod.Horizontal;
                    fillBackground.fillOrigin = (int)Image.OriginHorizontal.Right;
                    break;
                case RowFillDirection.BottomToTop:
                    fillBackground.fillMethod = Image.FillMethod.Vertical;
                    fillBackground.fillOrigin = (int)Image.OriginVertical.Bottom;
                    break;
            }
            fillBackground.raycastTarget = false;
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            if (rectTransform == null)
                rectTransform = (RectTransform)transform;
            ConfigureFillBackground();
        }
#endif
    }
}
