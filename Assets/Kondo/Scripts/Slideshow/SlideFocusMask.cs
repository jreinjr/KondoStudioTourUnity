using UnityEngine;
using UnityEngine.UI;

namespace Kondo.Slideshow
{
    /// <summary>
    /// Full-screen dimmer with the highlight hole baked into the authored sprite's
    /// alpha. Assign the per-slide cutout PNG on the Image; tint comes from the style.
    /// </summary>
    [RequireComponent(typeof(Image))]
    public class SlideFocusMask : MonoBehaviour
    {
        public SlideshowStyle style;
        public Image image;

        void Awake() => ApplyStyle();

        public void ApplyStyle()
        {
            if (image == null)
                image = GetComponent<Image>();
            image.raycastTarget = false;
            if (style != null)
                image.color = style.maskTint;

            var rect = (RectTransform)transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

#if UNITY_EDITOR
        void OnValidate() => ApplyStyle();
#endif
    }
}
