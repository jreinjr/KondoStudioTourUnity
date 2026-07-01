using System.Collections.Generic;
using UnityEngine;
using Kondo.Core;
using Kondo.UI;
using NuitrackSDK;

namespace Kondo.Pointing
{
    /// <summary>
    /// Orchestrates the pointing system: enumerates Nuitrack users, runs one
    /// <see cref="PointingArmSolver"/> per user, applies the screen-space cursor filter,
    /// selects the active user (centermost pointing skeleton, with sticky switching),
    /// and drives the cursor views.
    /// </summary>
    public class UserPointerManager : MonoBehaviour
    {
        [Header("References")]
        public SensorPoseCalibrator calibrator;
        public ProjectionScreen screen;
        public RectTransform cursorCanvas;
        [Tooltip("Classic ring/dot cursor prefab (used when Cursor Mode = Classic).")]
        public PointerCursorView cursorPrefab;
        [Tooltip("Animated cursor prefab with an Animator Controller (used when Cursor Mode = Animated). If unassigned, falls back to the classic prefab.")]
        public PointerCursorView animatedCursorPrefab;

        [Tooltip("Which cursor prefab drives all users: the classic ring/dot or the Animator-driven prefab.")]
        public PointerCursorMode cursorMode = PointerCursorMode.Classic;

        [Header("Pointing Strategy")]
        [Tooltip("Which pointing implementation drives the cursor. Switchable live in Play mode.")]
        public PointingMode pointingMode = PointingMode.ArmRay;

        [Tooltip("Mapping + smoothing for the horizontal-only pointing modes (JointBoundsCenter, SpineHorizontal). Needs on-site calibration.")]
        public HorizontalPointingConfig horizontalPointing = new HorizontalPointingConfig();

        /// <summary>
        /// True for the horizontal-only modes where the cursor's vertical position encodes the
        /// user's depth (rising as they approach) rather than aim. Hotspot hover should match on
        /// horizontal alignment only in these modes — the cursor never vertically coincides with a
        /// hotspot until it has risen all the way to the Select line.
        /// </summary>
        public bool IsHorizontalPointing =>
            pointingMode == PointingMode.SpineHorizontal || pointingMode == PointingMode.JointBoundsCenter
            || pointingMode == PointingMode.MouseOverrideWithDistance;

        [Tooltip("Interaction box for the BoxCursor pointing mode (also visualized by the skeleton debug overlay).")]
        public BoxCursorConfig boxCursor = new BoxCursorConfig();

        [Header("Joint Filtering")]
        public JointFilterConfig jointFilter = new JointFilterConfig();

        [Header("Aim Ray")]
        public RayModelConfig rayModel = new RayModelConfig();

        [Header("Pointing Detection")]
        public PointingDetectionConfig pointingDetection = new PointingDetectionConfig();

        [Header("Cursor Smoothing (screen-space One Euro, runs at sensor cadence)")]
        public OneEuroParams cursorFilter = new OneEuroParams(0.3f, 0.4f, 0.6f);

        [Header("Cursor Outlier Rejection (runs before smoothing)")]
        public UvOutlierGateConfig outlierGate = new UvOutlierGateConfig();

        [Header("Cursor Spring & Rest Stabilizer (runs at render rate)")]
        public CursorStabilizerConfig cursorStabilizer = new CursorStabilizerConfig();

        [Header("Active User Selection")]
        [Tooltip("How the single active (slideshow-driving) user is chosen. Switchable live in Play mode.")]
        public ActiveUserMode activeUserMode = ActiveUserMode.CentralPointing;

        [Tooltip("ClosestToScreen: a challenger must stand at least this much closer to the wall (meters) than the active user to steal.")]
        [Min(0f)] public float closestDepthStealMarginMeters = 0.25f;

        [Tooltip("ClosestToScreen: minimum seconds the active user keeps status before a closer challenger can steal.")]
        [Min(0f)] public float closestMinHoldSeconds = 1f;

        [Tooltip("Depth zones: a user must be at room-space Z <= this (closer to the wall; wall is at negative Z) for the Hover zone. Used by the slideshow hover/select gating and the CentralClosestZone selector.")]
        public float maxHoverZ = 2.5f;

        [Tooltip("Depth zones: a user must be at room-space Z <= this (closer than MaxHoverZ) for the Select zone.")]
        public float maxSelectZ = 1.5f;

        [Tooltip("FirstSeenInHover: depth hysteresis (meters). The active holder keeps the cursor until they step past MaxHoverZ + this margin, even though acquisition still requires being within MaxHoverZ. Prevents boundary flicker from sensor jitter.")]
        [Min(0f)] public float hoverExitMarginMeters = 0.2f;

        [Tooltip("A challenger must stand this much closer to the screen's center axis (meters) to steal active status from a pointing user.")]
        [Min(0f)] public float stealMarginMeters = 0.3f;

        [Tooltip("Minimum seconds a user keeps active status before anyone may steal it (while they keep pointing).")]
        [Min(0f)] public float minHoldSeconds = 1.5f;

        [Tooltip("Seconds the active user keeps status after they stop pointing, before handoff.")]
        [Min(0f)] public float activeLossGraceSeconds = 0.7f;

        [Tooltip("A user must have been pointing at least this long to become a candidate for active status.")]
        [Min(0f)] public float candidateMinPointSeconds = 0.3f;

        [Tooltip("Seconds after a skeleton disappears before its pointer state and cursor are removed.")]
        [Min(0f)] public float userLostGraceSeconds = 1f;

        [Header("Cursor Lifecycle & Appearance")]
        [Tooltip("Show cursors for non-active users (the active cursor is always shown).")]
        public bool showInactiveCursors = true;

        [Tooltip("Only show an inactive cursor while its user actually counts as pointing (hides incidental wall hits from arms in transit).")]
        public bool inactiveRequiresPointing = false;

        [Tooltip("Seconds the cursor holds its last position after aim is lost, before fading out.")]
        [Min(0f)] public float cursorHoldSeconds = 0.4f;

        [Min(0.01f)] public float cursorFadeInSeconds = 0.15f;
        [Min(0.01f)] public float cursorFadeOutSeconds = 0.5f;

        [Tooltip("Per-user identity colors, applied as a tint to inactive cursors (indexed by user ID).")]
        public Color[] userPalette =
        {
            new Color(0.25f, 0.8f, 1f),
            new Color(1f, 0.4f, 0.75f),
            new Color(0.6f, 1f, 0.35f),
            new Color(1f, 0.8f, 0.25f),
            new Color(0.7f, 0.55f, 1f),
            new Color(1f, 0.55f, 0.3f),
        };

        public class PointerState
        {
            public int UserId;
            public IPointingSolver Solver;
            public readonly OneEuroFilterVector2 UvFilter = new OneEuroFilterVector2();
            public readonly UvOutlierGate OutlierGate = new UvOutlierGate();
            /// <summary>dt accumulated across frames whose samples never reached the UV filter (discarded, absent, or staircase repeats), so the filter's speed estimate stays honest.</summary>
            public float PendingFilterDt;
            /// <summary>dt accumulated between outlier-gate evaluations, for the gate's history aging.</summary>
            public float PendingGateDt;
            /// <summary>Previous frame's raw ScreenUV; bit-identical UV means no new sensor information (held joints).</summary>
            public Vector2 LastRawUv;
            public bool HasLastRawUv;
            /// <summary>This frame's fresh sample was rejected by the outlier gate.</summary>
            public bool LastSampleDiscarded;
            /// <summary>The UV One Euro filter consumed a sample this frame.</summary>
            public bool FilterUpdatedThisFrame;
            /// <summary>Spring-smoothed cursor position; what views and hit-testing consume.</summary>
            public Vector2 DisplayUv;
            public Vector2 DisplayUvVelocity;
            /// <summary>0 = moving, 1 = fully at rest (extra-heavy spring engaged).</summary>
            public float Rest01;
            /// <summary>Display-only magnetism toward a hovered hotspot, written by the slideshow layer; expires if not refreshed.</summary>
            public Vector2 MagnetUv;
            public float MagnetWeight;
            public float MagnetSetTime = float.NegativeInfinity;
            /// <summary>Kind of hotspot currently under this cursor, written by the slideshow layer; expires if not refreshed.</summary>
            public CursorHotspotKind HoveredHotspotKind;
            public float HotspotKindSetTime = float.NegativeInfinity;
            public PointerCursorView View;
            public AimSample Sample;
            public float LastSeenTime;
            public float LastPointingTime = float.NegativeInfinity;
            public float PointingDuration;
            public float ActiveSince;
            public float TimeSinceUV = float.PositiveInfinity;
            public Vector2 Uv;
            public bool HasUv;
            public float Alpha;
            public float Centrality = float.PositiveInfinity;
            /// <summary>Room-space body reference from the pointing solver (X drives centrality, Z drives distance-to-screen).</summary>
            public Vector3 BodyPosition;
            public bool HasBody;
            /// <summary>Monotonic arrival order stamped once on creation; lower = seen earlier. Survives Nuitrack id recycling, unlike UserId. Used by the FirstSeenInHover selector.</summary>
            public long FirstSeenArrival;
        }

        /// <summary>Reserved id for the synthetic MouseOverride pointer (Nuitrack user ids start at 1).</summary>
        const int MouseUserId = 0;

        readonly Dictionary<int, PointerState> states = new Dictionary<int, PointerState>();
        readonly Dictionary<int, UserData> presentUsers = new Dictionary<int, UserData>();
        readonly List<int> removalScratch = new List<int>();
        long nextArrival = 0;

        IActiveUserSelector activeSelector;
        PointingMode lastPointingMode;
        ActiveUserMode lastActiveUserMode;

        public IReadOnlyDictionary<int, PointerState> States => states;
        public int ActiveUserId { get; private set; } = -1;

        void Awake()
        {
            lastPointingMode = pointingMode;
            lastActiveUserMode = activeUserMode;
            activeSelector = BuildActiveSelector();
        }

        void Update()
        {
            float dt = Time.deltaTime;
            float now = Time.time;

            ApplyModeSwitches();

            // Both mouse-driven modes share the same synthetic-user path: no sensor, no
            // floor-calibration warmup, and the single mouse user is force-activated immediately.
            bool mouseOverride = pointingMode == PointingMode.MouseOverride
                              || pointingMode == PointingMode.MouseOverrideWithDistance;
            bool calibrated = calibrator != null && calibrator.IsCalibrated;

            // MouseOverride drives the cursor straight from the mouse, so it needs neither the
            // sensor nor the floor-calibration warmup — it's interactive on the first frame.
            // Every other mode must wait for a calibrated room frame before it can aim.
            if (!mouseOverride && (!calibrated || screen == null))
            {
                FadeAllCursors(dt);
                return;
            }

            Matrix4x4 roomFromSensor = calibrated ? calibrator.RoomFromSensor : Matrix4x4.identity;
            float lateralOffset = screen != null ? screen.screenLateralOffsetMeters : 0f;

            presentUsers.Clear();
            if (mouseOverride)
            {
                // One synthetic user, independent of Nuitrack — its solver ignores the skeleton.
                if (!states.ContainsKey(MouseUserId))
                    states[MouseUserId] = CreateState(MouseUserId, now);
            }
            else if (NuitrackManager.sensorsData != null && NuitrackManager.sensorsData.Count > 0)
            {
                foreach (UserData user in NuitrackManager.sensorsData[0].Users)
                    if (user != null)
                        presentUsers[user.ID] = user;
            }

            foreach (var kv in presentUsers)
                if (!states.ContainsKey(kv.Key))
                    states[kv.Key] = CreateState(kv.Key, now);

            removalScratch.Clear();
            foreach (var kv in states)
            {
                PointerState st = kv.Value;
                bool isMouseUser = mouseOverride && kv.Key == MouseUserId;
                presentUsers.TryGetValue(kv.Key, out UserData user);
                if (user != null || isMouseUser)
                    st.LastSeenTime = now;
                else if (now - st.LastSeenTime > userLostGraceSeconds)
                {
                    removalScratch.Add(kv.Key);
                    continue;
                }

                st.Sample = st.Solver.Update(new PointingFrame(user, dt, roomFromSensor, screen, maxHoverZ, maxSelectZ));
                st.HasBody = st.Solver.HasBody;
                st.BodyPosition = st.Solver.BodyPosition;

                st.PointingDuration = st.Sample.IsPointing ? st.PointingDuration + dt : 0f;
                if (st.Sample.IsPointing)
                    st.LastPointingTime = now;

                st.Centrality = st.HasBody
                    ? Mathf.Abs(st.BodyPosition.x - lateralOffset)
                    : float.PositiveInfinity;

                st.PendingFilterDt += dt;
                st.PendingGateDt += dt;
                st.LastSampleDiscarded = false;
                st.FilterUpdatedThisFrame = false;

                if (st.Sample.HasScreenUV)
                {
                    // Bit-identical raw UV means the joints were held (no new sensor frame),
                    // so there is no new information: skip the gate and filter so the One
                    // Euro never sees the 30 Hz staircase. Extrapolated joints move every
                    // frame and still flow through.
                    bool newSample = !st.HasLastRawUv || st.Sample.ScreenUV != st.LastRawUv;
                    st.LastRawUv = st.Sample.ScreenUV;
                    st.HasLastRawUv = true;

                    if (newSample)
                    {
                        if (st.OutlierGate.Accept(st.Sample.ScreenUV, st.PendingGateDt, outlierGate))
                        {
                            bool wasInitialized = st.UvFilter.IsInitialized;
                            Vector2 prevFiltered = st.Uv;
                            st.Uv = st.UvFilter.Filter(st.Sample.ScreenUV, st.PendingFilterDt, cursorFilter);
                            st.FilterUpdatedThisFrame = true;

                            if (!wasInitialized)
                            {
                                // (Re)acquire: snap the spring so the cursor doesn't glide across the screen.
                                st.DisplayUv = st.Uv;
                                st.DisplayUvVelocity = Vector2.zero;
                                st.Rest01 = 0f;
                            }
                            else
                            {
                                float speed = (st.Uv - prevFiltered).magnitude / Mathf.Max(st.PendingFilterDt, 1e-4f);
                                UpdateRestState(st, speed, st.PendingFilterDt);
                            }
                            st.PendingFilterDt = 0f;
                        }
                        else
                        {
                            // Discarded outlier: the cursor holds its last position but stays alive.
                            st.LastSampleDiscarded = true;
                        }
                        st.PendingGateDt = 0f;
                    }

                    st.HasUv = true;
                    st.TimeSinceUV = 0f;
                }
                else
                {
                    st.TimeSinceUV += dt;
                    if (st.TimeSinceUV > cursorHoldSeconds)
                    {
                        // Reacquire snaps instead of gliding across the screen.
                        st.UvFilter.Reset();
                        st.OutlierGate.Reset();
                        st.PendingFilterDt = 0f;
                        st.PendingGateDt = 0f;
                        st.HasLastRawUv = false;
                    }
                }

                // Render-rate spring chases the sensor-cadence filtered target, so the
                // cursor moves smoothly at full frame rate from ~30 Hz data.
                if (st.HasUv)
                {
                    float smoothTime = Mathf.Lerp(cursorStabilizer.smoothTime, cursorStabilizer.restSmoothTime, st.Rest01);
                    st.DisplayUv = Vector2.SmoothDamp(st.DisplayUv, st.Uv, ref st.DisplayUvVelocity,
                                                      smoothTime, float.PositiveInfinity, dt);
                }
            }
            foreach (int id in removalScratch)
                RemoveState(id);

            if (mouseOverride)
            {
                // Exactly one (synthetic) user; make it active at once so the cursor is live
                // immediately, bypassing the candidate-dwell the selectors impose.
                if (ActiveUserId != MouseUserId && states.TryGetValue(MouseUserId, out PointerState mouseState))
                {
                    ActiveUserId = MouseUserId;
                    mouseState.ActiveSince = now;
                }
            }
            else
            {
                UpdateActiveUser(now);
            }
            DriveViews(dt);
        }

        /// <summary>Apply live changes to the pointing/active-user dropdowns: rebuild solvers / swap the selector.</summary>
        void ApplyModeSwitches()
        {
            if (pointingMode != lastPointingMode)
            {
                lastPointingMode = pointingMode;
                // Rebuild from scratch on a mode change: solvers differ, and MouseOverride's
                // synthetic user must not linger when leaving (nor stale skeletons when entering).
                ClearAllStates();
            }
            if (activeSelector == null || activeUserMode != lastActiveUserMode)
            {
                lastActiveUserMode = activeUserMode;
                activeSelector = BuildActiveSelector();
            }
        }

        IPointingSolver BuildSolver() => pointingMode switch
        {
            PointingMode.JointBoundsCenter => new JointBoundsPointingSolver(jointFilter, horizontalPointing),
            PointingMode.SpineHorizontal => new SpinePointingSolver(jointFilter, horizontalPointing),
            PointingMode.BoxCursor => new BoxCursorPointingSolver(jointFilter, boxCursor),
            PointingMode.MouseOverride => new MouseOverridePointingSolver(),
            PointingMode.MouseOverrideWithDistance => new MouseOverrideWithDistancePointingSolver(horizontalPointing),
            _ => new PointingArmSolver(jointFilter, rayModel, pointingDetection),
        };

        IActiveUserSelector BuildActiveSelector() => activeUserMode switch
        {
            ActiveUserMode.ClosestToScreen => new ClosestToScreenSelector(this),
            ActiveUserMode.CentralClosestZone => new CentralClosestZoneSelector(this),
            ActiveUserMode.SingleUser => new SingleUserSelector(),
            ActiveUserMode.FirstSeenInHover => new FirstSeenInHoverSelector(this),
            _ => new CentralPointingSelector(this),
        };

        /// <summary>
        /// Rest detector with speed hysteresis: deliberate motion snaps the cursor out of
        /// the rest hold immediately; near-stillness blends it in over restBlendSeconds.
        /// </summary>
        void UpdateRestState(PointerState st, float filteredSpeed, float sampleDt)
        {
            if (cursorStabilizer.restSpeedEnter <= 0f)
            {
                st.Rest01 = 0f;
                return;
            }

            if (filteredSpeed > cursorStabilizer.restSpeedExit)
                st.Rest01 = 0f;
            else if (filteredSpeed < cursorStabilizer.restSpeedEnter)
                st.Rest01 = Mathf.MoveTowards(st.Rest01, 1f, sampleDt / Mathf.Max(cursorStabilizer.restBlendSeconds, 1e-3f));
            // In the hysteresis band: hold the current rest level.
        }

        PointerState CreateState(int id, float now)
        {
            var st = new PointerState { UserId = id, LastSeenTime = now, FirstSeenArrival = nextArrival++ };
            st.Solver = BuildSolver();
            PointerCursorView prefab = cursorMode == PointerCursorMode.Animated && animatedCursorPrefab != null
                ? animatedCursorPrefab
                : cursorPrefab;
            if (prefab != null && cursorCanvas != null)
            {
                st.View = Instantiate(prefab, cursorCanvas);
                st.View.name = $"PointerCursor_User{id}";
                st.View.Init(cursorCanvas);
                st.View.SetAlpha(0f);
                if (userPalette != null && userPalette.Length > 0)
                {
                    int paletteIndex = ((id - 1) % userPalette.Length + userPalette.Length) % userPalette.Length;
                    st.View.SetUserTint(userPalette[paletteIndex]);
                }
            }
            return st;
        }

        void RemoveState(int id)
        {
            if (states.TryGetValue(id, out PointerState st) && st.View != null)
                Destroy(st.View.gameObject);
            states.Remove(id);
            if (ActiveUserId == id)
                ActiveUserId = -1;
        }

        void ClearAllStates()
        {
            foreach (PointerState st in states.Values)
                if (st.View != null)
                    Destroy(st.View.gameObject);
            states.Clear();
            ActiveUserId = -1;
        }

        void UpdateActiveUser(float now)
        {
            if (activeSelector == null)
                activeSelector = BuildActiveSelector();

            int newId = activeSelector.Select(states, ActiveUserId, now);
            if (newId != ActiveUserId)
            {
                ActiveUserId = newId;
                if (newId >= 0 && states.TryGetValue(newId, out PointerState st))
                    st.ActiveSince = now;
            }
        }

        void DriveViews(float dt)
        {
            foreach (var kv in states)
            {
                PointerState st = kv.Value;
                if (st.View == null)
                    continue;

                bool isActive = kv.Key == ActiveUserId;
                bool visible = st.HasUv && st.TimeSinceUV <= cursorHoldSeconds;
                if (!isActive)
                {
                    if (!showInactiveCursors)
                        visible = false;
                    else if (inactiveRequiresPointing && !st.Sample.IsPointing)
                        visible = false;
                }

                float target = visible ? 1f : 0f;
                float speed = target > st.Alpha
                    ? 1f / Mathf.Max(cursorFadeInSeconds, 1e-3f)
                    : 1f / Mathf.Max(cursorFadeOutSeconds, 1e-3f);
                st.Alpha = Mathf.MoveTowards(st.Alpha, target, speed * dt);

                st.View.SetActive(isActive);
                st.View.SetAlpha(st.Alpha);
                st.View.SetProximity(DepthProximity01(st));
                // Hovered hotspot kind expires like magnetism when the slideshow stops refreshing it
                // (transitions / non-Idle), so the animated cursor drops back to its neutral state.
                CursorHotspotKind kind = Time.time - st.HotspotKindSetTime < 0.1f
                    ? st.HoveredHotspotKind
                    : CursorHotspotKind.None;
                st.View.SetHotspotKind(kind);
                if (st.HasUv)
                {
                    Vector2 shown = st.DisplayUv;
                    // Display-only magnetism toward a hovered hotspot; stale data expires
                    // automatically when the slideshow layer stops refreshing it.
                    if (st.MagnetWeight > 0f && Time.time - st.MagnetSetTime < 0.1f)
                        shown = Vector2.Lerp(shown, st.MagnetUv, st.MagnetWeight);
                    st.View.SetUV(shown);
                }
            }
        }

        /// <summary>
        /// 0..1 depth progress for the inner-dot growth: 0 at (or beyond) the Hover-zone distance
        /// (far), 1 at (or within) the Select-zone distance (close). No body (e.g. MouseOverride)
        /// reports fully close so the dev cursor shows a full dot.
        /// </summary>
        public float DepthProximity01(PointerState st)
        {
            if (st == null || !st.HasBody)
                return 1f;
            float zSpan = maxHoverZ - maxSelectZ;
            if (Mathf.Abs(zSpan) < 1e-4f)
                return 1f;
            return Mathf.Clamp01((maxHoverZ - st.BodyPosition.z) / zSpan);
        }

        void FadeAllCursors(float dt)
        {
            foreach (PointerState st in states.Values)
            {
                st.Alpha = Mathf.MoveTowards(st.Alpha, 0f, dt / Mathf.Max(cursorFadeOutSeconds, 1e-3f));
                if (st.View != null)
                    st.View.SetAlpha(st.Alpha);
            }
        }
    }
}
