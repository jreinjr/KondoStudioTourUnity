using System.Collections.Generic;
using UnityEngine;

namespace Kondo.Core
{
    /// <summary>
    /// Establishes the sensor's pose relative to the room (origin on the floor directly
    /// below the lens, +Y up, +Z from the wall into the room) by averaging the Nuitrack
    /// floor plane, with a manual override / fallback. The pose is frozen once captured —
    /// live floor estimates jitter whenever people occlude the floor.
    /// </summary>
    public class SensorPoseCalibrator : MonoBehaviour
    {
        public enum PoseSource
        {
            None,
            FloorPlane,
            ManualOverride,
            ManualFallback,
            /// <summary>Exact pose replayed from a Nuitrack recording; wins over floor sampling and the manual override.</summary>
            Recording,
        }

        [Header("Manual Pose (used when Override Pose is on, or as a fallback)")]
        [Tooltip("Ignore the floor plane and use the manual values below. Use when the floor estimate is unavailable or untrusted.")]
        public bool overridePose = false;

        [Tooltip("Sensor lens height above the floor, meters (10 ft ≈ 3.05).")]
        [Min(0.2f)] public float mountHeightMeters = 3.05f;

        [Tooltip("Downward tilt of the sensor from horizontal, degrees.")]
        [Range(0f, 89f)] public float tiltDownDegrees = 30f;

        [Tooltip("Roll of the sensor around its view axis, degrees.")]
        [Range(-45f, 45f)] public float rollDegrees = 0f;

        [Header("Floor Auto-Calibration")]
        [Tooltip("Seconds of floor samples to collect before freezing the pose.")]
        [Min(0.5f)] public float warmupSeconds = 3f;

        [Tooltip("If no usable pose exists after this long, fall back to the manual values (a warning shows in the debug overlay).")]
        [Min(1f)] public float floorTimeoutSeconds = 10f;

        [Tooltip("Reject floor samples whose height differs from the running median by more than this (meters).")]
        [Min(0.01f)] public float outlierHeightMeters = 0.3f;

        [Tooltip("Reject floor samples whose normal differs from the running mean by more than this (degrees).")]
        [Min(0.1f)] public float outlierAngleDegrees = 5f;

        [Tooltip("Minimum accepted samples required before the pose snapshot is taken.")]
        [Min(1)] public int minSamples = 20;

        /// <summary>Maps sensor-space points (Nuitrack joint positions, meters) into room space.</summary>
        public Matrix4x4 RoomFromSensor { get; private set; } = Matrix4x4.identity;
        public bool IsCalibrated { get; private set; }
        public PoseSource Source { get; private set; } = PoseSource.None;
        public float CurrentHeight { get; private set; }
        public float CurrentTiltDegrees { get; private set; }
        public int SampleCount => heights.Count;

        readonly List<Vector3> normals = new List<Vector3>();
        readonly List<float> heights = new List<float>();
        readonly List<float> medianScratch = new List<float>();
        float elapsed;
        Vector3 lastSampledNormal;
        float lastSampledHeight;
        bool hasLastSample;

        public string StatusText
        {
            get
            {
                switch (Source)
                {
                    case PoseSource.FloorPlane:
                        return $"FLOOR-CALIBRATED  h={CurrentHeight:F2}m tilt={CurrentTiltDegrees:F1}°  ({SampleCount} samples)";
                    case PoseSource.ManualOverride:
                        return $"MANUAL OVERRIDE  h={CurrentHeight:F2}m tilt={CurrentTiltDegrees:F1}°";
                    case PoseSource.ManualFallback:
                        return $"!! FLOOR PLANE UNAVAILABLE — manual fallback  h={CurrentHeight:F2}m tilt={CurrentTiltDegrees:F1}° !!";
                    case PoseSource.Recording:
                        return $"RECORDED POSE  h={CurrentHeight:F2}m tilt={CurrentTiltDegrees:F1}°";
                    default:
                        return $"Calibrating from floor… {SampleCount}/{minSamples} samples, {elapsed:F1}s";
                }
            }
        }

        void Update()
        {
            // A replayed recording's pose is authoritative: suspend floor sampling AND the
            // manual override (which reapplies every frame) so neither stomps it. Recalibrate()
            // is the escape hatch back to live calibration.
            if (Source == PoseSource.Recording)
                return;

            if (overridePose)
            {
                // Reapply every frame so the inspector values can be tuned live.
                ApplyManual(PoseSource.ManualOverride);
                return;
            }

            if (IsCalibrated && Source == PoseSource.FloorPlane)
                return;

            elapsed += Time.deltaTime;
            TrySampleFloor();

            if (elapsed >= warmupSeconds && heights.Count >= minSamples)
                Snapshot();
            else if (elapsed >= floorTimeoutSeconds && !IsCalibrated)
                ApplyManual(PoseSource.ManualFallback);
        }

        /// <summary>Clears collected samples and re-runs the floor calibration.</summary>
        [ContextMenu("Recalibrate From Floor")]
        public void Recalibrate()
        {
            normals.Clear();
            heights.Clear();
            elapsed = 0f;
            hasLastSample = false;
            IsCalibrated = false;
            Source = PoseSource.None;
        }

        void TrySampleFloor()
        {
            if (NuitrackManager.sensorsData == null || NuitrackManager.sensorsData.Count == 0)
                return;

            Plane? floor = NuitrackManager.sensorsData[0].Floor;
            if (floor == null)
                return;

            Vector3 n = floor.Value.normal;
            float h = floor.Value.GetDistanceToPoint(Vector3.zero);
            if (n.sqrMagnitude < 1e-6f)
                return;

            n.Normalize();
            if (n.y < 0f)
            {
                n = -n;
                h = -h;
            }

            if (h < 0.2f || h > 10f)
                return;

            // Skip stale frames (the floor plane updates at sensor cadence, not render cadence).
            if (hasLastSample && n == lastSampledNormal && Mathf.Approximately(h, lastSampledHeight))
                return;
            lastSampledNormal = n;
            lastSampledHeight = h;
            hasLastSample = true;

            if (heights.Count >= 5)
            {
                if (Mathf.Abs(h - Median(heights)) > outlierHeightMeters)
                    return;
                if (Vector3.Angle(n, MeanNormal(normals)) > outlierAngleDegrees)
                    return;
            }

            normals.Add(n);
            heights.Add(h);
        }

        void Snapshot()
        {
            Vector3 n = MeanNormal(normals);
            float h = Median(heights);

            // Rotation taking the sensor-space floor normal to world up: captures pitch
            // and roll with no spurious yaw.
            Quaternion q = Quaternion.FromToRotation(n, Vector3.up);
            SetPose(new Vector3(0f, h, 0f), q, PoseSource.FloorPlane);
        }

        /// <summary>
        /// Apply the exact sensor pose captured in a Nuitrack recording (see
        /// Kondo.Recording.NuitrackRecordingPlayer). While <see cref="Source"/> is
        /// <see cref="PoseSource.Recording"/>, live floor sampling and the manual override are
        /// suspended so playback reproduces the recorded session's room frame exactly.
        /// </summary>
        public void ApplyRecordedPose(Vector3 position, Quaternion rotation)
        {
            SetPose(position, rotation, PoseSource.Recording);
        }

        void ApplyManual(PoseSource source)
        {
            Quaternion q = Quaternion.Euler(tiltDownDegrees, 0f, rollDegrees);
            SetPose(new Vector3(0f, mountHeightMeters, 0f), q, source);
        }

        void SetPose(Vector3 position, Quaternion rotation, PoseSource source)
        {
            RoomFromSensor = Matrix4x4.TRS(position, rotation, Vector3.one);
            IsCalibrated = true;
            Source = source;
            CurrentHeight = position.y;

            Vector3 fwd = rotation * Vector3.forward;
            CurrentTiltDegrees = Mathf.Asin(Mathf.Clamp(-fwd.y, -1f, 1f)) * Mathf.Rad2Deg;
        }

        float Median(List<float> values)
        {
            medianScratch.Clear();
            medianScratch.AddRange(values);
            medianScratch.Sort();
            int n = medianScratch.Count;
            return n % 2 == 1 ? medianScratch[n / 2] : (medianScratch[n / 2 - 1] + medianScratch[n / 2]) * 0.5f;
        }

        static Vector3 MeanNormal(List<Vector3> values)
        {
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < values.Count; i++)
                sum += values[i];
            return sum.sqrMagnitude > 1e-9f ? sum.normalized : Vector3.up;
        }
    }
}
