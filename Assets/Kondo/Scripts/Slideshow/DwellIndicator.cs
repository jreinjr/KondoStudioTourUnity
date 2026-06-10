using UnityEngine;
using UnityEngine.UI;

namespace Kondo.Slideshow
{
    /// <summary>
    /// Circular radial-fill progress graphic — the shared visual for hotspot dwell
    /// feedback and auto-advance countdowns. Hidden while progress is zero.
    /// </summary>
    public class DwellIndicator : MonoBehaviour
    {
        public SlideshowStyle style;
        public CanvasGroup group;
        public Image fillImage;
        [Tooltip("Optional faint full ring drawn behind the fill.")]
        public Image ringBackground;

        float targetAlpha;

        void Awake()
        {
            ApplyStyle();
            HideImmediate();
        }

        void Update()
        {
            if (group != null && !Mathf.Approximately(group.alpha, targetAlpha))
            {
                float fadeSeconds = style != null ? style.indicatorFadeSeconds : 0.15f;
                group.alpha = Mathf.MoveTowards(group.alpha, targetAlpha, Time.deltaTime / fadeSeconds);
            }
        }

        /// <summary>Set fill 0..1; visibility follows automatically (hidden at 0).</summary>
        public void SetProgress(float t01)
        {
            t01 = Mathf.Clamp01(t01);
            if (fillImage != null)
                fillImage.fillAmount = t01;
            targetAlpha = t01 > 0f ? 1f : 0f;
        }

        public void HideImmediate()
        {
            targetAlpha = 0f;
            if (group != null)
                group.alpha = 0f;
            if (fillImage != null)
                fillImage.fillAmount = 0f;
        }

        public void ApplyStyle()
        {
            if (style == null)
                return;
            if (fillImage != null)
            {
                fillImage.type = Image.Type.Filled;
                fillImage.fillMethod = Image.FillMethod.Radial360;
                fillImage.fillOrigin = (int)Image.Origin360.Top;
                fillImage.fillClockwise = true;
                fillImage.color = style.indicatorColor;
                fillImage.raycastTarget = false;
            }
            if (ringBackground != null)
            {
                Color c = style.indicatorColor;
                c.a *= 0.25f;
                ringBackground.color = c;
                ringBackground.raycastTarget = false;
            }
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            if (group == null)
                group = GetComponent<CanvasGroup>();
            ApplyStyle();
        }
#endif
    }
}
