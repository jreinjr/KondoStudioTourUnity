using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Kondo.Pointing;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Kondo.Debugging
{
    /// <summary>
    /// F2-toggled per-frame CSV recorder for the pointing pipeline: one row per user per
    /// render frame with the raw, filtered, and displayed cursor UV plus the current
    /// arm's joint positions, so jitter can be attributed to a pipeline stage offline.
    /// Files land in persistentDataPath/PointingLogs (path is logged on start).
    /// </summary>
    public class AimCsvRecorder : MonoBehaviour
    {
        const string Header =
            "time,frame,dt,userId,isActive,freshDataAgeMs,modelUsed,arm,quality,isPointing," +
            "hasScreenUV,onScreen,rawU,rawV,discarded,filterUpdated,filtU,filtV,dispU,dispV," +
            "rest01,magnetWeight," +
            "shoulderX,shoulderY,shoulderZ,shoulderQ,elbowX,elbowY,elbowZ,elbowQ," +
            "wristX,wristY,wristZ,wristQ,handX,handY,handZ,handQ";

        [Header("References")]
        public UserPointerManager pointerManager;

        [Header("Recording")]
        [Tooltip("Start a recording as soon as the scene starts.")]
        public bool recordOnStart = false;

        [Tooltip("Toggle recording with F2 at runtime.")]
        public bool hotkeyEnabled = true;

        [Min(0.1f)] public float flushIntervalSeconds = 1f;

        public bool IsRecording => writer != null;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        StreamWriter writer;
        readonly StringBuilder sb = new StringBuilder(512);
        float lastFlushTime;
        long rowCount;

        void Start()
        {
            if (recordOnStart)
                StartRecording();
        }

        // LateUpdate so every row sees the frame's final state: UserPointerManager has
        // filtered and SlideshowController has written magnet weights by then.
        void LateUpdate()
        {
#if ENABLE_INPUT_SYSTEM
            if (hotkeyEnabled && Keyboard.current != null && Keyboard.current.f2Key.wasPressedThisFrame)
            {
                if (IsRecording)
                    StopRecording();
                else
                    StartRecording();
            }
#endif
            if (writer == null || pointerManager == null)
                return;

            foreach (var kv in pointerManager.States)
                WriteRow(kv.Value);

            if (Time.unscaledTime - lastFlushTime >= flushIntervalSeconds)
            {
                writer.Flush();
                lastFlushTime = Time.unscaledTime;
            }
        }

        public void StartRecording()
        {
            if (IsRecording)
                return;

            string dir = Path.Combine(Application.persistentDataPath, "PointingLogs");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"aim_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            writer = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 16);
            writer.WriteLine(Header);
            lastFlushTime = Time.unscaledTime;
            rowCount = 0;
            Debug.Log($"[AimCsvRecorder] Recording to {path}");
        }

        public void StopRecording()
        {
            if (!IsRecording)
                return;
            writer.Flush();
            writer.Dispose();
            writer = null;
            Debug.Log($"[AimCsvRecorder] Recording stopped ({rowCount} rows).");
        }

        void OnDestroy() => StopRecording();
        void OnApplicationQuit() => StopRecording();

        void WriteRow(UserPointerManager.PointerState st)
        {
            AimSample s = st.Sample;
            sb.Clear();
            sb.Append(Time.time.ToString("F4", Inv)).Append(',');
            sb.Append(Time.frameCount).Append(',');
            sb.Append(Time.deltaTime.ToString("F5", Inv)).Append(',');
            sb.Append(st.UserId).Append(',');
            sb.Append(st.UserId == pointerManager.ActiveUserId ? '1' : '0').Append(',');
            if (!float.IsInfinity(st.Solver.FreshDataAge))
                sb.Append((st.Solver.FreshDataAge * 1000f).ToString("F1", Inv));
            sb.Append(',');
            sb.Append(s.ModelUsed).Append(',');
            sb.Append(s.Arm).Append(',');
            sb.Append(s.Quality.ToString("F3", Inv)).Append(',');
            sb.Append(s.IsPointing ? '1' : '0').Append(',');
            sb.Append(s.HasScreenUV ? '1' : '0').Append(',');
            sb.Append(s.OnScreen ? '1' : '0').Append(',');
            AppendUv(s.HasScreenUV, s.ScreenUV);
            sb.Append(st.LastSampleDiscarded ? '1' : '0').Append(',');
            sb.Append(st.FilterUpdatedThisFrame ? '1' : '0').Append(',');
            AppendUv(st.HasUv, st.Uv);
            AppendUv(st.HasUv, st.DisplayUv);
            sb.Append(st.Rest01.ToString("F3", Inv)).Append(',');
            sb.Append(st.MagnetWeight.ToString("F3", Inv)).Append(',');

            Arm arm = st.Solver.CurrentArm;
            bool left = arm == Arm.Left;
            AppendJoint(st, left ? nuitrack.JointType.LeftShoulder : nuitrack.JointType.RightShoulder);
            AppendJoint(st, left ? nuitrack.JointType.LeftElbow : nuitrack.JointType.RightElbow);
            AppendJoint(st, left ? nuitrack.JointType.LeftWrist : nuitrack.JointType.RightWrist);
            AppendJoint(st, left ? nuitrack.JointType.LeftHand : nuitrack.JointType.RightHand, trailingComma: false);

            writer.WriteLine(sb);
            rowCount++;
        }

        void AppendUv(bool has, Vector2 uv)
        {
            if (has)
            {
                sb.Append(uv.x.ToString("F5", Inv)).Append(',');
                sb.Append(uv.y.ToString("F5", Inv)).Append(',');
            }
            else
            {
                sb.Append(",,");
            }
        }

        void AppendJoint(UserPointerManager.PointerState st, nuitrack.JointType jt, bool trailingComma = true)
        {
            JointTracker tracker = st.Solver.Trackers[jt];
            if (tracker.IsUsable)
            {
                Vector3 p = tracker.Position;
                sb.Append(p.x.ToString("F4", Inv)).Append(',');
                sb.Append(p.y.ToString("F4", Inv)).Append(',');
                sb.Append(p.z.ToString("F4", Inv)).Append(',');
                sb.Append(tracker.Quality.ToString("F3", Inv));
            }
            else
            {
                sb.Append(",,,");
            }
            if (trailingComma)
                sb.Append(',');
        }
    }
}
