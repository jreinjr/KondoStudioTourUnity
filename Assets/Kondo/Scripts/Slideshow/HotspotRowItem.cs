using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Kondo.Slideshow
{
    /// <summary>
    /// One label in the bottom hotspot-selection row. Instantiated per hotspot by
    /// <see cref="HotspotRowView"/> from an editor-authored prefab: the prefab owns the
    /// appearance (background, font, the nested dwell ring, any extra decoration) while the
    /// view positions the slot and binds the hotspot. Base colors/font come from
    /// <see cref="SlideshowStyle"/> but the prefab can be customized freely.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public class HotspotRowItem : MonoBehaviour
    {
        [Tooltip("This item's own RectTransform (the slot the view positions).")]
        public RectTransform rectTransform;
        [Tooltip("Label background; tinted between the style's idle and hover colors on highlight.")]
        public Image background;
        [Tooltip("Label text (the hotspot's display name).")]
        public TextMeshProUGUI label;
        [Tooltip("Radial dwell ring, shown on the right of the label.")]
        public DwellIndicator indicator;

        public RectTransform Rect => rectTransform != null ? rectTransform : (rectTransform = (RectTransform)transform);

        /// <summary>
        /// Position this item as a bottom-left-anchored slot, set its label text, and apply the
        /// shared style. Layout that derives from the row height (the text's right margin and the
        /// ring's size/offset) is fitted here so the prefab stays resolution-independent.
        /// </summary>
        public void Init(SlideshowStyle style, string text, Vector2 anchoredPos, Vector2 size)
        {
            RectTransform rect = Rect;
            rect.anchorMin = Vector2.zero; // bottom-left of the container
            rect.anchorMax = Vector2.zero;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = anchoredPos;

            if (background != null)
            {
                if (style != null)
                    background.color = style.rowLabelBg;
                background.raycastTarget = false;
            }

            if (label != null)
            {
                label.text = text;
                if (style != null)
                {
                    label.color = style.rowLabelColor;
                    label.fontSize = style.rowFontSize;
                    if (style.rowFont != null)
                        label.font = style.rowFont;
                }
                // Leave room for the ring on the right (the ring rides the right edge).
                var labelRect = (RectTransform)label.transform;
                labelRect.offsetMax = new Vector2(-size.y, labelRect.offsetMax.y);
                label.raycastTarget = false;
            }

            if (indicator != null)
            {
                var indRect = (RectTransform)indicator.transform;
                float s = size.y * 0.6f;
                indRect.sizeDelta = new Vector2(s, s);
                indRect.anchoredPosition = new Vector2(-size.y * 0.2f, 0f);
                if (indicator.style == null && style != null)
                    indicator.style = style;
                indicator.HideImmediate();
            }
        }

        /// <summary>Drive the dwell ring (0..1; hidden at 0).</summary>
        public void SetProgress(float dwell01)
        {
            if (indicator != null)
                indicator.SetProgress(dwell01);
        }

        /// <summary>Tint the background between the style's idle and hover colors.</summary>
        public void SetHighlight(SlideshowStyle style, float highlight01)
        {
            if (background != null && style != null)
                background.color = Color.Lerp(style.rowLabelBg, style.rowLabelHoverBg, highlight01);
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            if (rectTransform == null)
                rectTransform = (RectTransform)transform;
        }
#endif
    }
}
