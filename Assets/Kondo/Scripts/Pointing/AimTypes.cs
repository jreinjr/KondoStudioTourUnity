using System;
using UnityEngine;
using Kondo.Core;

namespace Kondo.Pointing
{
    public enum Arm
    {
        Right,
        Left,
    }

    public enum RayModel
    {
        [Tooltip("Forearm direction (elbow → hand). Matches pointing intent best, but has the shortest baseline (most jitter).")]
        ElbowToHand,
        [Tooltip("Whole-arm direction (shoulder → hand). Longer baseline = more angular stability, slightly different feel.")]
        ShoulderToHand,
        [Tooltip("Eye-hand line (head → hand). Most accurate for distant targets if the head joint is stable.")]
        HeadToHand,
        [Tooltip("Upper-arm direction (shoulder → elbow). Coarse; used as a fallback when the hand is lost.")]
        ShoulderToElbow,
    }

    /// <summary>
    /// Result of one per-user aim solve, in room space.
    /// </summary>
    public struct AimSample
    {
        public bool HasRay;
        public Ray RoomRay;
        public RayModel ModelUsed;
        public Arm Arm;
        /// <summary>0..1, degrades with joint extrapolation and fallback ray models.</summary>
        public float Quality;
        public bool IsPointing;

        public bool HasScreenUV;
        /// <summary>Normalized screen position, may exceed 0..1 by the screen's edge margin.</summary>
        public Vector2 ScreenUV;
        public Vector3 RoomHit;
        /// <summary>True when the hit lies within the screen rect (± edge margin).</summary>
        public bool OnScreen;
    }

    [Serializable]
    public class JointFilterConfig
    {
        [Tooltip("Joints below this Nuitrack confidence are treated as lost. AI confidences are often quantized (~0 or ~0.75), so 0.5 is a safe gate.")]
        [Range(0f, 1f)] public float minConfidence = 0.5f;

        [Tooltip("Additionally require the joint to have valid depth data (recommended with Nuitrack AI).")]
        public bool requireGoodDepth = true;

        [Tooltip("After a joint is lost, keep predicting its position from its last velocity for this long before declaring it unusable.")]
        [Min(0f)] public float maxExtrapolationSeconds = 0.3f;

        [Tooltip("Half-life of the velocity decay while extrapolating a lost joint (smaller = the prediction brakes sooner).")]
        [Min(0.01f)] public float extrapolationHalfLifeSeconds = 0.15f;

        [Tooltip("One Euro filter applied to each joint position (in meters), at sensor frame cadence.")]
        public OneEuroParams filter = new OneEuroParams(1.0f, 0.5f, 1.0f);
    }

    [Serializable]
    public class RayModelConfig
    {
        [Tooltip("Preferred aim ray model. The solver automatically degrades to lower models when joints drop out. ShoulderToHand is the recommended default: its ~0.6m baseline roughly halves angular jitter versus the ~0.35m forearm.")]
        public RayModel primaryModel = RayModel.ShoulderToHand;

        [Tooltip("Use the wrist joint instead of the hand joint as the 'hand' end of rays. The Nuitrack hand joint tends to flip around the palm; the wrist is steadier.")]
        public bool useWristForHand = true;

        [Tooltip("When falling back to shoulder→elbow, the ray origin is pushed from the elbow along the ray by this assumed forearm length, so the cursor doesn't jump backward.")]
        [Min(0f)] public float forearmLengthMeters = 0.35f;

        [Tooltip("Joint pairs closer than this don't define a usable direction.")]
        [Min(0.01f)] public float minSegmentLengthMeters = 0.05f;

        [Tooltip("A better ray model must be continuously available for this long before the solver switches back up to it (prevents model flicker). Downgrades are immediate.")]
        [Min(0f)] public float modelUpgradeDwellSeconds = 0.2f;

        [Tooltip("The other arm's pointing score must exceed the current arm's by this margin before an arm switch is considered.")]
        [Min(0f)] public float armSwitchMargin = 0.15f;

        [Tooltip("The other arm must keep winning by the margin for this long before the solver switches arms.")]
        [Min(0f)] public float armSwitchDwellSeconds = 0.4f;
    }

    /// <summary>
    /// Render-rate cursor stabilizer: the One Euro filter runs at sensor cadence (~30 Hz),
    /// and a critically-damped spring chases its output every render frame, removing the
    /// 30 Hz staircase. A rest detector blends to a heavier spring while the user holds
    /// aim, so the cursor sits still for dwell selection without lagging deliberate moves.
    /// </summary>
    [Serializable]
    public class CursorStabilizerConfig
    {
        [Tooltip("Spring smoothing time (seconds) the displayed cursor takes to chase the filtered target during normal motion.")]
        [Min(0.01f)] public float smoothTime = 0.09f;

        [Tooltip("Filtered-UV speed (screen units/s) below which the cursor counts as held at rest. 0 disables the rest stabilizer.")]
        [Min(0f)] public float restSpeedEnter = 0.04f;

        [Tooltip("Speed above which rest ends immediately (hysteresis; should exceed the enter speed).")]
        [Min(0f)] public float restSpeedExit = 0.15f;

        [Tooltip("Spring smoothing time used when fully at rest (heavier hold for dwell steadiness).")]
        [Min(0.01f)] public float restSmoothTime = 0.35f;

        [Tooltip("Seconds to blend into the rest hold once speed drops below the enter threshold.")]
        [Min(0.01f)] public float restBlendSeconds = 0.25f;
    }

    [Serializable]
    public class PointingDetectionConfig
    {
        [Header("Enter (all conditions must hold to start pointing)")]
        [Tooltip("Arm straightness: |shoulder→hand| / (|shoulder→elbow| + |elbow→hand|). 1 = fully extended.")]
        [Range(0f, 1f)] public float enterExtensionRatio = 0.80f;

        [Tooltip("Hand must be at least this far above the torso joint (meters; can be negative).")]
        public float enterHandAboveTorsoMeters = 0.0f;

        [Tooltip("Hand must be at least this far in front of the shoulder, toward the screen (meters).")]
        public float enterHandForwardMeters = 0.25f;

        [Tooltip("Minimum component of the aim direction toward the wall (0..1).")]
        [Range(0f, 1f)] public float enterRayTowardWall = 0.20f;

        [Tooltip("Require the aim ray to actually hit the screen rect (± edge margin) to start pointing.")]
        public bool requireScreenHitToEnter = true;

        [Tooltip("Enter conditions must hold continuously for this long before the user counts as pointing.")]
        [Min(0f)] public float enterDwellSeconds = 0.25f;

        [Header("Exit (any violation sustained for the dwell stops pointing)")]
        [Range(0f, 1f)] public float exitExtensionRatio = 0.70f;
        public float exitHandAboveTorsoMeters = -0.10f;
        public float exitHandForwardMeters = 0.15f;
        [Range(0f, 1f)] public float exitRayTowardWall = 0.10f;

        [Tooltip("An exit violation (or total aim loss) must persist this long before the user stops counting as pointing.")]
        [Min(0f)] public float exitDwellSeconds = 0.5f;
    }
}
