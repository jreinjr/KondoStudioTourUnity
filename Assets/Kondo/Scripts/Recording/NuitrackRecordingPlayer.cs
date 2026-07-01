using System;
using System.Collections.Generic;
using UnityEngine;
using Kondo.Core;
using NuitrackSDK;

namespace Kondo.Recording
{
    /// <summary>
    /// Replays a <see cref="NuitrackRecorder"/> file (.knrec) as the pointing system's user
    /// source: reconstructs real <see cref="UserData"/>/<c>nuitrack.Skeleton</c> objects from
    /// the recorded joints, so the entire live pipeline (JointTracker → solvers → filters →
    /// active-user selection → slideshow) runs unmodified on the recorded session. Select it
    /// via <c>UserPointerManager.inputSource = NuitrackRecording</c>.
    ///
    /// The recorded calibration pose is applied verbatim to the <see cref="SensorPoseCalibrator"/>
    /// (no warmup nondeterminism); between recorded frames the exposed users are left untouched,
    /// so their bit-identical values register as "no new sensor frame" downstream — exactly like
    /// the live ~30 Hz sensor under a faster render rate.
    ///
    /// Executes after NuitrackManager (-100) and before the calibrator/manager (default 0), so
    /// each frame's recorded users and pose are in place before anything consumes them.
    /// </summary>
    [DefaultExecutionOrder(-90)]
    public class NuitrackRecordingPlayer : MonoBehaviour
    {
        [Header("References")]
        public SensorPoseCalibrator calibrator;

        [Header("Playback")]
        [Tooltip("Recording to play: an absolute path, or a filename/relative path resolved against persistentDataPath/NuitrackRecordings.")]
        public string filePath;

        [Tooltip("Load and play the recording as soon as the scene starts.")]
        public bool playOnStart = true;

        [Tooltip("Restart from the beginning when the recording ends (otherwise playback stops and the users leave).")]
        public bool loop = false;

        public bool IsLoaded => data != null;
        public bool IsPlaying { get; private set; }
        public double PlaybackTime => clock;
        public double Duration => data?.Duration ?? 0.0;

        /// <summary>Reconstructed users of the latest applied skeleton frame; empty when stopped.</summary>
        public IReadOnlyList<UserData> Users => userList;

        /// <summary>Latest recorded floor plane (stored for completeness; the recorded pose drives calibration).</summary>
        public Plane? Floor { get; private set; }

        NuitrackRecordingFormat.RecordingData data;
        string loadedPath;
        double clock;
        int nextRecord;
        readonly Dictionary<int, UserData> users = new Dictionary<int, UserData>();
        readonly List<UserData> userList = new List<UserData>();
        readonly List<int> removalScratch = new List<int>();
        static bool skeletonRoundTripChecked;

        void Start()
        {
            if (playOnStart && TryLoad(filePath))
                Play();
        }

        void Update()
        {
            if (!IsPlaying || data == null)
                return;

            clock += Time.unscaledDeltaTime;
            ApplyRecordsUpTo(clock);

            if (nextRecord >= data.Records.Count && clock >= data.Duration)
            {
                if (loop)
                {
                    ResetPlayback();
                }
                else
                {
                    Debug.Log($"[NuitrackRecordingPlayer] Recording finished ({data.Duration:F1}s): {loadedPath}");
                    Stop();
                }
            }
        }

        /// <summary>
        /// Load a recording (absolute path, or relative to the recordings directory).
        /// Returns false with a single logged error on failure.
        /// </summary>
        public bool TryLoad(string path)
        {
            Stop();
            data = null;
            loadedPath = null;

            string resolved = NuitrackRecordingFormat.ResolvePath(path);
            if (resolved == null)
            {
                Debug.LogError("[NuitrackRecordingPlayer] No recording file path set.");
                return false;
            }

            try
            {
                data = NuitrackRecordingFormat.Load(resolved);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NuitrackRecordingPlayer] Failed to load recording: {ex.Message}");
                data = null;
                return false;
            }

            loadedPath = resolved;
            if (data.Truncated)
                Debug.LogWarning($"[NuitrackRecordingPlayer] Recording has a truncated final record (crash mid-write?); playing the intact {data.Records.Count} records.");
            if (!data.HasCalibrationPose)
                Debug.LogWarning("[NuitrackRecordingPlayer] Recording contains no calibration pose — playback will stay uncalibrated (no cursors), like an uncalibrated live session.");
            Debug.Log($"[NuitrackRecordingPlayer] Loaded {data.Records.Count} records, {data.Duration:F1}s, recorded {data.StartUtc:yyyy-MM-dd HH:mm} UTC: {resolved}");

            VerifySkeletonRoundTrip();
            ResetPlayback();
            return true;
        }

        public void Play()
        {
            if (data == null && !TryLoad(filePath))
                return;
            IsPlaying = true;
        }

        /// <summary>Freeze the clock; exposed users hold their values (downstream sees a sensor stall).</summary>
        public void Pause() => IsPlaying = false;

        /// <summary>Stop and clear the exposed users (everyone "leaves"); the applied recorded pose stays.</summary>
        public void Stop()
        {
            IsPlaying = false;
            ResetPlayback();
        }

        public void Restart()
        {
            if (data == null)
            {
                Play();
                return;
            }
            ResetPlayback();
            IsPlaying = true;
        }

        void ResetPlayback()
        {
            clock = 0.0;
            nextRecord = 0;
            users.Clear();
            userList.Clear();
            Floor = null;
        }

        void ApplyRecordsUpTo(double time)
        {
            // File order preserves the recorder's pose-before-frame ordering; on a slow render
            // frame several records apply back-to-back and the latest wins, matching the live
            // "latest sensor frame only" semantics.
            while (nextRecord < data.Records.Count && data.Records[nextRecord].Time <= time)
            {
                NuitrackRecordingFormat.Record record = data.Records[nextRecord++];
                switch (record.Kind)
                {
                    case NuitrackRecordingFormat.RecordKind.CalibrationPose:
                        if (calibrator != null)
                            calibrator.ApplyRecordedPose(record.PosePosition, record.PoseRotation);
                        break;

                    case NuitrackRecordingFormat.RecordKind.Floor:
                        Floor = new Plane(record.FloorNormal, record.FloorPoint);
                        break;

                    case NuitrackRecordingFormat.RecordKind.SkeletonFrame:
                        ApplySkeletonFrame(record);
                        break;
                }
            }
        }

        void ApplySkeletonFrame(NuitrackRecordingFormat.Record record)
        {
            // Mirrors NuitrackSDK.Users.UpdateData: one persistent UserData per id, a fresh
            // skeleton per sensor frame, users removed when their id disappears. Between frames
            // nothing is touched, so downstream dedup sees bit-identical "held" joints.
            foreach (NuitrackRecordingFormat.RecordedUser recordedUser in record.Users)
            {
                if (!users.TryGetValue(recordedUser.Id, out UserData user))
                {
                    user = new UserData(recordedUser.Id);
                    users[recordedUser.Id] = user;
                }
                user.AddData(NuitrackRecordingFormat.BuildSkeleton(recordedUser.Id, recordedUser.Confidences, recordedUser.RealMm));
            }

            removalScratch.Clear();
            foreach (int id in users.Keys)
            {
                bool present = false;
                foreach (NuitrackRecordingFormat.RecordedUser recordedUser in record.Users)
                    if (recordedUser.Id == id) { present = true; break; }
                if (!present)
                    removalScratch.Add(id);
            }
            foreach (int id in removalScratch)
                users.Remove(id);

            userList.Clear();
            foreach (UserData user in users.Values)
                userList.Add(user);
        }

        /// <summary>
        /// One-time sanity check that the native nuitrack.Skeleton.GetJoint returns the joints
        /// we author (its lookup strategy lives in the closed-source dll).
        /// </summary>
        static void VerifySkeletonRoundTrip()
        {
            if (skeletonRoundTripChecked)
                return;
            skeletonRoundTripChecked = true;

            var confidences = new float[NuitrackRecordingFormat.JointCount];
            var realMm = new Vector3[NuitrackRecordingFormat.JointCount];
            for (int i = 0; i < NuitrackRecordingFormat.JointCount; i++)
            {
                confidences[i] = 0.75f;
                realMm[i] = new Vector3(100f * i, 200f, 3000f);
            }
            nuitrack.Skeleton skeleton = NuitrackRecordingFormat.BuildSkeleton(1, confidences, realMm);
            nuitrack.Joint joint = skeleton.GetJoint(nuitrack.JointType.RightHand);
            if (joint.Type != nuitrack.JointType.RightHand || joint.Real.X != 100f * (int)nuitrack.JointType.RightHand)
                Debug.LogError("[NuitrackRecordingPlayer] Reconstructed skeleton failed the GetJoint round-trip — the nuitrack dll's joint lookup does not match the recorded layout; playback data would be wrong.");
        }
    }
}
