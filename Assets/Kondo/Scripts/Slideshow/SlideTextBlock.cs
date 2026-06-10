using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Kondo.Slideshow
{
    /// <summary>
    /// Text with a legibility background. All styling (font, size, colors, padding)
    /// is pulled from the shared style asset; the designer only sizes the rect and
    /// writes the copy.
    /// </summary>
    public class SlideTextBlock : MonoBehaviour
    {
        public SlideshowStyle style;
        public TMP_Text text;
        public Image background;

        void Awake() => ApplyStyle();

        public void ApplyStyle()
        {
            if (style == null)
                return;
            if (text != null)
            {
                if (style.font != null)
                    text.font = style.font;
                text.fontSize = style.fontSize;
                text.color = style.textColor;
                text.raycastTarget = false;
                // Text is a full-stretch child of the background; padding via offsets.
                RectTransform rect = text.rectTransform;
                rect.offsetMin = style.textBackgroundPadding;
                rect.offsetMax = -style.textBackgroundPadding;
            }
            if (background != null)
            {
                background.color = style.textBackgroundColor;
                background.raycastTarget = false;
            }
        }

#if UNITY_EDITOR
        void OnValidate() => ApplyStyle();
#endif
    }
}
