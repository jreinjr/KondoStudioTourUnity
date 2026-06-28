using TMPro;
using UnityEngine;

namespace Kondo.Slideshow
{
    /// <summary>
    /// A single line of instructional text sitting directly above the bottom hotspot row
    /// (same place whether or not the row is shown). The <see cref="SlideshowController"/>
    /// owns the message and visibility; this component just applies the shared style and
    /// eases its <see cref="CanvasGroup"/> toward the target alpha, mirroring
    /// <see cref="HotspotRowView"/>'s fade.
    /// </summary>
    public class SlideshowHelperText : MonoBehaviour
    {
        public SlideshowStyle style;
        [Tooltip("Drives the helper text's show/hide fade.")]
        public CanvasGroup group;
        public TextMeshProUGUI text;

        float targetAlpha;

        void Awake()
        {
            if (group != null)
                group.alpha = 0f;
            ApplyStyle();
        }

        void Update()
        {
            if (group == null)
                return;
            float fade = style != null ? style.helperFadeSeconds : 0.25f;
            if (!Mathf.Approximately(group.alpha, targetAlpha))
                group.alpha = Mathf.MoveTowards(group.alpha, targetAlpha, Time.deltaTime / Mathf.Max(fade, 1e-3f));
        }

        public void SetVisible(bool visible) => targetAlpha = visible ? 1f : 0f;

        public void SetMessage(string message)
        {
            if (text != null && text.text != message)
                text.text = message;
        }

        public void ApplyStyle()
        {
            if (text == null || style == null)
                return;
            text.color = style.helperColor;
            text.fontSize = style.helperFontSize;
            text.alignment = TextAlignmentOptions.Center;
            text.enableAutoSizing = false;
            text.raycastTarget = false;
            if (style.helperFont != null)
                text.font = style.helperFont;
        }

#if UNITY_EDITOR
        void OnValidate() => ApplyStyle();
#endif
    }
}
