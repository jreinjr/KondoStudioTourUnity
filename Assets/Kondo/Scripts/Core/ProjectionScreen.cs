using UnityEngine;

namespace Kondo.Core
{
    /// <summary>
    /// Defines the physical projection rectangle on the wall (in room space) and maps
    /// aim rays to normalized screen UV, including the artistic gain/offset trims.
    /// Room space: origin on the floor below the sensor, +Y up, +Z into the room,
    /// so the wall plane sits at z = wallZOffsetMeters and aim rays travel toward -Z.
    /// </summary>
    public class ProjectionScreen : MonoBehaviour
    {
        [Header("References")]
        public SensorPoseCalibrator calibrator;

        [Header("Physical Screen (meters, measured on site)")]
        [Tooltip("Width of the projected image.")]
        [Min(0.1f)] public float screenWidthMeters = 4f;

        [Tooltip("Height of the projected image.")]
        [Min(0.1f)] public float screenHeightMeters = 2.25f;

        [Tooltip("Vertical distance from the sensor lens down to the TOP edge of the projected image.")]
        public float screenTopBelowSensorMeters = 0.3f;

        [Tooltip("Horizontal offset of the screen center relative to the sensor (positive = +X in room space). 0 when the sensor is centered above the image.")]
        public float screenLateralOffsetMeters = 0f;

        [Tooltip("Z of the wall/projection plane in room space. Negative = the wall surface is slightly behind the sensor lens.")]
        public float wallZOffsetMeters = -0.05f;

        [Header("Aim Trims (artistic control)")]
        [Tooltip("Mirror the horizontal axis. Flip this if pointing left moves the cursor right.")]
        public bool flipX = true;

        [Tooltip("Aim amplification around the screen center. >1 lets users reach the edges without fully extending their arm. Vertical range is usually smaller, so Y gain is typically higher.")]
        public Vector2 aimGain = new Vector2(1.4f, 1.6f);

        [Tooltip("Aim bias in normalized screen units, applied before gain (e.g. raise Y if everyone systematically aims low).")]
        public Vector2 aimOffset = Vector2.zero;

        [Tooltip("How far past the screen edge (normalized) a hit may land and still count as on-screen; the cursor 'pins' to the edge instead of vanishing.")]
        [Range(0f, 0.5f)] public float edgeMargin = 0.05f;

        [Tooltip("Minimum component of the aim direction toward the wall; rays flatter than this produce no hit.")]
        [Range(0.01f, 0.5f)] public float minTowardWall = 0.05f;

        public float SensorHeight
        {
            get
            {
                if (calibrator == null)
                    return 3f;
                return calibrator.IsCalibrated ? calibrator.CurrentHeight : calibrator.mountHeightMeters;
            }
        }

        /// <summary>Center of the projected image in room space.</summary>
        public Vector3 ScreenCenter => new Vector3(
            screenLateralOffsetMeters,
            SensorHeight - screenTopBelowSensorMeters - screenHeightMeters * 0.5f,
            wallZOffsetMeters);

        /// <summary>
        /// Intersects a room-space ray with the projection plane. Returns false when the
        /// ray doesn't head toward the wall. uv is normalized over the screen rect with
        /// gain/offset trims applied and clamped to ±edgeMargin; onScreen reports whether
        /// the trimmed hit lies within the rect (± margin).
        /// </summary>
        public bool RaycastToUV(Ray roomRay, out Vector2 uv, out Vector3 roomHit, out bool onScreen)
        {
            uv = default;
            roomHit = default;
            onScreen = false;

            Vector3 d = roomRay.direction;
            if (d.z >= -minTowardWall)
                return false;

            float t = (wallZOffsetMeters - roomRay.origin.z) / d.z;
            if (t <= 0f)
                return false;

            roomHit = roomRay.origin + d * t;
            Vector3 c = ScreenCenter;
            float u = 0.5f + (roomHit.x - c.x) / Mathf.Max(screenWidthMeters, 1e-3f) * (flipX ? -1f : 1f);
            float v = 0.5f + (roomHit.y - c.y) / Mathf.Max(screenHeightMeters, 1e-3f);

            Vector2 half = new Vector2(0.5f, 0.5f);
            uv = half + Vector2.Scale(aimGain, new Vector2(u, v) - half + aimOffset);

            onScreen = uv.x >= -edgeMargin && uv.x <= 1f + edgeMargin
                    && uv.y >= -edgeMargin && uv.y <= 1f + edgeMargin;

            uv.x = Mathf.Clamp(uv.x, -edgeMargin, 1f + edgeMargin);
            uv.y = Mathf.Clamp(uv.y, -edgeMargin, 1f + edgeMargin);
            return true;
        }
    }
}
