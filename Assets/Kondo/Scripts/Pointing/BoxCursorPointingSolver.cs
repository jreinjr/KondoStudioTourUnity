using System.Collections.Generic;
using UnityEngine;
using NuitrackSDK;

namespace Kondo.Pointing
{
    /// <summary>
    /// Box-cursor pointing: a screen-aspect box, parallel to the screen and centered on the
    /// user's body, defines a small interaction window. While a hand is inside the box it acts
    /// as the cursor, mapped by its position within the box to the full screen. When both hands
    /// are inside, the one nearer the screen (smaller room-space Z) wins. Reuses
    /// <see cref="JointTracker"/> for the same cadence-skip/extrapolation as the rest of the
    /// pipeline. The box geometry is exposed for the debug overlay.
    /// </summary>
    public class BoxCursorPointingSolver : IPointingSolver
    {
        static readonly nuitrack.JointType[] TrackedJoints =
        {
            nuitrack.JointType.Torso,
            nuitrack.JointType.LeftShoulder, nuitrack.JointType.RightShoulder,
            nuitrack.JointType.LeftWrist, nuitrack.JointType.RightWrist,
            nuitrack.JointType.LeftHand, nuitrack.JointType.RightHand,
        };

        readonly Dictionary<nuitrack.JointType, JointTracker> trackers = new Dictionary<nuitrack.JointType, JointTracker>();
        readonly JointFilterConfig jointFilter;
        readonly BoxCursorConfig config;

        public bool HasBody { get; private set; }
        public Vector3 BodyPosition { get; private set; }

        // Exposed for the debug overlay.
        public bool HasBox { get; private set; }
        public Vector3 BoxCenter { get; private set; }
        public float BoxWidth { get; private set; }
        public float BoxHeight { get; private set; }

        public BoxCursorPointingSolver(JointFilterConfig jointFilter, BoxCursorConfig config)
        {
            this.jointFilter = jointFilter;
            this.config = config;
            foreach (var jt in TrackedJoints)
                trackers[jt] = new JointTracker();
        }

        public AimSample Update(in PointingFrame frame)
        {
            UserData.SkeletonData skeleton = frame.User?.Skeleton;
            foreach (var jt in TrackedJoints)
            {
                JointTracker tracker = trackers[jt];
                if (skeleton != null)
                    tracker.Update(skeleton.GetJoint(jt), frame.RoomFromSensor, frame.Dt, jointFilter);
                else
                    tracker.UpdateMissing(frame.Dt, jointFilter);
            }

            JointTracker torso = trackers[nuitrack.JointType.Torso];
            JointTracker ls = trackers[nuitrack.JointType.LeftShoulder];
            JointTracker rs = trackers[nuitrack.JointType.RightShoulder];

            HasBox = config.TryComputeBox(torso.IsUsable, torso.Position, ls.IsUsable, ls.Position,
                rs.IsUsable, rs.Position, frame.Screen, out Vector3 center, out float width, out float height);
            BoxCenter = center;
            BoxWidth = width;
            BoxHeight = height;

            if (!HasBox)
            {
                HasBody = false;
                return new AimSample { HasRay = false, IsPointing = false, HasScreenUV = false };
            }

            // Body reference for active-user selection: torso when available, else the box center.
            BodyPosition = torso.IsUsable ? torso.Position : center;
            HasBody = true;

            JointTracker leftEnd = HandEnd(Arm.Left);
            JointTracker rightEnd = HandEnd(Arm.Right);
            bool leftIn = leftEnd.IsUsable && InsideBox(leftEnd.Position, center, width, height);
            bool rightIn = rightEnd.IsUsable && InsideBox(rightEnd.Position, center, width, height);

            JointTracker chosen = null;
            if (leftIn && rightIn)
                chosen = leftEnd.Position.z <= rightEnd.Position.z ? leftEnd : rightEnd; // smaller Z = nearer screen
            else if (leftIn)
                chosen = leftEnd;
            else if (rightIn)
                chosen = rightEnd;

            if (chosen == null)
                return new AimSample { HasRay = false, IsPointing = false, HasScreenUV = false };

            Vector3 h = chosen.Position;
            float u = (h.x - center.x) / Mathf.Max(width, 1e-3f) + 0.5f;
            float v = (h.y - center.y) / Mathf.Max(height, 1e-3f) + 0.5f;
            if (config.flipX)
                u = 1f - u;

            return new AimSample
            {
                HasRay = false,
                IsPointing = true,
                Quality = 1f,
                HasScreenUV = true,
                ScreenUV = new Vector2(Mathf.Clamp01(u), Mathf.Clamp01(v)),
                OnScreen = true,
            };
        }

        static bool InsideBox(Vector3 p, Vector3 center, float width, float height) =>
            Mathf.Abs(p.x - center.x) <= width * 0.5f && Mathf.Abs(p.y - center.y) <= height * 0.5f;

        JointTracker HandEnd(Arm arm)
        {
            JointTracker wrist = trackers[arm == Arm.Left ? nuitrack.JointType.LeftWrist : nuitrack.JointType.RightWrist];
            JointTracker hand = trackers[arm == Arm.Left ? nuitrack.JointType.LeftHand : nuitrack.JointType.RightHand];
            JointTracker preferred = config.useWristForHand ? wrist : hand;
            JointTracker backup = config.useWristForHand ? hand : wrist;
            return preferred.IsUsable ? preferred : backup;
        }
    }
}
