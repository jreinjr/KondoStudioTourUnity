using System;
using UnityEngine;
using Kondo.Core;
using NuitrackSDK;

namespace Kondo.Pointing
{
    /// <summary>Which pointing implementation drives the cursor. Selected on <see cref="UserPointerManager"/>.</summary>
    public enum PointingMode
    {
        [Tooltip("Aim ray from the arm (forearm/whole-arm fallback chain). The original, full 2D pointing.")]
        ArmRay,
        [Tooltip("Horizontal-only: smoothed center of the bounding box of the user's joints (raising an arm biases it sideways).")]
        JointBoundsCenter,
        [Tooltip("Horizontal-only: lateral position of the torso/spine joint (where the user stands).")]
        SpineHorizontal,
        [Tooltip("A screen-aspect box centered on the user's torso; the hand inside it (the one nearer the screen) is the cursor, mapped by its position in the box.")]
        BoxCursor,
        [Tooltip("Development/override: the cursor follows the mouse exclusively, ignoring the skeleton and how close the user stands. Drives the show from the mouse.")]
        MouseOverride,
        [Tooltip("Development/override: mouse X drives the cursor X; mouse Y drives a virtual standing distance (Hover zone at 10% screen height, Select zone at 50%) instead of the cursor Y, so the depth-zone interaction can be tested from the mouse.")]
        MouseOverrideWithDistance,
    }

    /// <summary>Per-frame inputs handed to every <see cref="IPointingSolver"/>.</summary>
    public readonly struct PointingFrame
    {
        public readonly UserData User;
        public readonly float Dt;
        public readonly Matrix4x4 RoomFromSensor;
        public readonly ProjectionScreen Screen;
        /// <summary>Room-space Z (meters) of the Hover-zone boundary (farther from the wall). The horizontal cursor sits on its hover line at or beyond this distance.</summary>
        public readonly float HoverZ;
        /// <summary>Room-space Z (meters) of the Select-zone boundary (closer to the wall). The horizontal cursor reaches its select line at or within this distance.</summary>
        public readonly float SelectZ;

        public PointingFrame(UserData user, float dt, Matrix4x4 roomFromSensor, ProjectionScreen screen, float hoverZ, float selectZ)
        {
            User = user;
            Dt = dt;
            RoomFromSensor = roomFromSensor;
            Screen = screen;
            HoverZ = hoverZ;
            SelectZ = selectZ;
        }
    }

    /// <summary>
    /// A per-user pointing strategy: consumes one user's skeleton each frame and produces an
    /// <see cref="AimSample"/> (screen UV + IsPointing). One instance per user, created and
    /// driven by <see cref="UserPointerManager"/>. <see cref="BodyPosition"/> is the user's
    /// room-space body reference used by active-user selection (its X for centrality, its Z
    /// for distance to the screen).
    /// </summary>
    public interface IPointingSolver
    {
        AimSample Update(in PointingFrame frame);
        bool HasBody { get; }
        Vector3 BodyPosition { get; }
    }

    /// <summary>
    /// Maps a user's lateral room position to a horizontal cursor for the horizontal-only
    /// pointing modes (<see cref="PointingMode.JointBoundsCenter"/>, <see cref="PointingMode.SpineHorizontal"/>).
    /// These don't aim at the wall — they answer "where is the user standing", so the mapping
    /// is a simple room-X range to screen U. The cursor's vertical position rises with distance:
    /// it rides the <see cref="hoverLineV01"/> (low) when the user is far and rises toward the
    /// <see cref="selectLineV01"/> (slightly higher) as they approach the wall, so closing in
    /// nudges the cursor up. Requires on-site calibration.
    /// </summary>
    [Serializable]
    public class HorizontalPointingConfig
    {
        [Tooltip("Room-space X (meters) that maps to the left screen edge (U=0) before flip.")]
        public float roomXAtLeftEdgeMeters = -1.5f;

        [Tooltip("Room-space X (meters) that maps to the right screen edge (U=1) before flip.")]
        public float roomXAtRightEdgeMeters = 1.5f;

        [Tooltip("Mirror the horizontal axis (match the projection's flip so moving right on the floor moves the cursor right on the wall).")]
        public bool flipX = true;

        [Tooltip("Vertical screen line (0 = bottom, 1 = top) the cursor rides when the user is far (at/beyond the Hover-zone distance). Near the bottom of the screen.")]
        [Range(0f, 1f)] public float hoverLineV01 = 0.03f;

        [Tooltip("Vertical screen line (0 = bottom, 1 = top) the cursor rises to when the user is close (at/within the Select-zone distance). Slightly higher than the hover line.")]
        [Range(0f, 1f)] public float selectLineV01 = 0.10f;

        [Tooltip("One Euro filter applied to the bounding-box extents (JointBoundsCenter only) so they survive joints popping in and out.")]
        public OneEuroParams extentFilter = new OneEuroParams(1.0f, 0.3f, 1.0f);

        /// <summary>
        /// Build the horizontal-only aim sample for a given room-space body position. Shared by
        /// the spine and joint-bounds solvers. U comes from the body's lateral X; V interpolates
        /// from <see cref="hoverLineV01"/> to <see cref="selectLineV01"/> as the body's Z closes
        /// from <paramref name="hoverZ"/> (far) to <paramref name="selectZ"/> (near), so the
        /// cursor rises as the user approaches. OnScreen reflects the mapped X range.
        /// </summary>
        public AimSample ToHorizontalSample(bool hasBody, Vector3 bodyRoom, float hoverZ, float selectZ)
        {
            if (!hasBody)
                return new AimSample { HasRay = false, IsPointing = false, HasScreenUV = false };

            float span = roomXAtRightEdgeMeters - roomXAtLeftEdgeMeters;
            float t = Mathf.Abs(span) < 1e-4f ? 0.5f : (bodyRoom.x - roomXAtLeftEdgeMeters) / span;
            bool onScreen = t >= 0f && t <= 1f;
            float u = Mathf.Clamp01(t);
            if (flipX)
                u = 1f - u;

            // Rise from the hover line (at hoverZ, far) to the select line (at selectZ, near) as
            // the user closes in. Wall is at negative Z, so closer means smaller Z.
            float zSpan = hoverZ - selectZ;
            float rise = Mathf.Abs(zSpan) < 1e-4f ? 1f : Mathf.Clamp01((hoverZ - bodyRoom.z) / zSpan);
            float v = Mathf.Lerp(hoverLineV01, selectLineV01, rise);

            return new AimSample
            {
                HasRay = false,
                IsPointing = true,
                Quality = 1f,
                HasScreenUV = true,
                ScreenUV = new Vector2(u, Mathf.Clamp01(v)),
                OnScreen = onScreen,
            };
        }
    }

    /// <summary>
    /// A screen-aspect-ratio box, parallel to the screen and centered on the user's body, that
    /// maps a hand's position within it to the full screen (used by <see cref="PointingMode.BoxCursor"/>).
    /// The box is centered horizontally on the spine and, by default, vertically on the
    /// spine–shoulder intersection (the shoulder midpoint), with an adjustable offset.
    /// </summary>
    [Serializable]
    public class BoxCursorConfig
    {
        [Tooltip("Width of the interaction box in meters. Its height follows the screen's aspect ratio. A hand inside the box becomes the cursor.")]
        [Min(0.05f)] public float boxWidthMeters = 0.9f;

        [Tooltip("Vertical offset (meters) of the box center from the spine–shoulder intersection (shoulder midpoint). Positive raises the box.")]
        public float verticalOffsetMeters = 0f;

        [Tooltip("Mirror the horizontal axis so the cursor moves the same way the projection does.")]
        public bool flipX = true;

        [Tooltip("Use the wrist joint instead of the hand joint as the cursor point (the hand joint tends to flip around the palm).")]
        public bool useWristForHand = false;

        /// <summary>
        /// Compute the box from body joints: centered horizontally on the torso (or shoulder
        /// midpoint), vertically on the shoulder midpoint plus <see cref="verticalOffsetMeters"/>,
        /// with the screen's aspect ratio. Returns false if no body reference is available.
        /// </summary>
        public bool TryComputeBox(bool hasTorso, Vector3 torso, bool hasLeftShoulder, Vector3 leftShoulder,
            bool hasRightShoulder, Vector3 rightShoulder, ProjectionScreen screen,
            out Vector3 center, out float width, out float height)
        {
            center = default;
            width = 0f;
            height = 0f;

            bool hasShoulders = hasLeftShoulder && hasRightShoulder;
            Vector3 shoulderMid = hasShoulders ? (leftShoulder + rightShoulder) * 0.5f : Vector3.zero;

            float cx;
            if (hasTorso) cx = torso.x;
            else if (hasShoulders) cx = shoulderMid.x;
            else return false;

            float baseY = hasShoulders ? shoulderMid.y : (hasTorso ? torso.y : 0f);
            float cz = hasTorso ? torso.z : (hasShoulders ? shoulderMid.z : 0f);
            center = new Vector3(cx, baseY + verticalOffsetMeters, cz);

            width = Mathf.Max(0.05f, boxWidthMeters);
            float aspect = screen != null
                ? screen.screenHeightMeters / Mathf.Max(screen.screenWidthMeters, 1e-3f)
                : 0.5625f;
            height = width * aspect;
            return true;
        }
    }
}
