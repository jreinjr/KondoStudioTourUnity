using System.Collections.Generic;
using UnityEngine;
using Kondo.Core;
using NuitrackSDK;

namespace Kondo.Pointing
{
    /// <summary>
    /// Per-user aim solver: tracks the relevant joints, picks the pointing arm, computes
    /// the aim ray through a fallback chain of ray models, and runs the is-pointing
    /// state machine with enter/exit hysteresis. Plain class, one instance per user,
    /// driven by <see cref="UserPointerManager"/>.
    /// </summary>
    public class PointingArmSolver
    {
        static readonly nuitrack.JointType[] TrackedJoints =
        {
            nuitrack.JointType.Head, nuitrack.JointType.Torso,
            nuitrack.JointType.LeftShoulder, nuitrack.JointType.LeftElbow, nuitrack.JointType.LeftWrist, nuitrack.JointType.LeftHand,
            nuitrack.JointType.RightShoulder, nuitrack.JointType.RightElbow, nuitrack.JointType.RightWrist, nuitrack.JointType.RightHand,
        };

        public readonly Dictionary<nuitrack.JointType, JointTracker> Trackers = new Dictionary<nuitrack.JointType, JointTracker>();

        Arm currentArm = Arm.Right;
        float armSwitchTimer;

        readonly List<RayModel> chain = new List<RayModel>();
        RayModel chainPrimary = (RayModel)(-1);
        int chainIndex = -1;
        float modelUpgradeTimer;

        bool isPointing;
        float enterTimer;
        float exitTimer;
        float noRayTimer;

        public AimSample LastSample { get; private set; }
        public Vector3 TorsoPosition { get; private set; }
        public bool HasTorso { get; private set; }
        public float ArmScoreLeft { get; private set; }
        public float ArmScoreRight { get; private set; }
        public Arm CurrentArm => currentArm;
        /// <summary>Seconds since any tracked joint received fresh sensor data.</summary>
        public float FreshDataAge { get; private set; } = float.PositiveInfinity;

        public PointingArmSolver()
        {
            foreach (var jt in TrackedJoints)
                Trackers[jt] = new JointTracker();
        }

        public AimSample Update(UserData user, float dt, Matrix4x4 roomFromSensor, ProjectionScreen screen,
                                JointFilterConfig jf, RayModelConfig rm, PointingDetectionConfig pd)
        {
            UserData.SkeletonData skeleton = user?.Skeleton;
            float freshest = float.PositiveInfinity;
            foreach (var jt in TrackedJoints)
            {
                JointTracker tracker = Trackers[jt];
                if (skeleton != null)
                    tracker.Update(skeleton.GetJoint(jt), roomFromSensor, dt, jf);
                else
                    tracker.UpdateMissing(dt, jf);
                freshest = Mathf.Min(freshest, tracker.TimeSinceFreshData);
            }
            FreshDataAge = freshest;

            UpdateTorso();
            UpdateArmChoice(dt, rm);

            bool hasRay = SelectRay(dt, rm, out Ray ray, out float quality, out RayModel modelUsed);

            bool hasUV = false;
            Vector2 uv = default;
            Vector3 hit = default;
            bool onScreen = false;
            if (hasRay && screen != null)
                hasUV = screen.RaycastToUV(ray, out uv, out hit, out onScreen);

            UpdatePointingState(dt, hasRay, ray, onScreen, rm, pd);

            var sample = new AimSample
            {
                HasRay = hasRay,
                RoomRay = ray,
                ModelUsed = modelUsed,
                Arm = currentArm,
                Quality = quality,
                IsPointing = isPointing,
                HasScreenUV = hasUV,
                ScreenUV = uv,
                RoomHit = hit,
                OnScreen = onScreen,
            };
            LastSample = sample;
            return sample;
        }

        void UpdateTorso()
        {
            JointTracker torso = Trackers[nuitrack.JointType.Torso];
            if (torso.IsUsable)
            {
                TorsoPosition = torso.Position;
                HasTorso = true;
                return;
            }

            JointTracker ls = Trackers[nuitrack.JointType.LeftShoulder];
            JointTracker rs = Trackers[nuitrack.JointType.RightShoulder];
            if (ls.IsUsable && rs.IsUsable)
            {
                TorsoPosition = (ls.Position + rs.Position) * 0.5f;
                HasTorso = true;
            }
            else
            {
                HasTorso = false;
            }
        }

        void UpdateArmChoice(float dt, RayModelConfig rm)
        {
            ArmScoreLeft = ArmScore(Arm.Left, rm);
            ArmScoreRight = ArmScore(Arm.Right, rm);

            float current = currentArm == Arm.Left ? ArmScoreLeft : ArmScoreRight;
            float other = currentArm == Arm.Left ? ArmScoreRight : ArmScoreLeft;

            if (other > current + rm.armSwitchMargin)
                armSwitchTimer += dt;
            else
                armSwitchTimer = 0f;

            if (armSwitchTimer >= rm.armSwitchDwellSeconds)
            {
                currentArm = currentArm == Arm.Left ? Arm.Right : Arm.Left;
                armSwitchTimer = 0f;
                chainIndex = -1;
                modelUpgradeTimer = 0f;
            }
        }

        /// <summary>
        /// Heuristic "how much does this arm look like it's pointing at the wall" score,
        /// used only to compare the two arms against each other.
        /// </summary>
        float ArmScore(Arm arm, RayModelConfig rm)
        {
            JointTracker shoulder = Shoulder(arm);
            if (!shoulder.IsUsable)
                return 0f;

            JointTracker elbow = Elbow(arm);
            JointTracker hand = HandEnd(arm, rm);

            float score = 0f;
            if (hand.IsUsable)
            {
                float forward = shoulder.Position.z - hand.Position.z;
                score += Mathf.Clamp01(forward / 0.5f);

                if (elbow.IsUsable)
                {
                    float upper = (elbow.Position - shoulder.Position).magnitude;
                    float fore = (hand.Position - elbow.Position).magnitude;
                    float extension = (hand.Position - shoulder.Position).magnitude / Mathf.Max(upper + fore, 1e-3f);
                    score += Mathf.Clamp01((extension - 0.5f) / 0.5f) * 0.5f;
                }

                if (HasTorso)
                    score += Mathf.Clamp01((hand.Position.y - TorsoPosition.y + 0.2f) / 0.6f) * 0.3f;
            }
            else if (elbow.IsUsable)
            {
                float forward = shoulder.Position.z - elbow.Position.z;
                score += Mathf.Clamp01(forward / 0.3f) * 0.5f;
            }

            return score;
        }

        bool SelectRay(float dt, RayModelConfig rm, out Ray ray, out float quality, out RayModel modelUsed)
        {
            ray = default;
            quality = 0f;
            modelUsed = rm.primaryModel;

            if (chainPrimary != rm.primaryModel)
            {
                chainPrimary = rm.primaryModel;
                chain.Clear();
                chain.Add(rm.primaryModel);
                foreach (RayModel m in new[] { RayModel.ElbowToHand, RayModel.ShoulderToHand, RayModel.ShoulderToElbow })
                    if (!chain.Contains(m))
                        chain.Add(m);
                chainIndex = -1;
                modelUpgradeTimer = 0f;
            }

            int best = -1;
            Ray bestRay = default;
            float bestQuality = 0f;
            bool currentOk = false;
            Ray currentRay = default;
            float currentQuality = 0f;

            for (int i = 0; i < chain.Count; i++)
            {
                if (!TryBuildRay(chain[i], currentArm, rm, out Ray r, out float q))
                    continue;
                if (best < 0)
                {
                    best = i;
                    bestRay = r;
                    bestQuality = q;
                }
                if (i == chainIndex)
                {
                    currentOk = true;
                    currentRay = r;
                    currentQuality = q;
                }
            }

            if (best < 0)
            {
                chainIndex = -1;
                modelUpgradeTimer = 0f;
                return false;
            }

            if (chainIndex < 0 || chainIndex >= chain.Count || !currentOk)
            {
                // Nothing selected yet, or the current model just dropped out: take the
                // best available immediately (downgrades must not stall the cursor).
                chainIndex = best;
                modelUpgradeTimer = 0f;
                ray = bestRay;
                quality = bestQuality;
            }
            else if (best < chainIndex)
            {
                // A better model is back; require it to stay available for the dwell
                // before switching up, to avoid model flicker.
                modelUpgradeTimer += dt;
                if (modelUpgradeTimer >= rm.modelUpgradeDwellSeconds)
                {
                    chainIndex = best;
                    modelUpgradeTimer = 0f;
                    ray = bestRay;
                    quality = bestQuality;
                }
                else
                {
                    ray = currentRay;
                    quality = currentQuality;
                }
            }
            else
            {
                modelUpgradeTimer = 0f;
                ray = currentRay;
                quality = currentQuality;
            }

            modelUsed = chain[chainIndex];
            return true;
        }

        bool TryBuildRay(RayModel model, Arm arm, RayModelConfig rm, out Ray ray, out float quality)
        {
            ray = default;
            quality = 0f;

            JointTracker from, to;
            float penalty;
            switch (model)
            {
                case RayModel.ElbowToHand:
                    from = Elbow(arm); to = HandEnd(arm, rm); penalty = 1f;
                    break;
                case RayModel.ShoulderToHand:
                    from = Shoulder(arm); to = HandEnd(arm, rm); penalty = 0.9f;
                    break;
                case RayModel.HeadToHand:
                    from = Trackers[nuitrack.JointType.Head]; to = HandEnd(arm, rm); penalty = 0.9f;
                    break;
                case RayModel.ShoulderToElbow:
                    from = Shoulder(arm); to = Elbow(arm); penalty = 0.7f;
                    break;
                default:
                    return false;
            }

            if (!from.IsUsable || !to.IsUsable)
                return false;

            Vector3 segment = to.Position - from.Position;
            if (segment.magnitude < rm.minSegmentLengthMeters)
                return false;

            Vector3 dir = segment.normalized;
            // For the upper-arm fallback, push the origin out to roughly where the hand
            // would be so the wall hit doesn't jump backward when the hand drops out.
            Vector3 origin = model == RayModel.ShoulderToElbow
                ? to.Position + dir * rm.forearmLengthMeters
                : to.Position;

            ray = new Ray(origin, dir);
            quality = Mathf.Min(from.Quality, to.Quality) * penalty;
            return true;
        }

        void UpdatePointingState(float dt, bool hasRay, Ray ray, bool onScreen, RayModelConfig rm, PointingDetectionConfig pd)
        {
            JointTracker shoulder = Shoulder(currentArm);
            JointTracker elbow = Elbow(currentArm);
            JointTracker hand = HandEnd(currentArm, rm);

            bool canEvaluate = hasRay && shoulder.IsUsable && hand.IsUsable;
            if (canEvaluate)
            {
                noRayTimer = 0f;

                float extension = 1f;
                if (elbow.IsUsable)
                {
                    float upper = (elbow.Position - shoulder.Position).magnitude;
                    float fore = (hand.Position - elbow.Position).magnitude;
                    extension = (hand.Position - shoulder.Position).magnitude / Mathf.Max(upper + fore, 1e-3f);
                }

                float handAbove = HasTorso ? hand.Position.y - TorsoPosition.y : float.PositiveInfinity;
                float handForward = shoulder.Position.z - hand.Position.z;
                float towardWall = -ray.direction.z;

                if (!isPointing)
                {
                    bool enter = extension >= pd.enterExtensionRatio
                              && handAbove > pd.enterHandAboveTorsoMeters
                              && handForward > pd.enterHandForwardMeters
                              && towardWall > pd.enterRayTowardWall
                              && (!pd.requireScreenHitToEnter || onScreen);
                    if (enter)
                    {
                        enterTimer += dt;
                        if (enterTimer >= pd.enterDwellSeconds)
                        {
                            isPointing = true;
                            exitTimer = 0f;
                        }
                    }
                    else
                    {
                        enterTimer = 0f;
                    }
                }
                else
                {
                    bool violation = extension < pd.exitExtensionRatio
                                  || handAbove < pd.exitHandAboveTorsoMeters
                                  || handForward < pd.exitHandForwardMeters
                                  || towardWall < pd.exitRayTowardWall;
                    if (violation)
                    {
                        exitTimer += dt;
                        if (exitTimer >= pd.exitDwellSeconds)
                        {
                            isPointing = false;
                            enterTimer = 0f;
                        }
                    }
                    else
                    {
                        exitTimer = 0f;
                    }
                }
            }
            else if (hasRay)
            {
                // Degraded ray (e.g. shoulder→elbow, hand unknown): conditions can't be
                // evaluated, so hold the current pointing state.
                noRayTimer = 0f;
            }
            else
            {
                noRayTimer += dt;
                enterTimer = 0f;
                if (isPointing && noRayTimer >= pd.exitDwellSeconds)
                {
                    isPointing = false;
                    exitTimer = 0f;
                }
            }
        }

        JointTracker Shoulder(Arm arm) =>
            Trackers[arm == Arm.Left ? nuitrack.JointType.LeftShoulder : nuitrack.JointType.RightShoulder];

        JointTracker Elbow(Arm arm) =>
            Trackers[arm == Arm.Left ? nuitrack.JointType.LeftElbow : nuitrack.JointType.RightElbow];

        /// <summary>The "hand end" of rays: wrist or hand joint per config, with the other as backup.</summary>
        JointTracker HandEnd(Arm arm, RayModelConfig rm)
        {
            JointTracker wrist = Trackers[arm == Arm.Left ? nuitrack.JointType.LeftWrist : nuitrack.JointType.RightWrist];
            JointTracker hand = Trackers[arm == Arm.Left ? nuitrack.JointType.LeftHand : nuitrack.JointType.RightHand];
            JointTracker preferred = rm.useWristForHand ? wrist : hand;
            JointTracker backup = rm.useWristForHand ? hand : wrist;
            return preferred.IsUsable ? preferred : backup;
        }
    }
}
