using System;
using UnityEngine;

namespace Kondo.Slideshow
{
    public enum TransitionKind
    {
        Video,
        FadeThroughBlack,
    }

    /// <summary>
    /// One edge of the slide graph: where to go and how to get there. Embedded in
    /// SlideHotspot and the Slide's auto-advance block so the controller handles a
    /// single shape regardless of what triggered the transition.
    /// </summary>
    [Serializable]
    public class SlideTransitionTarget
    {
        [Tooltip("Slide prefab to load when this transition fires.")]
        public Slide targetSlide;

        public TransitionKind kind = TransitionKind.Video;

        [Tooltip("Video path relative to StreamingAssets, e.g. Transitions/a_to_b.mp4. Used when kind is Video.")]
        public string transitionVideoPath;

        [Tooltip("Override the style's fade-through-black duration. Used when kind is FadeThroughBlack.")]
        public bool overrideFadeDuration;
        [Min(0.05f)] public float fadeThroughBlackSecondsOverride = 1f;

        public bool IsValid => targetSlide != null;

        public float FadeThroughBlackSeconds(SlideshowStyle style)
        {
            return overrideFadeDuration || style == null ? fadeThroughBlackSecondsOverride : style.fadeThroughBlackSeconds;
        }
    }
}
