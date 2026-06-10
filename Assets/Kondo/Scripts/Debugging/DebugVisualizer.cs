using System.Text;
using UnityEngine;
using UnityEngine.UI;
using Kondo.Core;
using Kondo.Pointing;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Kondo.Debugging
{
    /// <summary>
    /// Scene-view gizmos (sensor pose, screen rect, skeletons, aim rays) and an F1-toggled
    /// runtime stats overlay. A calibration fallback warning is shown even when the
    /// overlay is hidden.
    /// </summary>
    public class DebugVisualizer : MonoBehaviour
    {
        [Header("References")]
        public SensorPoseCalibrator calibrator;
        public ProjectionScreen screen;
        public UserPointerManager pointerManager;
        public Text statsText;

        [Header("Scene Gizmos")]
        public bool drawSensorPose = true;
        public bool drawScreenRect = true;
        public bool drawFloorGrid = true;
        public bool drawSkeletons = true;
        public bool drawRays = true;

        [Header("Stats Overlay")]
        public bool showStatsOverlay = true;
        [Tooltip("Toggle the overlay with F1 at runtime.")]
        public bool statsHotkeyEnabled = true;

        readonly StringBuilder sb = new StringBuilder(1024);
        float fpsSmoothed;

        void Update()
        {
#if ENABLE_INPUT_SYSTEM
            if (statsHotkeyEnabled && Keyboard.current != null && Keyboard.current.f1Key.wasPressedThisFrame)
                showStatsOverlay = !showStatsOverlay;
#endif
            float dt = Time.unscaledDeltaTime;
            if (dt > 0f)
                fpsSmoothed = Mathf.Lerp(fpsSmoothed, 1f / dt, 0.05f);

            if (statsText == null)
                return;

            bool warning = calibrator != null && calibrator.Source == SensorPoseCalibrator.PoseSource.ManualFallback;
            statsText.enabled = showStatsOverlay || warning;
            if (statsText.enabled)
                statsText.text = BuildStats(warningOnly: warning && !showStatsOverlay);
        }

        string BuildStats(bool warningOnly)
        {
            sb.Clear();

            if (calibrator != null)
                sb.AppendLine(calibrator.StatusText);
            if (warningOnly)
                return sb.ToString();

            sb.AppendLine($"render {fpsSmoothed:F0} fps");

            if (pointerManager != null)
            {
                sb.AppendLine($"active user: {(pointerManager.ActiveUserId >= 0 ? pointerManager.ActiveUserId.ToString() : "none")}");
                foreach (var kv in pointerManager.States)
                {
                    UserPointerManager.PointerState st = kv.Value;
                    AimSample s = st.Sample;
                    string marker = kv.Key == pointerManager.ActiveUserId ? "►" : " ";
                    sb.Append($"{marker} user {kv.Key}: ");
                    sb.Append(s.IsPointing ? "POINTING" : "idle    ");
                    sb.Append($"  arm={s.Arm}");
                    sb.Append(s.HasRay ? $"  model={s.ModelUsed}  q={s.Quality:F2}" : "  no ray");
                    if (!float.IsInfinity(st.Centrality))
                        sb.Append($"  center|x|={st.Centrality:F2}m");
                    float age = st.Solver.FreshDataAge;
                    sb.Append(float.IsInfinity(age) ? "  data: none" : $"  data age={age * 1000f:F0}ms");
                    if (s.HasScreenUV)
                        sb.Append($"  uv=({st.Uv.x:F2},{st.Uv.y:F2}){(s.OnScreen ? "" : " OFF")}");
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        void OnDrawGizmos()
        {
            if (calibrator == null || screen == null)
                return;

            Matrix4x4 roomFromSensor = Application.isPlaying && calibrator.IsCalibrated
                ? calibrator.RoomFromSensor
                : Matrix4x4.TRS(
                    new Vector3(0f, calibrator.mountHeightMeters, 0f),
                    Quaternion.Euler(calibrator.tiltDownDegrees, 0f, calibrator.rollDegrees),
                    Vector3.one);

            Vector3 sensorPos = roomFromSensor.MultiplyPoint3x4(Vector3.zero);

            if (drawSensorPose)
            {
                Gizmos.color = Color.red;
                Gizmos.DrawRay(sensorPos, roomFromSensor.MultiplyVector(Vector3.right) * 0.4f);
                Gizmos.color = Color.green;
                Gizmos.DrawRay(sensorPos, roomFromSensor.MultiplyVector(Vector3.up) * 0.4f);
                Gizmos.color = Color.blue;
                Gizmos.DrawRay(sensorPos, roomFromSensor.MultiplyVector(Vector3.forward) * 1.2f);
                Gizmos.color = Color.white;
                Gizmos.DrawWireSphere(sensorPos, 0.06f);
            }

            if (drawScreenRect)
            {
                Vector3 c = screen.ScreenCenter;
                Vector3 right = Vector3.right * screen.screenWidthMeters * 0.5f;
                Vector3 up = Vector3.up * screen.screenHeightMeters * 0.5f;
                Gizmos.color = Color.cyan;
                Gizmos.DrawLine(c - right - up, c + right - up);
                Gizmos.DrawLine(c + right - up, c + right + up);
                Gizmos.DrawLine(c + right + up, c - right + up);
                Gizmos.DrawLine(c - right + up, c - right - up);
                Gizmos.color = new Color(0f, 1f, 1f, 0.4f);
                Gizmos.DrawLine(c - right * 0.1f, c + right * 0.1f);
                Gizmos.DrawLine(c - up * 0.1f, c + up * 0.1f);
            }

            if (drawFloorGrid)
            {
                Gizmos.color = new Color(0.5f, 0.5f, 0.5f, 0.5f);
                for (int i = -3; i <= 3; i++)
                {
                    Gizmos.DrawLine(new Vector3(i, 0f, 0f), new Vector3(i, 0f, 6f));
                    Gizmos.DrawLine(new Vector3(-3f, 0f, i + 3f), new Vector3(3f, 0f, i + 3f));
                }
            }

            if (!Application.isPlaying || pointerManager == null)
                return;

            foreach (var kv in pointerManager.States)
            {
                UserPointerManager.PointerState st = kv.Value;
                bool isActive = kv.Key == pointerManager.ActiveUserId;

                if (drawSkeletons)
                {
                    foreach (JointTracker tracker in st.Solver.Trackers.Values)
                    {
                        if (!tracker.IsUsable)
                            continue;
                        Gizmos.color = Color.Lerp(Color.red, Color.green, tracker.Quality);
                        Gizmos.DrawSphere(tracker.Position, 0.035f);
                    }
                }

                if (drawRays && st.Sample.HasRay)
                {
                    Gizmos.color = isActive ? Color.yellow : new Color(0.6f, 0.6f, 0.6f, 0.8f);
                    Vector3 end = st.Sample.HasScreenUV
                        ? st.Sample.RoomHit
                        : st.Sample.RoomRay.origin + st.Sample.RoomRay.direction * 5f;
                    Gizmos.DrawLine(st.Sample.RoomRay.origin, end);
                    if (st.Sample.HasScreenUV)
                        Gizmos.DrawWireSphere(st.Sample.RoomHit, 0.05f);
                }
            }
        }
    }
}
