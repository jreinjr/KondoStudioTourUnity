using UnityEngine;

namespace Kondo.Slideshow
{
    /// <summary>
    /// An element (text block, focus mask, ...) that fades in after its slide has
    /// finished fading in, with timing from the shared style or a local override.
    /// </summary>
    [RequireComponent(typeof(CanvasGroup))]
    public class SlideFadeInElement : MonoBehaviour
    {
        public SlideshowStyle style;
        public CanvasGroup group;

        [Header("Timing Override")]
        public bool overrideTiming;
        [Min(0f)] public float delaySeconds = 0.4f;
        [Min(0.01f)] public float fadeSeconds = 0.6f;

        public float Delay => overrideTiming || style == null ? delaySeconds : style.elementDefaultDelay;
        public float Duration => overrideTiming || style == null ? fadeSeconds : style.elementDefaultFadeSeconds;

        public virtual void SetHidden()
        {
            if (group != null)
                group.alpha = 0f;
        }

        public virtual void Play(MonoBehaviour host)
        {
            if (group != null)
                Fading.Fade(host, group, 1f, Duration, Delay);
        }

        public virtual void PlayOut(MonoBehaviour host)
        {
            if (group != null)
                Fading.Fade(host, group, 0f, Duration);
        }

#if UNITY_EDITOR
        protected virtual void OnValidate()
        {
            if (group == null)
                group = GetComponent<CanvasGroup>();
            else if (group.gameObject != gameObject)
                Debug.LogWarning($"[SlideFadeInElement] {name}: 'group' points to a CanvasGroup on a different " +
                    "object — its fade won't show. Assign this object's own CanvasGroup.", this);
        }
#endif
    }
}
