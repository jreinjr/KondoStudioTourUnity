using System.Collections.Generic;
using UnityEngine;
using Kondo.Core;
using NuitrackSDK;

namespace Kondo.Pointing
{
    /// <summary>
    /// Horizontal-only pointing: the cursor follows the smoothed horizontal center of the
    /// bounding box of the user's joints. Because joints pop in and out of tracking, the box's
    /// left/right extents are run through a One Euro filter so the center doesn't jitter. Since
    /// the hands are included, raising one arm widens that side of the box and biases the center
    /// toward it — an intentional "lean/reach" affordance on top of "where the user stands".
    /// </summary>
    public class JointBoundsPointingSolver : IPointingSolver
    {
        static readonly nuitrack.JointType[] TrackedJoints =
        {
            nuitrack.JointType.Head, nuitrack.JointType.Torso, nuitrack.JointType.Waist,
            nuitrack.JointType.LeftShoulder, nuitrack.JointType.LeftElbow, nuitrack.JointType.LeftWrist, nuitrack.JointType.LeftHand,
            nuitrack.JointType.RightShoulder, nuitrack.JointType.RightElbow, nuitrack.JointType.RightWrist, nuitrack.JointType.RightHand,
        };

        readonly Dictionary<nuitrack.JointType, JointTracker> trackers = new Dictionary<nuitrack.JointType, JointTracker>();
        readonly JointFilterConfig jointFilter;
        readonly HorizontalPointingConfig horizontal;

        // (minX, maxX) smoothing at sensor cadence, mirroring the rest of the pipeline.
        readonly OneEuroFilterVector2 extentFilter = new OneEuroFilterVector2();
        Vector2 lastRawExtent;
        bool hasLastRawExtent;
        float accumulatedDt;

        public bool HasBody { get; private set; }
        public Vector3 BodyPosition { get; private set; }

        public JointBoundsPointingSolver(JointFilterConfig jointFilter, HorizontalPointingConfig horizontal)
        {
            this.jointFilter = jointFilter;
            this.horizontal = horizontal;
            foreach (var jt in TrackedJoints)
                trackers[jt] = new JointTracker();
        }

        public AimSample Update(in PointingFrame frame)
        {
            accumulatedDt += frame.Dt;

            UserData.SkeletonData skeleton = frame.User?.Skeleton;
            float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
            float ySum = 0f, zSum = 0f;
            int usable = 0;
            foreach (var jt in TrackedJoints)
            {
                JointTracker tracker = trackers[jt];
                if (skeleton != null)
                    tracker.Update(skeleton.GetJoint(jt), frame.RoomFromSensor, frame.Dt, jointFilter);
                else
                    tracker.UpdateMissing(frame.Dt, jointFilter);

                if (!tracker.IsUsable)
                    continue;
                Vector3 p = tracker.Position;
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                ySum += p.y;
                zSum += p.z;
                usable++;
            }

            if (usable == 0)
            {
                HasBody = false;
                extentFilter.Reset();
                hasLastRawExtent = false;
                return horizontal.ToHorizontalSample(false, BodyPosition, frame.HoverZ, frame.SelectZ);
            }

            var rawExtent = new Vector2(minX, maxX);
            // Bit-identical extents mean no new sensor frame (all trackers held their last
            // position): skip the filter so its speed estimate stays honest, and carry the dt
            // forward. Extrapolated joints move every frame and still flow through.
            bool newSample = !hasLastRawExtent || rawExtent != lastRawExtent;
            lastRawExtent = rawExtent;
            hasLastRawExtent = true;

            Vector2 smoothed = newSample
                ? extentFilter.Filter(rawExtent, accumulatedDt, horizontal.extentFilter)
                : (extentFilter.IsInitialized ? extentFilter.Value : rawExtent);
            if (newSample)
                accumulatedDt = 0f;

            float centerX = (smoothed.x + smoothed.y) * 0.5f;
            JointTracker torso = trackers[nuitrack.JointType.Torso];
            float y = torso.IsUsable ? torso.Position.y : ySum / usable;
            float z = torso.IsUsable ? torso.Position.z : zSum / usable;
            BodyPosition = new Vector3(centerX, y, z);
            HasBody = true;

            return horizontal.ToHorizontalSample(true, BodyPosition, frame.HoverZ, frame.SelectZ);
        }
    }
}
