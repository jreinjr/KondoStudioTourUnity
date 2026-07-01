using System.Collections;
using System.Collections.Generic;
using Kondo.Pointing;
using UnityEngine;
using UnityEngine.InputSystem;

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
        Crossfading,
        OverlayShowing,
    }

    /// <summary>
    /// How close the active user stands, gating hotspot interaction. None = too far (no
    /// highlight); Hover = close enough to highlight the aimed hotspot (no dwell/fire);
    /// Select = close enough to dwell and activate. Ordered None &lt; Hover &lt; Select.
    /// </summary>
    public enum InteractionZone
    {
        None,
        Hover,
        Select,
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
        [Tooltip("In-scene Slide instance the show starts on (must be a scene reference, NOT a prefab asset — " +
                 "the graph uses in-scene hotspot/auto-advance targets). The show drives the actual scene instances.")]
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

        [Header("Helper Text")]
        [Tooltip("Instructional line shown above the hotspot row (coaches the visitor to stand / step forward / proceed).")]
        public SlideshowHelperText helperText;

        // Dev fallback when no tracked user supplies depth (e.g. the Editor): arrow keys
        // cycle this simulated zone None→Hover→Select. Ignored once a real body is present.
        InteractionZone devZone = InteractionZone.None;

        Slide currentSlide;
        SlideTransitionTarget pendingTarget;
        SlideshowState state;
        float idleEnteredTime; // when the current slide became interactive (TimedDebounce guard)
        bool autoBackoutEngaged; // the active user has been within the back-out line since this idle began
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

            HideAllSlidesAtStartup();

            state = SlideshowState.FadingIn;
            currentSlide = ShowSlide(startSlidePrefab);
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

            UpdateCursorSuppression();

            if (state != SlideshowState.Idle || currentSlide == null)
                return;

            float dt = Time.deltaTime;
            UpdateDevZone();

            // Active interaction zone: from the tracked user's depth, else the dev arrow-key
            // override. Hover highlights the aimed hotspot; only Select dwells and fires.
            UserPointerManager.PointerState activeBody = null;
            UserPointerManager pm = pointers != null ? pointers.pointerManager : null;
            if (pm != null && pm.ActiveUserId >= 0)
                pm.States.TryGetValue(pm.ActiveUserId, out activeBody);
            bool hasBody = activeBody != null && activeBody.HasBody;
            InteractionZone zone;
            if (hasBody)
            {
                float z = activeBody.BodyPosition.z; // wall at negative Z, so smaller = closer
                zone = z <= pm.maxSelectZ ? InteractionZone.Select
                     : z <= pm.maxHoverZ ? InteractionZone.Hover
                     : InteractionZone.None;
            }
            else
            {
                zone = devZone;
            }
            bool highlightEnabled = zone != InteractionZone.None;
            bool dwellEnabled = zone == InteractionZone.Select;

            // Navigation guard: stop a cursor parked over the same spot from re-triggering the
            // freshly loaded slide. TimedDebounce holds dwell off for a window after the slide
            // became interactive; RequireReentry (handled per-hotspot in UpdateHover) holds dwell
            // off until the cursor has left the hotspot's zone once.
            bool requireRelease = style.navigationGuard == NavigationGuardMode.RequireReentry;
            if (style.navigationGuard == NavigationGuardMode.TimedDebounce &&
                Time.time - idleEnteredTime < style.navigationDebounceSeconds)
                dwellEnabled = false;

            var screenPoints = pointers != null ? pointers.ScreenPoints : null;

            SlideHotspot firedHotspot = null;
            SlideHotspot prepCandidate = null;
            float bestDwell = 0f;

            // Each pointer hovers at most one hotspot: the nearest point within radius.
            // An already-hovered hotspot keeps its hover out to radius × the exit
            // multiplier (hysteresis), so edge jitter doesn't reset the dwell.
            hovered.Clear();
            SlideHotspot skeletonHovered = null;
            SlideHotspot mouseHovered = null;
            float skeletonHoverDist = 0f;
            // In horizontal-only pointing the cursor's vertical position encodes depth (it rises as
            // the user approaches), so it never vertically coincides with a hotspot until it has
            // risen to the Select line. Match hover on horizontal alignment alone so a hotspot
            // highlights the moment the user is laterally lined up and starts to approach.
            bool horizontalHover = pm != null && pm.IsHorizontalPointing;
            if (screenPoints != null)
            {
                for (int i = 0; i < screenPoints.Count; i++)
                {
                    SlideHotspot nearest = null;
                    float nearestDist = float.MaxValue;
                    foreach (SlideHotspot hotspot in currentSlide.Hotspots)
                    {
                        if (!hotspot.IsInteractable)
                            continue; // blank row spacers are never hovered
                        float radius = activeSelector.ZoneRadius(hotspot)
                                     * (hotspot.IsHovered ? style.hotspotExitRadiusMultiplier : 1f);
                        Vector2 zonePoint = activeSelector.ZonePoint(hotspot);
                        float dist = horizontalHover
                            ? Mathf.Abs(screenPoints[i].x - zonePoint.x)
                            : Vector2.Distance(screenPoints[i], zonePoint);
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
                        else
                        {
                            mouseHovered = nearest;
                        }
                    }
                }
            }

            // What the active interaction is aiming at (the tracked user, else the dev mouse).
            SlideHotspot pointed = hasBody ? skeletonHovered : mouseHovered;

            // Only pull the cursor toward a hotspot when hovering actually counts (Hover/Select).
            ApplyHotspotMagnetism(highlightEnabled ? skeletonHovered : null, skeletonHoverDist);
            // Tell the active cursor which kind of hotspot it is over (drives the animated cursor).
            ApplyCursorHotspotKind(highlightEnabled ? pointed : null);

            foreach (SlideHotspot hotspot in currentSlide.Hotspots)
            {
                if (!hotspot.IsInteractable)
                    continue; // blank row spacers neither highlight nor dwell
                bool h = highlightEnabled && hovered.Contains(hotspot);
                if (hotspot.UpdateHover(h, dwellEnabled, requireRelease, dt) && firedHotspot == null)
                    firedHotspot = hotspot;

                if (hotspot.Dwell01 > bestDwell)
                {
                    bestDwell = hotspot.Dwell01;
                    prepCandidate = hotspot;
                }
            }

            // Active user's normalized depth (hover 0 → select 1), driving the row-label background fill.
            // In the dev/no-body case, approximate it from the simulated zone so it can be exercised.
            float proximity01 = hasBody
                ? pm.DepthProximity01(activeBody)
                : (devZone == InteractionZone.Select ? 1f : devZone == InteractionZone.Hover ? 0.5f : 0f);
            activeSelector.Tick(currentSlide.Hotspots, proximity01, dt);
            UpdateHelperText(zone, pointed);

            bool autoFired = currentSlide.TickAutoAdvance(dt);
            // Auto-advance slides mirror the overlay hold: they advance either when the countdown
            // expires (above) or when the engaged user steps back past the back-out line, so the
            // next visitor isn't stuck waiting out the full delay. The back-out is suppressed for the
            // first backoutMinSeconds; AutoAdvanceBackedOut still runs each frame to keep its
            // engagement latch current during that window.
            if (!autoFired && currentSlide.AutoAdvanceTarget != null)
            {
                bool backedOut = AutoAdvanceBackedOut();
                if (backedOut && Time.time - idleEnteredTime >= style.backoutMinSeconds)
                    autoFired = true;
            }

            // Pre-open the most likely transition video so its first frame sits ready
            // under the slide before the reveal. A dwelling hotspot wins; otherwise a
            // hover-zone aim warms its clip up before the visitor steps forward; otherwise
            // the auto countdown — or an engaged user on an auto-advance slide, whose
            // back-out could fire the advance well before the countdown window.
            SlideTransitionTarget prepTarget = null;
            if (prepCandidate != null)
                prepTarget = prepCandidate.action == HotspotAction.Transition ? prepCandidate.Target : null;
            else if (pointed != null)
                prepTarget = pointed.action == HotspotAction.Transition ? pointed.Target : null;
            else if (autoBackoutEngaged || currentSlide.AutoAdvanceTimeRemaining <= style.autoIndicatorWindowSeconds + 1f)
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
        /// Hide the pointer cursor(s) while they can't do anything, so they only fade in once the
        /// freshly-loaded slide is actually interactable: (1) all through a slide-to-slide transition
        /// (the cursor mustn't linger over the transition video / blackout), and (2) through the new
        /// slide's interaction-gating window after it reaches Idle — the navigation debounce for a
        /// hotspot slide, the back-out minimum for an auto-advance slide. Overlays keep their (flipped)
        /// cursor throughout, since the visitor uses it to step back and close them.
        /// </summary>
        void UpdateCursorSuppression()
        {
            UserPointerManager pm = pointers != null ? pointers.pointerManager : null;
            if (pm != null)
                pm.SuppressCursors = ShouldSuppressCursor();
        }

        bool ShouldSuppressCursor()
        {
            if (state == SlideshowState.OverlayShowing)
                return false;
            if (state != SlideshowState.Idle)
                return true; // mid slide-to-slide transition
            // Freshly loaded slide: stay hidden until its interaction gate opens, then fade in.
            return Time.time - idleEnteredTime < CursorInteractableDelaySeconds();
        }

        /// <summary>
        /// Seconds after a slide becomes Idle before its cursor can act (and so is shown): the earliest
        /// of the guard timers gating this slide — the auto-advance back-out minimum, and, for a hotspot
        /// slide under the timed-debounce guard, the navigation debounce. The cursor fades in as soon as
        /// it can do *something* (dwell a hotspot or back out), so a slide gated by both appears at the
        /// earlier mark. Both windows are measured from <see cref="idleEnteredTime"/>, matching the
        /// dwell/back-out gates in Update. Zero when nothing time-gates the slide.
        /// </summary>
        float CursorInteractableDelaySeconds()
        {
            if (currentSlide == null || style == null)
                return 0f;
            float delay = float.PositiveInfinity;
            if (currentSlide.AutoAdvanceTarget != null)
                delay = Mathf.Min(delay, style.backoutMinSeconds);
            if (style.navigationGuard == NavigationGuardMode.TimedDebounce &&
                currentSlide.Hotspots != null && currentSlide.Hotspots.Count > 0)
                delay = Mathf.Min(delay, style.navigationDebounceSeconds);
            return float.IsPositiveInfinity(delay) ? 0f : delay;
        }

        /// <summary>Dev fallback: arrow keys cycle the simulated zone None→Hover→Select (used only when no body supplies depth).</summary>
        void UpdateDevZone()
        {
            Keyboard kb = Keyboard.current;
            if (kb == null)
                return;
            if (kb.upArrowKey.wasPressedThisFrame)
                devZone = (InteractionZone)Mathf.Min((int)devZone + 1, (int)InteractionZone.Select);
            else if (kb.downArrowKey.wasPressedThisFrame)
                devZone = (InteractionZone)Mathf.Max((int)devZone - 1, (int)InteractionZone.None);
        }

        /// <summary>
        /// Coach the visitor: not aiming at a hotspot (or too far) → stand in front; aiming from
        /// the Hover zone → step forward to proceed; aiming from the Select zone → proceeding.
        /// </summary>
        void UpdateHelperText(InteractionZone zone, SlideHotspot pointed)
        {
            if (helperText == null)
                return;
            string msg;
            if (pointed == null || zone == InteractionZone.None)
                msg = style.helperIdleText;
            else if (zone == InteractionZone.Select)
                msg = string.Format(style.helperSelectFormat, pointed.ProceedLabel);
            else
                msg = string.Format(style.helperHoverFormat, pointed.ProceedLabel);
            helperText.SetMessage(msg);
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
        /// Report the hovered hotspot's kind (navigation/investigation) to the active user's
        /// cursor. This is the only place the slideshow's per-hotspot tag maps into the
        /// pointing-side <see cref="CursorHotspotKind"/>; the manager expires it if we stop
        /// refreshing it (e.g. during a transition), mirroring the magnetism channel.
        /// </summary>
        void ApplyCursorHotspotKind(SlideHotspot hotspot)
        {
            if (pointers == null || pointers.pointerManager == null)
                return;
            if (!pointers.pointerManager.States.TryGetValue(pointers.pointerManager.ActiveUserId, out var st))
                return;
            st.HoveredHotspotKind = hotspot == null
                ? CursorHotspotKind.None
                : hotspot.isInvestigation ? CursorHotspotKind.Investigation : CursorHotspotKind.Navigation;
            // Nav hotspots can point the beckon arrow right; investigation/none leave it at the default (left).
            st.HoveredArrowMirror = hotspot != null && !hotspot.isInvestigation && hotspot.arrowPointsRight;
            st.HotspotKindSetTime = Time.time;
        }

        /// <summary>
        /// In-slide overlay: hotspots fade away, the slide content zooms toward the
        /// hotspot's hover point while its mask/text fade on, hold, then everything reverses.
        /// </summary>
        IEnumerator OverlayRoutine(SlideHotspot hotspot)
        {
            Debug.Log($"[SlideshowController] Overlay fired: '{hotspot.name}' on '{currentSlide.name}' t={Time.time:F2}");
            float openTime = Time.time; // back-out is suppressed until backoutMinSeconds after this
            state = SlideshowState.OverlayShowing;
            Kondo.UI.BeckonGraphic.SetPointingDown(true); // flip the cursor's beckon to point down for the duration of the overlay
            activeSelector?.SetVisible(false);
            helperText?.SetVisible(false);
            Slide slide = currentSlide;
            slide.CancelAutoAdvance();

            // Zoom toward the hotspot's Hover Point (its `point`), not the bottom-row label.
            // Captured before anything scales.
            Vector3 zoomPointWorld = hotspot.PointWorld;
            foreach (SlideHotspot h in slide.Hotspots)
            {
                h.ResetDwell(snapAlpha: false);
                Fading.Fade(this, h.group, 0f, style.overlayHotspotsFadeSeconds);
            }
            slide.ZoomTowardWorldPoint(zoomPointWorld, hotspot.OverlayZoomScale, style.overlayZoomSeconds);
            yield return new WaitForSeconds(style.overlayHotspotsFadeSeconds);

            float slowestIn = 0f;
            foreach (SlideFadeInElement element in hotspot.overlayElements)
            {
                if (element == null)
                    continue;
                element.Play(this);
                slowestIn = Mathf.Max(slowestIn, element.Delay + element.Duration);
            }

            // Let the zoom-in and element fade-ins fully settle before the hold becomes
            // interruptible. Breaking out mid-fade-in would leave each element's still-pending
            // Play() fade coroutine (possibly still in its Delay) running after PlayOut() below,
            // stranding the element at full alpha — and the zoom-in is finished by now too, so
            // an early ZoomTo(1f) reverses cleanly from a settled scale.
            if (slowestIn > 0f)
                yield return new WaitForSeconds(slowestIn);

            // Hold the overlay open, but end early if the visitor steps back past the back-out line
            // (e.g. walks away) so the next person isn't stuck waiting out the full duration. The
            // early exit is suppressed for the first backoutMinSeconds so a brief step-back right
            // after opening can't immediately cancel it. The reverse below still plays in full.
            for (float held = 0f; held < hotspot.OverlayDuration; held += Time.deltaTime)
            {
                if (Time.time - openTime >= style.backoutMinSeconds && !ActiveUserWithinBackoutZone())
                {
                    Debug.Log($"[SlideshowController] Overlay '{hotspot.name}' ended early: active user backed out t={Time.time:F2}");
                    break;
                }
                yield return null;
            }

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

        /// <summary>
        /// True while the active tracked user is at or within the back-out line — the depth past
        /// which an open overlay or an auto-advancing slide backs out. Returns true when there is no
        /// tracked body driving the cursor (dev mouse / no sensor), since a mouse can't "step back"
        /// and overlays shouldn't self-cancel without a depth source. (Wall at negative Z, so a
        /// smaller z is closer than MaxBackoutZ.)
        /// </summary>
        bool ActiveUserWithinBackoutZone()
        {
            UserPointerManager pm = pointers != null ? pointers.pointerManager : null;
            if (pm == null || pm.ActiveUserId < 0)
                return true;
            if (!pm.States.TryGetValue(pm.ActiveUserId, out var st) || !st.HasBody)
                return true;
            return st.BodyPosition.z <= pm.MaxBackoutZ;
        }

        /// <summary>
        /// True on the frame the active user steps back out of an auto-advancing slide. Only a genuine
        /// step-back counts: the user must have first been seen within the back-out line during this
        /// idle period (latched in <see cref="autoBackoutEngaged"/>), so a visitor merely standing far
        /// away — or no tracked body at all — never fast-forwards the slide. Mirrors the overlay's
        /// back-out (which is always a genuine step-back, since overlays only open from the Select zone).
        /// </summary>
        bool AutoAdvanceBackedOut()
        {
            UserPointerManager pm = pointers != null ? pointers.pointerManager : null;
            if (pm == null || pm.ActiveUserId < 0)
                return false;
            if (!pm.States.TryGetValue(pm.ActiveUserId, out var st) || !st.HasBody)
                return false;
            if (st.BodyPosition.z <= pm.MaxBackoutZ)
            {
                autoBackoutEngaged = true; // the user has come within the back-out line
                return false;
            }
            return autoBackoutEngaged; // farther than the line, and they were engaged: back out
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
            helperText?.SetVisible(false);
            currentSlide.CancelAutoAdvance();

            if (target.kind == TransitionKind.Video)
                StartCoroutine(VideoTransition());
            else if (target.kind == TransitionKind.Crossfade)
                StartCoroutine(CrossfadeTransition(target.CrossfadeSeconds(style)));
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

            currentSlide.OnHidden();
            currentSlide.gameObject.SetActive(false);
            currentSlide = null;

            state = SlideshowState.PlayingVideo;
            bool videoDone = false;
            video.Play(() => videoDone = true);

            // Show the next slide now, invisible above the playing video, so its own
            // background video (if any) has the whole transition to open and prep.
            Slide incoming = ShowSlide(pendingTarget.targetSlide);

            while (!videoDone)
                yield return null;

            state = SlideshowState.FadingIn;
            currentSlide = incoming;
            yield return RevealRoutine(incoming);
            video.Close();
            EnterIdle();
        }

        /// <summary>
        /// Crossfade (cross-dissolve): the incoming slide fades in on top of the outgoing one,
        /// which stays fully opaque underneath so the midpoint never shows through to the black
        /// canvas below. No blackout. The outgoing slide is parked once the incoming is fully
        /// opaque; enter fades then play, matching the other transitions.
        /// </summary>
        IEnumerator CrossfadeTransition(float duration)
        {
            state = SlideshowState.Crossfading;
            Slide outgoing = currentSlide;

            // Bring the incoming slide up invisible, guaranteed above the outgoing one (both are
            // active children of slideCanvas during the dissolve, so sibling order decides depth),
            // and let its background video reach its first frame before the fade starts.
            Slide incoming = ShowSlide(pendingTarget.targetSlide);
            incoming.transform.SetAsLastSibling();
            yield return WaitForSlideReady(incoming);
            incoming.OnShown(); // background video runs as the slide dissolves in

            yield return FadeAndWait(incoming.Group, 1f, duration);

            if (outgoing != null)
            {
                outgoing.OnHidden();
                outgoing.gameObject.SetActive(false);
            }

            currentSlide = incoming;
            incoming.PlayEnterFades();
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
                currentSlide.OnHidden();
                currentSlide.gameObject.SetActive(false);
                currentSlide = null;
            }

            Slide incoming = ShowSlide(pendingTarget.targetSlide);
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

        /// <summary>
        /// Show an in-scene Slide instance. Slides are NOT instantiated — the show drives the
        /// actual scene objects (their hotspot targets are in-scene references that would dangle
        /// on a clone). Reparents under <see cref="slideCanvas"/> so the slide's own non-overriding
        /// canvas inherits the −10 sorting (below the blackout), activates it, and resets its state.
        /// The reparent guard makes this idempotent (a no-op once startup has parked every slide).
        /// </summary>
        Slide ShowSlide(Slide slide)
        {
            var rect = (RectTransform)slide.transform;
            if (rect.parent != slideCanvas)
                rect.SetParent(slideCanvas, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            slide.gameObject.SetActive(true);
            slide.ResetForEntry(); // sets alpha to 0, so the slide reactivates invisibly
            return slide;
        }

        /// <summary>
        /// Park every Slide instance in the scene (including unreachable or pre-disabled ones)
        /// under <see cref="slideCanvas"/> and deactivate it, so only the start slide shows once
        /// <see cref="Start"/> reactivates it. Find-based so new slides need no wiring.
        /// </summary>
        void HideAllSlidesAtStartup()
        {
            foreach (Slide slide in FindObjectsByType<Slide>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var rect = (RectTransform)slide.transform;
                if (rect.parent != slideCanvas)
                    rect.SetParent(slideCanvas, false);
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
                slide.gameObject.SetActive(false);
            }
        }

        void EnterIdle()
        {
            pendingTarget = null;
            state = SlideshowState.Idle;
            // Auto-advance slides are functional overlays (timed hold + step-back back-out), so their
            // beckon points down for the duration just like ShowOverlay; every other slide points up.
            Kondo.UI.BeckonGraphic.SetPointingDown(currentSlide.AutoAdvanceTarget != null);
            idleEnteredTime = Time.time; // start the TimedDebounce window for the new slide
            autoBackoutEngaged = false;  // re-arm the auto-advance back-out latch for the new slide
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

            // Only coach when there's something to choose.
            helperText?.SetVisible(currentSlide.Hotspots != null && currentSlide.Hotspots.Count > 0);
        }
    }
}
