using UnityEngine;
using Kondo.Core;

namespace Kondo.Pointing
{
    /// <summary>
    /// Tracks one skeleton joint in room space: confidence gating, One Euro filtering at
    /// sensor-frame cadence (Nuitrack runs ~30 Hz while Unity renders faster, so stale
    /// frames must not feed the filter's derivative), and short velocity extrapolation
    /// across dropouts.
    /// </summary>
    public class JointTracker
    {
        readonly OneEuroFilterVector3 filter = new OneEuroFilterVector3();

        Vector3 lastSensorPos;
        bool hasSensorPos;

        Vector3 position;
        Vector3 velocity;
        float timeSinceValid = float.PositiveInfinity;
        float accumulatedDt;
        bool everValid;
        float maxExtrapolation = 0.3f;

        /// <summary>Filtered (or extrapolated) joint position in room space.</summary>
        public Vector3 Position => position;
        public Vector3 Velocity => velocity;

        /// <summary>True while the joint is tracked or within the extrapolation window.</summary>
        public bool IsUsable => everValid && timeSinceValid <= maxExtrapolation;

        /// <summary>1 while live, ramping to 0 as extrapolation ages out.</summary>
        public float Quality => !IsUsable ? 0f : Mathf.Clamp01(1f - timeSinceValid / Mathf.Max(maxExtrapolation, 1e-3f));

        /// <summary>Seconds since the last genuinely new sensor sample for this joint.</summary>
        public float TimeSinceFreshData { get; private set; } = float.PositiveInfinity;

        public void Update(NuitrackSDK.UserData.SkeletonData.Joint joint, Matrix4x4 roomFromSensor, float dt, JointFilterConfig cfg)
        {
            maxExtrapolation = cfg.maxExtrapolationSeconds;
            accumulatedDt += dt;
            TimeSinceFreshData += dt;

            bool valid = false;
            Vector3 sensorPos = default;
            if (joint != null)
            {
                sensorPos = joint.Position;
                valid = joint.Confidence >= cfg.minConfidence && (!cfg.requireGoodDepth || joint.IsGoodDepth);
            }

            // Identical raw position means Nuitrack hasn't delivered a new frame yet.
            bool newFrame = valid && (!hasSensorPos || sensorPos != lastSensorPos);
            if (valid)
            {
                lastSensorPos = sensorPos;
                hasSensorPos = true;
            }

            if (newFrame)
            {
                Vector3 raw = roomFromSensor.MultiplyPoint3x4(sensorPos);
                Vector3 prev = position;
                bool wasUsable = IsUsable;
                position = filter.Filter(raw, accumulatedDt, cfg.filter);
                velocity = wasUsable ? (position - prev) / Mathf.Max(accumulatedDt, 1e-4f) : Vector3.zero;
                everValid = true;
                timeSinceValid = 0f;
                accumulatedDt = 0f;
                TimeSinceFreshData = 0f;
            }
            else if (valid)
            {
                // Tracked, just no new sensor frame this render frame: hold.
                timeSinceValid = 0f;
            }
            else
            {
                timeSinceValid += dt;
                if (everValid && timeSinceValid <= cfg.maxExtrapolationSeconds)
                {
                    position += velocity * dt;
                    velocity *= Mathf.Pow(0.5f, dt / Mathf.Max(cfg.extrapolationHalfLifeSeconds, 1e-3f));
                }
                else
                {
                    // Lost for good: reset so reacquisition snaps instead of dragging across the room.
                    filter.Reset();
                    velocity = Vector3.zero;
                }
            }
        }

        public void UpdateMissing(float dt, JointFilterConfig cfg)
        {
            Update(null, Matrix4x4.identity, dt, cfg);
        }
    }
}
