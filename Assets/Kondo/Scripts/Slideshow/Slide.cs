using System;
using System.Collections;
using System.Collections.Generic;
using RenderHeads.Media.AVProVideo;
using UnityEngine;
using UnityEngine.UI;

namespace Kondo.Slideshow
{
    public enum SlideBackgroundKind
    {
        Image,
        VideoLoop,
        VideoOnce,
    }

    public enum AutoAdvanceMode
    {
        AfterDelay,
        OnBackgroundVideoEnd,
    }

    /// <summary>
    /// Root of every slide prefab. Owns the background (image or video), the
    /// hotspots, the fade-in elements (text, focus masks), and the optional
    /// auto-advance. The opaque background is what occludes the transition video
    /// on the canvas below until the controller fades this slide out.
    /// </summary>
    [RequireComponent(typeof(CanvasGroup))]
    public class Slide : MonoBehaviour
    {
        public SlideshowStyle style;
        public CanvasGroup canvasGroup;
        [Tooltip("Scaled by overlay hotspots: background + hotspots + masks. Text lives outside so it never zooms.")]
        public RectTransform zoomRoot;

        [Header("Background")]
        public SlideBackgroundKind backgroundKind = SlideBackgroundKind.Image;
        public Image backgroundImage;
        public DisplayUGUI backgroundVideoDisplay;
        public MediaPlayer backgroundPlayer;
        [Tooltip("Video path relative to StreamingAssets, e.g. Slides/atelier_loop.mp4. Used for video background kinds.")]
        public string backgroundVideoPath;

        [Header("Auto Advance")]
        [Tooltip("Transition without user input: after the global auto-advance delay, or when the one-shot background video ends.")]
        public bool autoAdvance;
        public AutoAdvanceMode autoAdvanceMode = AutoAdvanceMode.AfterDelay;
        public SlideTransitionTarget autoAdvanceTarget = new SlideTransitionTarget();
        [Tooltip("Optional countdown indicator placed somewhere on the slide; fills during the final window before the advance.")]
        public DwellIndicator autoAdvanceIndicator;

        /// <summary>Fired once when a VideoOnce background reaches its end (it holds the last frame).</summary>
        public event Action BackgroundVideoFinished;

        SlideHotspot[] hotspots;
        SlideFadeInElement[] fadeInElements;
        readonly HashSet<SlideFadeInElement> overlayOnlyElements = new HashSet<SlideFadeInElement>();
        bool backgroundReady;
        bool backgroundFinishedFired;
        Coroutine zoomRoutine;
        bool zoomWarned;
        bool autoArmed;
        bool autoFired;
        float autoElapsed;

        public IReadOnlyList<SlideHotspot> Hotspots => hotspots;
        public CanvasGroup Group => canvasGroup;
        public bool HasVideoBackground => backgroundKind != SlideBackgroundKind.Image;

        /// <summary>True once the slide can be revealed without showing a hole (bg video first frame uploaded).</summary>
        public bool IsReadyToShow => !HasVideoBackground || backgroundReady;

        /// <summary>VideoOnce: seconds of background video left to play. PositiveInfinity when unknown.</summary>
        public float BackgroundTimeRemaining
        {
            get
            {
                if (backgroundKind != SlideBackgroundKind.VideoOnce || backgroundPlayer == null ||
                    backgroundPlayer.Control == null || backgroundPlayer.Info == null)
                    return float.PositiveInfinity;
                double duration = backgroundPlayer.Info.GetDuration();
                if (duration <= 0.0 || double.IsInfinity(duration))
                    return float.PositiveInfinity;
                return Mathf.Max(0f, (float)(duration - backgroundPlayer.Control.GetCurrentTime()));
            }
        }

        void Awake()
        {
            hotspots = GetComponentsInChildren<SlideHotspot>(true);
            fadeInElements = GetComponentsInChildren<SlideFadeInElement>(true);

            // Elements owned by an overlay hotspot only appear when it triggers,
            // never on slide entry.
            foreach (SlideHotspot hotspot in hotspots)
            {
                if (hotspot.overlayElements == null)
                    continue;
                foreach (SlideFadeInElement element in hotspot.overlayElements)
                    if (element != null)
                        overlayOnlyElements.Add(element);
            }

            ApplyBackgroundKind();
            if (HasVideoBackground && backgroundPlayer != null)
            {
                backgroundPlayer.AutoOpen = false;
                backgroundPlayer.AutoStart = false;
                backgroundPlayer.Loop = backgroundKind == SlideBackgroundKind.VideoLoop;
                backgroundPlayer.Events.AddListener(OnMediaEvent);
                if (backgroundVideoDisplay != null)
                {
                    backgroundVideoDisplay.NoDefaultDisplay = true;
                    backgroundVideoDisplay.Player = backgroundPlayer;
                }
            }
        }

        /// <summary>Prepare the slide to be faded in: invisible, elements hidden, bg video opening paused.</summary>
        public void ResetForEntry()
        {
            canvasGroup.alpha = 0f;
            if (zoomRoot != null)
            {
                zoomRoot.pivot = new Vector2(0.5f, 0.5f);
                zoomRoot.offsetMin = Vector2.zero;
                zoomRoot.offsetMax = Vector2.zero;
                zoomRoot.localScale = Vector3.one;
            }
            foreach (SlideFadeInElement element in fadeInElements)
                element.SetHidden();
            foreach (SlideHotspot hotspot in hotspots)
            {
                hotspot.ResetDwell();
                hotspot.PositionIndicator(); // root rect is final by now; Awake ran too early
            }
            autoArmed = false;
            if (autoAdvanceIndicator != null)
                autoAdvanceIndicator.HideImmediate();

            if (HasVideoBackground && backgroundPlayer != null)
            {
                backgroundReady = false;
                backgroundFinishedFired = false;
                if (string.IsNullOrEmpty(backgroundVideoPath))
                    Debug.LogError($"[Slide] {name}: background kind is {backgroundKind} but no video path is set.", this);
                else
                    backgroundPlayer.OpenMedia(MediaPathType.RelativeToStreamingAssetsFolder, backgroundVideoPath, autoPlay: false);
            }
        }

        /// <summary>Called when the slide is (about to be) fully visible: start the background video.</summary>
        public void OnShown()
        {
            if (HasVideoBackground && backgroundPlayer != null)
                backgroundPlayer.Play();
        }

        /// <summary>
        /// Called as the slide is parked (deactivated) for reuse: release the background-video
        /// decoder so parked slides don't hold media open. <see cref="ResetForEntry"/> re-opens it
        /// on the next show. Call while the slide is still active, before SetActive(false).
        /// </summary>
        public void OnHidden()
        {
            if (HasVideoBackground && backgroundPlayer != null)
                backgroundPlayer.CloseMedia();
        }

        public void PlayEnterFades()
        {
            foreach (SlideFadeInElement element in fadeInElements)
                if (!overlayOnlyElements.Contains(element))
                    element.Play(this);
        }

        public SlideTransitionTarget AutoAdvanceTarget => autoAdvance ? autoAdvanceTarget : null;

        /// <summary>Seconds until the auto advance fires (PositiveInfinity when disabled or unknown).</summary>
        public float AutoAdvanceTimeRemaining
        {
            get
            {
                if (!autoArmed || autoFired)
                    return float.PositiveInfinity;
                return autoAdvanceMode == AutoAdvanceMode.AfterDelay
                    ? Mathf.Max(0f, AutoAdvanceDelaySeconds - autoElapsed)
                    : BackgroundTimeRemaining;
            }
        }

        // Auto-advance slides act as functional overlays, so their timed hold shares the overlay
        // duration rather than a separate value.
        float AutoAdvanceDelaySeconds => style != null ? style.overlayDurationSeconds : 6f;

        /// <summary>Arm the countdown. Called by the controller when the slide reaches Idle.</summary>
        public void BeginAutoAdvance()
        {
            autoArmed = autoAdvance && autoAdvanceTarget != null && autoAdvanceTarget.targetSlide != null;
            autoFired = false;
            autoElapsed = 0f;
            if (autoAdvance && !autoArmed)
                Debug.LogError($"[Slide] {name}: autoAdvance is on but no target slide is set.", this);
            if (autoAdvanceIndicator != null)
                autoAdvanceIndicator.HideImmediate();
        }

        public void CancelAutoAdvance() => autoArmed = false;

        /// <summary>Returns true exactly once, on the frame the auto advance fires.</summary>
        public bool TickAutoAdvance(float dt)
        {
            if (!autoArmed || autoFired)
                return false;
            autoElapsed += dt;

            if (autoAdvanceIndicator != null && style != null)
            {
                float window = style.autoIndicatorWindowSeconds;
                float remaining = AutoAdvanceTimeRemaining;
                if (window > 0f && !float.IsPositiveInfinity(remaining))
                    autoAdvanceIndicator.SetProgress(Mathf.Clamp01(1f - remaining / window)); // hidden until remaining < window
            }

            bool shouldFire = autoAdvanceMode == AutoAdvanceMode.AfterDelay
                ? autoElapsed >= AutoAdvanceDelaySeconds
                : backgroundFinishedFired;
            if (shouldFire)
                autoFired = true;
            return shouldFire;
        }

        /// <summary>Zoom the slide content (zoom root) toward a world-space point (overlay hotspot zoom).</summary>
        public void ZoomTowardWorldPoint(Vector3 world, float scale, float seconds)
        {
            if (!HasZoomRoot())
                return;

            var corners = new Vector3[4];
            zoomRoot.GetWorldCorners(corners); // 0 BL, 2 TR
            var pivot = new Vector2(
                Mathf.InverseLerp(corners[0].x, corners[2].x, world.x),
                Mathf.InverseLerp(corners[0].y, corners[2].y, world.y));

            // Changing the pivot of a stretched rect shifts its anchoredPosition;
            // re-zeroing the offsets keeps it full-stretch. Scaling then happens
            // about the pivot, i.e. toward the hotspot point.
            zoomRoot.pivot = pivot;
            zoomRoot.offsetMin = Vector2.zero;
            zoomRoot.offsetMax = Vector2.zero;
            ZoomTo(scale, seconds);
        }

        /// <summary>Scale the zoom root about its current pivot (use scale 1 to zoom back out).</summary>
        public void ZoomTo(float scale, float seconds)
        {
            if (!HasZoomRoot())
                return;
            if (zoomRoutine != null)
                StopCoroutine(zoomRoutine);
            zoomRoutine = StartCoroutine(ZoomRoutine(scale, seconds));
        }

        bool HasZoomRoot()
        {
            if (zoomRoot != null)
                return true;
            if (!zoomWarned)
                Debug.LogWarning($"[Slide] {name}: no zoomRoot assigned — overlay zoom skipped. Run 'Kondo/Upgrade Slide Prefabs'.", this);
            zoomWarned = true;
            return false;
        }

        IEnumerator ZoomRoutine(float scale, float seconds)
        {
            float from = zoomRoot.localScale.x;
            float current = from;
            float rate = Mathf.Abs(scale - from) / Mathf.Max(0.01f, seconds);
            while (!Mathf.Approximately(current, scale))
            {
                current = Mathf.MoveTowards(current, scale, rate * Time.deltaTime);
                zoomRoot.localScale = new Vector3(current, current, 1f);
                yield return null;
            }
            zoomRoutine = null;
        }

        void OnMediaEvent(MediaPlayer mp, MediaPlayerEvent.EventType evt, ErrorCode error)
        {
            Debug.Log($"[Slide] {name}: {evt} '{backgroundVideoPath}' t={Time.time:F2}");
            switch (evt)
            {
                case MediaPlayerEvent.EventType.FirstFrameReady:
                    backgroundReady = true;
                    break;
                case MediaPlayerEvent.EventType.FinishedPlaying:
                    if (backgroundKind == SlideBackgroundKind.VideoOnce && !backgroundFinishedFired)
                    {
                        backgroundFinishedFired = true;
                        backgroundPlayer.Pause(); // hold the last frame
                        BackgroundVideoFinished?.Invoke();
                    }
                    break;
                case MediaPlayerEvent.EventType.Error:
                    Debug.LogError($"[Slide] {name}: background video error '{error}' for '{backgroundVideoPath}'.", this);
                    break;
            }
        }

        void ApplyBackgroundKind()
        {
            bool video = HasVideoBackground;
            if (backgroundImage != null)
                backgroundImage.enabled = !video;
            if (backgroundVideoDisplay != null)
                backgroundVideoDisplay.enabled = video;
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            if (canvasGroup == null)
                canvasGroup = GetComponent<CanvasGroup>();
            ApplyBackgroundKind();
            if (backgroundImage != null)
                backgroundImage.raycastTarget = false;
            if (backgroundVideoDisplay != null)
                backgroundVideoDisplay.raycastTarget = false;
            if (autoAdvance && autoAdvanceMode == AutoAdvanceMode.OnBackgroundVideoEnd &&
                backgroundKind != SlideBackgroundKind.VideoOnce)
                Debug.LogWarning($"[Slide] {name}: auto-advance mode is OnBackgroundVideoEnd but the background kind is {backgroundKind} — it will never fire.", this);
        }
#endif
    }
}
