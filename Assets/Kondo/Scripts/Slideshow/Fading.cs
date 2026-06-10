using System;
using System.Collections;
using UnityEngine;

namespace Kondo.Slideshow
{
    /// <summary>CanvasGroup alpha fades as coroutines, shared by the slideshow components.</summary>
    public static class Fading
    {
        /// <summary>
        /// Fade a CanvasGroup toward <paramref name="to"/> at the full-range rate 1/duration,
        /// optionally after a delay. The coroutine runs on <paramref name="host"/>.
        /// </summary>
        public static Coroutine Fade(MonoBehaviour host, CanvasGroup group, float to, float duration,
            float delay = 0f, Action onComplete = null)
        {
            return host.StartCoroutine(FadeRoutine(group, to, duration, delay, onComplete));
        }

        static IEnumerator FadeRoutine(CanvasGroup group, float to, float duration, float delay, Action onComplete)
        {
            if (delay > 0f)
                yield return new WaitForSeconds(delay);

            if (duration > 0f)
            {
                while (group != null && !Mathf.Approximately(group.alpha, to))
                {
                    group.alpha = Mathf.MoveTowards(group.alpha, to, Time.deltaTime / duration);
                    yield return null;
                }
            }

            if (group != null)
                group.alpha = to;
            onComplete?.Invoke();
        }
    }
}
