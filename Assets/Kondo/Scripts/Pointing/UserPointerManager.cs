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
        public PointerCursorView cursorPrefab;

        [Header("Joint Filtering")]
        public JointFilterConfig jointFilter = new JointFilterConfig();

        [Header("Aim Ray")]
        public RayModelConfig rayModel = new RayModelConfig();

        [Header("Pointing Detection")]
        public PointingDetectionConfig pointingDetection = new PointingDetectionConfig();

        [Header("Cursor Smoothing (screen-space One Euro, runs at render rate)")]
        public OneEuroParams cursorFilter = new OneEuroParams(1.2f, 5f, 1f);

        [Header("Active User Selection")]
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
            public readonly PointingArmSolver Solver = new PointingArmSolver();
            public readonly OneEuroFilterVector2 UvFilter = new OneEuroFilterVector2();
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
        }

        readonly Dictionary<int, PointerState> states = new Dictionary<int, PointerState>();
        readonly Dictionary<int, UserData> presentUsers = new Dictionary<int, UserData>();
        readonly List<int> removalScratch = new List<int>();

        public IReadOnlyDictionary<int, PointerState> States => states;
        public int ActiveUserId { get; private set; } = -1;

        void Update()
        {
            float dt = Time.deltaTime;
            float now = Time.time;

            if (calibrator == null || screen == null || !calibrator.IsCalibrated)
            {
                FadeAllCursors(dt);
                return;
            }

            presentUsers.Clear();
            if (NuitrackManager.sensorsData != null && NuitrackManager.sensorsData.Count > 0)
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
                presentUsers.TryGetValue(kv.Key, out UserData user);
                if (user != null)
                    st.LastSeenTime = now;
                else if (now - st.LastSeenTime > userLostGraceSeconds)
                {
                    removalScratch.Add(kv.Key);
                    continue;
                }

                st.Sample = st.Solver.Update(user, dt, calibrator.RoomFromSensor, screen,
                                             jointFilter, rayModel, pointingDetection);

                st.PointingDuration = st.Sample.IsPointing ? st.PointingDuration + dt : 0f;
                if (st.Sample.IsPointing)
                    st.LastPointingTime = now;

                st.Centrality = st.Solver.HasTorso
                    ? Mathf.Abs(st.Solver.TorsoPosition.x - screen.screenLateralOffsetMeters)
                    : float.PositiveInfinity;

                if (st.Sample.HasScreenUV)
                {
                    st.Uv = st.UvFilter.Filter(st.Sample.ScreenUV, dt, cursorFilter);
                    st.HasUv = true;
                    st.TimeSinceUV = 0f;
                }
                else
                {
                    st.TimeSinceUV += dt;
                    if (st.TimeSinceUV > cursorHoldSeconds)
                        st.UvFilter.Reset(); // reacquire snaps instead of gliding across the screen
                }
            }
            foreach (int id in removalScratch)
                RemoveState(id);

            SelectActiveUser(now);
            DriveViews(dt);
        }

        PointerState CreateState(int id, float now)
        {
            var st = new PointerState { UserId = id, LastSeenTime = now };
            if (cursorPrefab != null && cursorCanvas != null)
            {
                st.View = Instantiate(cursorPrefab, cursorCanvas);
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

        void SelectActiveUser(float now)
        {
            PointerState active = null;
            if (ActiveUserId >= 0 && !states.TryGetValue(ActiveUserId, out active))
            {
                ActiveUserId = -1;
                active = null;
            }

            // Best challenger: most central user that has been pointing long enough.
            PointerState best = null;
            foreach (PointerState st in states.Values)
            {
                if (st.UserId == ActiveUserId)
                    continue;
                if (!st.Sample.IsPointing || st.PointingDuration < candidateMinPointSeconds)
                    continue;
                if (best == null || st.Centrality < best.Centrality)
                    best = st;
            }

            if (active != null)
            {
                bool stillPointing = active.Sample.IsPointing
                                  || now - active.LastPointingTime <= activeLossGraceSeconds;
                if (!stillPointing)
                {
                    SetActive(best, now);
                }
                else if (best != null
                         && best.Centrality < active.Centrality - stealMarginMeters
                         && now - active.ActiveSince >= minHoldSeconds)
                {
                    SetActive(best, now);
                }
            }
            else if (best != null)
            {
                SetActive(best, now);
            }
        }

        void SetActive(PointerState st, float now)
        {
            ActiveUserId = st?.UserId ?? -1;
            if (st != null)
                st.ActiveSince = now;
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
                if (st.HasUv)
                    st.View.SetUV(st.Uv);
            }
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
