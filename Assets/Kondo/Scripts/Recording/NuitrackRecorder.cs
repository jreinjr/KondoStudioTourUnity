using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Kondo.Core;
using Kondo.Pointing;
using NuitrackSDK;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Kondo.Recording
{
    /// <summary>
    /// F3-toggled recorder for everything Nuitrack contributes to driving the experience:
    /// user skeletons + IDs, the floor plane, and the calibrated sensor pose, written as
    /// timestamped binary records (see <see cref="NuitrackRecordingFormat"/>) that
    /// <see cref="NuitrackRecordingPlayer"/> can replay through the whole pointing pipeline.
    /// Files land in persistentDataPath/NuitrackRecordings (path is logged on start).
    ///
    /// Reads the sensor directly, so it records valid data in any pointing mode — a session
    /// driven from MouseOverride still captures the skeletons in the space. Mouse input itself
    /// is deliberately NOT recorded; recordings capture the sensor, not the dev fallback.
    ///
    /// Runs in LateUpdate: NuitrackManager (execution order -100) freezes sensor data for the
    /// whole scripted frame, so what is written is exactly what UserPointerManager consumed.
    /// A record is only written when its data actually changed (the project's bit-identical
    /// "no new sensor frame" idiom), which keeps files at sensor cadence, not render cadence.
    /// </summary>
    public class NuitrackRecorder : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Source of calibration-pose events; the recorded pose is what playback reapplies verbatim.")]
        public SensorPoseCalibrator calibrator;

        [Tooltip("Optional guard: recording is refused while the manager is driven by a recording (never record a replay).")]
        public UserPointerManager pointerManager;

        [Header("Recording")]
        [Tooltip("Start a recording as soon as the scene starts.")]
        public bool recordOnStart = false;

        [Tooltip("Toggle recording with F3 at runtime.")]
        public bool hotkeyEnabled = true;

        [Min(0.1f)] public float flushIntervalSeconds = 1f;

        public bool IsRecording => writer != null;

        BinaryWriter writer;
        double startTime;
        float lastFlushTime;
        long frameCount, floorCount, poseCount;
        string currentPath;

        // Last-written state, for change detection. Joint data per user is packed as
        // [conf, x, y, z] * JointCount so comparison and reuse are allocation-free.
        readonly Dictionary<int, float[]> lastUserData = new Dictionary<int, float[]>();
        readonly List<int> userScratch = new List<int>();
        readonly List<UserData> presentScratch = new List<UserData>();
        float[] jointScratch;
        readonly float[] confScratch = new float[NuitrackRecordingFormat.JointCount];
        readonly Vector3[] realScratch = new Vector3[NuitrackRecordingFormat.JointCount];
        bool hasLastFloor;
        Vector3 lastFloorNormal, lastFloorPoint;
        bool hasLastPose;
        Vector3 lastPosePosition;
        Quaternion lastPoseRotation;
        SensorPoseCalibrator.PoseSource lastPoseSource;

        void Start()
        {
            if (recordOnStart)
                StartRecording();
        }

        void LateUpdate()
        {
#if ENABLE_INPUT_SYSTEM
            if (hotkeyEnabled && Keyboard.current != null && Keyboard.current.f3Key.wasPressedThisFrame)
            {
                if (IsRecording)
                    StopRecording();
                else
                    StartRecording();
            }
#endif
            if (writer == null)
                return;

            try
            {
                double t = Time.unscaledTimeAsDouble - startTime;
                WritePoseIfChanged(t);
                WriteFloorIfChanged(t);
                WriteSkeletonFrameIfChanged(t);

                if (Time.unscaledTime - lastFlushTime >= flushIntervalSeconds)
                {
                    writer.Flush();
                    lastFlushTime = Time.unscaledTime;
                }
            }
            catch (Exception ex)
            {
                // Never take down the kiosk over a recording failure (disk full, etc.).
                Debug.LogError($"[NuitrackRecorder] Write failed, stopping recording: {ex.Message}");
                StopRecording();
            }
        }

        public void StartRecording()
        {
            if (IsRecording)
                return;

            if (pointerManager != null && pointerManager.inputSource == NuitrackInputSource.NuitrackRecording)
            {
                Debug.LogWarning("[NuitrackRecorder] Refusing to record while the pointer manager is driven by a recording — switch Input Source to LiveSensor first.");
                return;
            }

            string dir = NuitrackRecordingFormat.DefaultDirectory;
            Directory.CreateDirectory(dir);
            currentPath = Path.Combine(dir, $"nuitrack_{DateTime.Now:yyyyMMdd_HHmmss}{NuitrackRecordingFormat.Extension}");
            writer = new BinaryWriter(new FileStream(currentPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16));
            NuitrackRecordingFormat.WriteHeader(writer);

            startTime = Time.unscaledTimeAsDouble;
            lastFlushTime = Time.unscaledTime;
            frameCount = floorCount = poseCount = 0;
            // Empty caches make the first LateUpdate a full t=0 snapshot (pose + floor + frame):
            // the calibrator typically froze long before recording started, so waiting for a
            // change would never capture the pose playback needs.
            lastUserData.Clear();
            hasLastFloor = false;
            hasLastPose = false;
            Debug.Log($"[NuitrackRecorder] Recording to {currentPath}");
        }

        public void StopRecording()
        {
            if (!IsRecording)
                return;
            try
            {
                writer.Flush();
                writer.Dispose();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NuitrackRecorder] Error closing recording: {ex.Message}");
            }
            writer = null;
            Debug.Log($"[NuitrackRecorder] Recording stopped: {frameCount} skeleton frames, {floorCount} floor samples, {poseCount} pose events → {currentPath}");
        }

        void OnDestroy() => StopRecording();
        void OnApplicationQuit() => StopRecording();

        void WritePoseIfChanged(double t)
        {
            if (calibrator == null || !calibrator.IsCalibrated)
                return;

            Matrix4x4 m = calibrator.RoomFromSensor;
            Vector3 pos = m.GetColumn(3);
            Quaternion rot = m.rotation; // TRS with identity scale, so exact

            if (hasLastPose && pos == lastPosePosition && rot == lastPoseRotation && calibrator.Source == lastPoseSource)
                return;
            lastPosePosition = pos;
            lastPoseRotation = rot;
            lastPoseSource = calibrator.Source;
            hasLastPose = true;

            NuitrackRecordingFormat.WriteCalibrationPose(writer, t, pos, rot, (byte)calibrator.Source);
            poseCount++;
        }

        void WriteFloorIfChanged(double t)
        {
            if (NuitrackManager.sensorsData == null || NuitrackManager.sensorsData.Count == 0)
                return;
            Plane? floor = NuitrackManager.sensorsData[0].Floor;
            if (floor == null)
                return;

            Vector3 normal = floor.Value.normal;
            // Recover the plane's reference point: Plane stores normal + distance, and the
            // closest point to the origin reproduces the same plane on reconstruction.
            Vector3 point = floor.Value.ClosestPointOnPlane(Vector3.zero);
            if (hasLastFloor && normal == lastFloorNormal && point == lastFloorPoint)
                return;
            lastFloorNormal = normal;
            lastFloorPoint = point;
            hasLastFloor = true;

            NuitrackRecordingFormat.WriteFloor(writer, t, normal, point);
            floorCount++;
        }

        void WriteSkeletonFrameIfChanged(double t)
        {
            if (NuitrackManager.sensorsData == null || NuitrackManager.sensorsData.Count == 0)
                return;

            // Membership matches what UserPointerManager consumes: users with a skeleton.
            presentScratch.Clear();
            foreach (UserData user in NuitrackManager.sensorsData[0].Users)
                if (user != null && user.Skeleton != null)
                    presentScratch.Add(user);

            int stride = 4 * NuitrackRecordingFormat.JointCount; // [conf, x, y, z] per joint
            jointScratch ??= new float[stride];

            bool changed = presentScratch.Count != lastUserData.Count;
            foreach (UserData user in presentScratch)
            {
                PackJoints(user, jointScratch);
                if (!lastUserData.TryGetValue(user.ID, out float[] last))
                {
                    changed = true;
                    last = new float[stride];
                    lastUserData[user.ID] = last;
                }
                else if (!changed)
                {
                    for (int i = 0; i < stride; i++)
                        if (jointScratch[i] != last[i]) { changed = true; break; }
                }
                Array.Copy(jointScratch, last, stride);
            }

            // Drop cache entries for users that left (their absence is itself a change).
            userScratch.Clear();
            foreach (int id in lastUserData.Keys)
            {
                bool present = false;
                foreach (UserData user in presentScratch)
                    if (user.ID == id) { present = true; break; }
                if (!present)
                    userScratch.Add(id);
            }
            foreach (int id in userScratch)
            {
                lastUserData.Remove(id);
                changed = true;
            }

            if (!changed)
                return;

            NuitrackRecordingFormat.BeginSkeletonFrame(writer, t, presentScratch.Count);
            foreach (UserData user in presentScratch)
            {
                float[] packed = lastUserData[user.ID];
                for (int j = 0; j < NuitrackRecordingFormat.JointCount; j++)
                {
                    confScratch[j] = packed[j * 4];
                    realScratch[j] = new Vector3(packed[j * 4 + 1], packed[j * 4 + 2], packed[j * 4 + 3]);
                }
                NuitrackRecordingFormat.WriteSkeletonFrameUser(writer, user.ID, confScratch, realScratch);
            }
            frameCount++;
        }

        static void PackJoints(UserData user, float[] into)
        {
            for (int j = 0; j < NuitrackRecordingFormat.JointCount; j++)
            {
                UserData.SkeletonData.Joint joint = user.Skeleton.GetJoint((nuitrack.JointType)j);
                if (joint != null)
                {
                    nuitrack.Vector3 real = joint.RawJoint.Real;
                    into[j * 4] = joint.Confidence;
                    into[j * 4 + 1] = real.X;
                    into[j * 4 + 2] = real.Y;
                    into[j * 4 + 3] = real.Z;
                }
                else
                {
                    into[j * 4] = 0f;
                    into[j * 4 + 1] = 0f;
                    into[j * 4 + 2] = 0f;
                    into[j * 4 + 3] = 0f;
                }
            }
        }
    }
}
