using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Kondo.Slideshow
{
    public enum SlideshowState
    {
        Idle,
        AwaitingVideo,
        FadingOut,
        PlayingVideo,
        FadingIn,
        BlackingOut,
        BlackHold,
        BlackingIn,
        OverlayShowing,
    }

    /// <summary>
    /// Drives the show: hover/dwell and auto-advance while Idle, then one of two
    /// transition paths — seamless video (fade out over the pre-paused first frame,
    /// play, fade the next slide in over the held last frame) or fade-through-black.
    /// Slides are prefabs instantiated into the slide canvas; the graph is whatever
    /// is reachable from the start slide.
    /// </summary>
    public class SlideshowController : MonoBehaviour
    {
        public SlideshowStyle style;
        [Tooltip("Slide prefab the show starts on. The graph is reachable from here via hotspot/auto-advance targets.")]
        public Slide startSlidePrefab;
        public RectTransform slideCanvas;
        [Tooltip("Full-screen black overlay used by fade-through-black transitions.")]
        public CanvasGroup blackout;
        public SlideshowPointerProvider pointers;
        public TransitionVideoPlayer video;

        [Header("Hotspot Selection")]
        [Tooltip("How hotspots are selected. Switchable live in Play mode.")]
        public HotspotSelectionMode hotspotMode = HotspotSelectionMode.InImage;
        [Tooltip("Bottom-row selection UI (required for the BottomRow mode).")]
        public HotspotRowView hotspotRow;

        Slide currentSlide;
        SlideTransitionTarget pendingTarget;
        SlideshowState state;
        readonly HashSet<SlideHotspot> hovered = new HashSet<SlideHotspot>();

        readonly InImageHotspotSelector inImageSelector = new InImageHotspotSelector();
        IHotspotSelector activeSelector;
        HotspotSelectionMode lastHotspotMode;
        Slide selectorSlide;

        public SlideshowState State => state;
        public Slide CurrentSlide => currentSlide;

        void Start()
        {
            if (startSlidePrefab == null || style == null || slideCanvas == null || video == null)
            {
                Debug.LogError("[SlideshowController] Missing references (start slide / style / slide canvas / video).", this);
                enabled = false;
                return;
            }
            if (blackout != null)
                blackout.alpha = 0f;

            lastHotspotMode = hotspotMode;
            activeSelector = ResolveSelector();

            state = SlideshowState.FadingIn;
            currentSlide = SpawnSlide(startSlidePrefab);
            StartCoroutine(InitialReveal());
        }

        IHotspotSelector ResolveSelector()
        {
            if (hotspotMode == HotspotSelectionMode.BottomRow)
            {
                if (hotspotRow != null)
                    return hotspotRow;
                Debug.LogWarning("[SlideshowController] hotspotMode is BottomRow but no HotspotRowView is assigned — using in-image selection.", this);
            }
            return inImageSelector;
        }

        /// <summary>(Re)point the active selector at the current slide, matching the show state.</summary>
        void ActivateSelectorForCurrentSlide()
        {
            if (activeSelector == null)
                return;
            if (currentSlide != null && state == SlideshowState.Idle)
            {
                activeSelector.OnSlideChanged(currentSlide);
                selectorSlide = currentSlide;
                activeSelector.SetVisible(true);
            }
            else
            {
                activeSelector.SetVisible(false);
            }
        }

        IEnumerator InitialReveal()
        {
            yield return RevealRoutine(currentSlide);
            EnterIdle();
        }

        void Update()
        {
            if (hotspotMode != lastHotspotMode)
            {
                lastHotspotMode = hotspotMode;
                IHotspotSelector previous = activeSelector;
                activeSelector = ResolveSelector();
                if (previous != null && previous != activeSelector)
                    previous.SetVisible(false);
                selectorSlide = null;
                ActivateSelectorForCurrentSlide();
            }

            if (state != SlideshowState.Idle || currentSlide == null)
                return;

            float dt = Time.deltaTime;
            var screenPoints = pointers != null ? pointers.ScreenPoints : null;

            SlideHotspot firedHotspot = null;
            SlideHotspot prepCandidate = null;
            float bestDwell = 0f;

            // Each pointer hovers at most one hotspot: the nearest point within radius.
            // An already-hovered hotspot keeps its hover out to radius × the exit
            // multiplier (hysteresis), so edge jitter doesn't reset the dwell.
            hovered.Clear();
            SlideHotspot skeletonHovered = null;
            float skeletonHoverDist = 0f;
            if (screenPoints != null)
            {
                for (int i = 0; i < screenPoints.Count; i++)
                {
                    SlideHotspot nearest = null;
                    float nearestDist = float.MaxValue;
                    foreach (SlideHotspot hotspot in currentSlide.Hotspots)
                    {
                        float radius = activeSelector.ZoneRadius(hotspot)
                                     * (hotspot.IsHovered ? style.hotspotExitRadiusMultiplier : 1f);
                        float dist = Vector2.Distance(screenPoints[i], activeSelector.ZonePoint(hotspot));
                        if (dist <= radius && dist < nearestDist)
                        {
                            nearestDist = dist;
                            nearest = hotspot;
                        }
                    }
                    if (nearest != null)
                    {
                        hovered.Add(nearest);
                        if (pointers != null && i == pointers.SkeletonPointIndex)
                        {
                            skeletonHovered = nearest;
                            skeletonHoverDist = nearestDist;
                        }
                    }
                }
            }

            ApplyHotspotMagnetism(skeletonHovered, skeletonHoverDist);

            foreach (SlideHotspot hotspot in currentSlide.Hotspots)
            {
                if (hotspot.UpdateHover(hovered.Contains(hotspot), dt) && firedHotspot == null)
                    firedHotspot = hotspot;

                if (hotspot.Dwell01 > bestDwell)
                {
                    bestDwell = hotspot.Dwell01;
                    prepCandidate = hotspot;
                }
            }

            activeSelector.Tick(currentSlide.Hotspots, dt);

            bool autoFired = currentSlide.TickAutoAdvance(dt);

            // Pre-open the most likely transition video so its first frame sits ready
            // under the slide before the reveal. Hover beats the auto countdown.
            SlideTransitionTarget prepTarget = null;
            if (prepCandidate != null)
                prepTarget = prepCandidate.action == HotspotAction.Transition ? prepCandidate.Target : null;
            else if (currentSlide.AutoAdvanceTimeRemaining <= style.autoIndicatorWindowSeconds + 1f)
                prepTarget = currentSlide.AutoAdvanceTarget;
            if (prepTarget != null && prepTarget.kind == TransitionKind.Video &&
                !string.IsNullOrEmpty(prepTarget.transitionVideoPath))
                video.Prepare(prepTarget.transitionVideoPath);

            if (firedHotspot != null)
            {
                if (firedHotspot.action == HotspotAction.ShowOverlay)
                    StartCoroutine(OverlayRoutine(firedHotspot));
                else
                    BeginTransition(firedHotspot.Target);
            }
            else if (autoFired)
            {
                BeginTransition(currentSlide.AutoAdvanceTarget);
            }
        }

        /// <summary>
        /// Display-only cursor pull toward the hovered hotspot's center for the active
        /// skeleton user. Hit-testing never sees this (stickiness is hysteresis's job);
        /// the pull just makes the cursor visibly settle onto the target. The weight
        /// expires in UserPointerManager if we stop refreshing it (e.g. on transition).
        /// </summary>
        void ApplyHotspotMagnetism(SlideHotspot hotspot, float distToCenter)
        {
            if (hotspot == null || style.hotspotMagnetStrength <= 0f ||
                pointers == null || pointers.pointerManager == null)
                return;
            if (!pointers.pointerManager.States.TryGetValue(pointers.pointerManager.ActiveUserId, out var st))
                return;

            // Proximity fades the pull in toward the center and to zero at the enter
            // radius, so there is no tug at the hysteresis annulus.
            float proximity = 1f - Mathf.Clamp01(distToCenter / Mathf.Max(activeSelector.ZoneRadius(hotspot), 1e-3f));
            Vector2 centerPx = activeSelector.ZonePoint(hotspot);
            st.MagnetUv = new Vector2(centerPx.x / Screen.width, centerPx.y / Screen.height);
            st.MagnetWeight = style.hotspotMagnetStrength * proximity;
            st.MagnetSetTime = Time.time;
        }

        /// <summary>
        /// In-slide overlay: hotspots fade away, the slide content zooms toward the
        /// hotspot point while its mask/text fade on, hold, then everything reverses.
        /// </summary>
        IEnumerator OverlayRoutine(SlideHotspot hotspot)
        {
            Debug.Log($"[SlideshowController] Overlay fired: '{hotspot.name}' on '{currentSlide.name}' t={Time.time:F2}");
            state = SlideshowState.OverlayShowing;
            activeSelector?.SetVisible(false);
            Slide slide = currentSlide;
            slide.CancelAutoAdvance();

            Vector3 zoomPointWorld = hotspot.PointWorld; // capture before anything scales
            foreach (SlideHotspot h in slide.Hotspots)
            {
                h.ResetDwell(snapAlpha: false);
                Fading.Fade(this, h.group, 0f, style.overlayHotspotsFadeSeconds);
            }
            slide.ZoomTowardWorldPoint(zoomPointWorld, style.overlayZoomScale, style.overlayZoomSeconds);
            yield return new WaitForSeconds(style.overlayHotspotsFadeSeconds);

            float slowestIn = 0f;
            foreach (SlideFadeInElement element in hotspot.overlayElements)
            {
                if (element == null)
                    continue;
                element.Play(this);
                slowestIn = Mathf.Max(slowestIn, element.Delay + element.Duration);
            }
            yield return new WaitForSeconds(slowestIn + hotspot.OverlayDuration);

            float slowestOut = 0f;
            foreach (SlideFadeInElement element in hotspot.overlayElements)
            {
                if (element == null)
                    continue;
                element.PlayOut(this);
                slowestOut = Mathf.Max(slowestOut, element.Duration);
            }
            slide.ZoomTo(1f, style.overlayZoomSeconds);
            yield return new WaitForSeconds(Mathf.Max(slowestOut, style.overlayZoomSeconds));

            foreach (SlideHotspot h in slide.Hotspots)
                Fading.Fade(this, h.group, style.hotspotIdleAlpha, style.overlayHotspotsFadeSeconds);
            yield return new WaitForSeconds(style.overlayHotspotsFadeSeconds);

            EnterIdle();
        }

        void BeginTransition(SlideTransitionTarget target)
        {
            if (target == null || target.targetSlide == null)
            {
                Debug.LogError($"[SlideshowController] Transition fired on '{currentSlide.name}' with no target slide — ignoring.", this);
                return;
            }

            Debug.Log($"[SlideshowController] {target.kind} transition: '{currentSlide.name}' → '{target.targetSlide.name}' t={Time.time:F2}");
            pendingTarget = target;
            activeSelector?.SetVisible(false);
            currentSlide.CancelAutoAdvance();

            if (target.kind == TransitionKind.Video)
                StartCoroutine(VideoTransition());
            else
                StartCoroutine(BlackTransition(target.FadeThroughBlackSeconds(style)));
        }

        IEnumerator VideoTransition()
        {
            state = SlideshowState.AwaitingVideo;
            video.Prepare(pendingTarget.transitionVideoPath);

            float deadline = Time.time + style.videoPrepTimeoutSeconds;
            while (!video.IsFirstFrameReady && !video.HasError && Time.time < deadline)
                yield return null;

            if (!video.IsFirstFrameReady)
            {
                Debug.LogWarning($"[SlideshowController] Transition video '{pendingTarget.transitionVideoPath}' not ready " +
                                 $"(error={video.HasError}) — falling back to fade-through-black.", this);
                video.Close();
                yield return BlackTransitionRoutine(style.fadeThroughBlackSeconds);
                yield break;
            }

            state = SlideshowState.FadingOut;
            yield return FadeAndWait(currentSlide.Group, 0f, style.slideFadeOutSeconds);

            Destroy(currentSlide.gameObject);
            currentSlide = null;

            state = SlideshowState.PlayingVideo;
            bool videoDone = false;
            video.Play(() => videoDone = true);

            // Build the next slide now, invisible above the playing video, so its own
            // background video (if any) has the whole transition to open and prep.
            Slide incoming = SpawnSlide(pendingTarget.targetSlide);

            while (!videoDone)
                yield return null;

            state = SlideshowState.FadingIn;
            currentSlide = incoming;
            yield return RevealRoutine(incoming);
            video.Close();
            EnterIdle();
        }

        IEnumerator BlackTransition(float duration)
        {
            yield return BlackTransitionRoutine(duration);
        }

        IEnumerator BlackTransitionRoutine(float duration)
        {
            state = SlideshowState.BlackingOut;
            yield return FadeAndWait(blackout, 1f, duration * 0.5f);

            if (currentSlide != null)
            {
                Destroy(currentSlide.gameObject);
                currentSlide = null;
            }

            Slide incoming = SpawnSlide(pendingTarget.targetSlide);
            incoming.Group.alpha = 1f; // hidden under black; enter elements stay hidden

            state = SlideshowState.BlackHold;
            yield return WaitForSlideReady(incoming);

            state = SlideshowState.BlackingIn;
            incoming.OnShown(); // background video runs as the black lifts
            yield return FadeAndWait(blackout, 0f, duration * 0.5f);

            currentSlide = incoming;
            incoming.PlayEnterFades();
            EnterIdle();
        }

        /// <summary>Wait for bg-video readiness, fade the slide in, then start its video and enter fades.</summary>
        IEnumerator RevealRoutine(Slide slide)
        {
            yield return WaitForSlideReady(slide);
            yield return FadeAndWait(slide.Group, 1f, style.slideFadeInSeconds);
            slide.OnShown();
            slide.PlayEnterFades();
        }

        IEnumerator WaitForSlideReady(Slide slide)
        {
            float deadline = Time.time + style.videoPrepTimeoutSeconds;
            while (!slide.IsReadyToShow && Time.time < deadline)
                yield return null;
            if (!slide.IsReadyToShow)
                Debug.LogWarning($"[SlideshowController] Slide '{slide.name}' background not ready in time — revealing anyway.", this);
        }

        IEnumerator FadeAndWait(CanvasGroup group, float to, float duration)
        {
            if (group == null)
                yield break;
            bool done = false;
            Fading.Fade(this, group, to, duration, 0f, () => done = true);
            while (!done)
                yield return null;
        }

        Slide SpawnSlide(Slide prefab)
        {
            Slide slide = Instantiate(prefab, slideCanvas);
            var rect = (RectTransform)slide.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            slide.ResetForEntry();
            return slide;
        }

        void EnterIdle()
        {
            pendingTarget = null;
            state = SlideshowState.Idle;
            currentSlide.BeginAutoAdvance();

            if (activeSelector != null)
            {
                if (currentSlide != selectorSlide)
                {
                    activeSelector.OnSlideChanged(currentSlide);
                    selectorSlide = currentSlide;
                }
                activeSelector.SetVisible(true);
            }
        }
    }
}
