using System.Collections.Generic;
using UnityEngine;
using NuitrackSDK;

namespace Kondo.Pointing
{
    /// <summary>
    /// Horizontal-only pointing: the cursor's horizontal position follows the user's
    /// torso/spine joint (i.e. "where they stand"), mapped to screen U via
    /// <see cref="HorizontalPointingConfig"/>. Falls back to the waist, then the shoulder
    /// midpoint, when the torso joint drops out. Reuses <see cref="JointTracker"/> so the
    /// sensor-cadence skip and extrapolation behave like the rest of the pipeline.
    /// </summary>
    public class SpinePointingSolver : IPointingSolver
    {
        static readonly nuitrack.JointType[] TrackedJoints =
        {
            nuitrack.JointType.Torso, nuitrack.JointType.Waist,
            nuitrack.JointType.LeftShoulder, nuitrack.JointType.RightShoulder,
        };

        readonly Dictionary<nuitrack.JointType, JointTracker> trackers = new Dictionary<nuitrack.JointType, JointTracker>();
        readonly JointFilterConfig jointFilter;
        readonly HorizontalPointingConfig horizontal;

        public bool HasBody { get; private set; }
        public Vector3 BodyPosition { get; private set; }

        public SpinePointingSolver(JointFilterConfig jointFilter, HorizontalPointingConfig horizontal)
        {
            this.jointFilter = jointFilter;
            this.horizontal = horizontal;
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
            JointTracker waist = trackers[nuitrack.JointType.Waist];
            JointTracker ls = trackers[nuitrack.JointType.LeftShoulder];
            JointTracker rs = trackers[nuitrack.JointType.RightShoulder];

            if (torso.IsUsable)
            {
                BodyPosition = torso.Position;
                HasBody = true;
            }
            else if (waist.IsUsable)
            {
                BodyPosition = waist.Position;
                HasBody = true;
            }
            else if (ls.IsUsable && rs.IsUsable)
            {
                BodyPosition = (ls.Position + rs.Position) * 0.5f;
                HasBody = true;
            }
            else
            {
                HasBody = false;
            }

            return horizontal.ToHorizontalSample(HasBody, BodyPosition, frame.HoverZ, frame.SelectZ);
        }
    }
}
