using System.IO;
using UnityEditor;
using UnityEngine;
using Kondo.Recording;

namespace Kondo.EditorTools
{
    /// <summary>
    /// Editor utilities for the Nuitrack record/replay system: a synthetic-recording
    /// generator so the whole playback path (reconstruction → solvers → cursor → slideshow)
    /// can be exercised with zero sensor hardware, and a player inspector with a file
    /// browser + play-mode transport controls.
    /// </summary>
    public static class NuitrackRecordingTools
    {
        const float DurationSeconds = 30f;
        const float FrameRate = 30f;
        const float MountHeight = 3.05f;   // the calibrator's manual-pose defaults
        const float TiltDown = 30f;

        /// <summary>
        /// Write a synthetic .knrec: the t=0 calibration pose + floor, then ~30 s of one
        /// user (id 1) strolling laterally while pointing a fully extended right arm at a
        /// Lissajous target on the screen, authored in room space and converted to sensor
        /// space through the inverse of the recorded pose — exactly what a real sensor
        /// session produces, so the reconstructed pipeline recovers the room-space motion.
        /// </summary>
        [MenuItem("Kondo/Write Synthetic Nuitrack Recording")]
        public static void WriteSyntheticRecording()
        {
            string dir = NuitrackRecordingFormat.DefaultDirectory;
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "synthetic_walk" + NuitrackRecordingFormat.Extension);

            Matrix4x4 roomFromSensor = Matrix4x4.TRS(
                new Vector3(0f, MountHeight, 0f), Quaternion.Euler(TiltDown, 0f, 0f), Vector3.one);
            Matrix4x4 sensorFromRoom = roomFromSensor.inverse;

            var confidences = new float[NuitrackRecordingFormat.JointCount];
            var realMm = new Vector3[NuitrackRecordingFormat.JointCount];

            using (var writer = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write)))
            {
                NuitrackRecordingFormat.WriteHeader(writer);
                NuitrackRecordingFormat.WriteCalibrationPose(writer, 0.0,
                    new Vector3(0f, MountHeight, 0f), Quaternion.Euler(TiltDown, 0f, 0f),
                    (byte)Kondo.Core.SensorPoseCalibrator.PoseSource.ManualOverride);

                // Sensor-space floor plane, consistent with the pose (informational; playback
                // applies the pose directly).
                Vector3 floorNormalSensor = sensorFromRoom.MultiplyVector(Vector3.up).normalized;
                Vector3 floorPointSensor = sensorFromRoom.MultiplyPoint3x4(Vector3.zero);
                NuitrackRecordingFormat.WriteFloor(writer, 0.0, floorNormalSensor, floorPointSensor);

                int frames = Mathf.RoundToInt(DurationSeconds * FrameRate);
                for (int f = 0; f < frames; f++)
                {
                    double t = f / (double)FrameRate;
                    AuthorFrame((float)t, sensorFromRoom, confidences, realMm);
                    NuitrackRecordingFormat.BeginSkeletonFrame(writer, t, 1);
                    NuitrackRecordingFormat.WriteSkeletonFrameUser(writer, 1, confidences, realMm);
                }

                // The user leaves at the end, so non-looping playback exercises the loss-grace fade.
                NuitrackRecordingFormat.BeginSkeletonFrame(writer, DurationSeconds, 0);
            }

            Debug.Log($"[NuitrackRecordingTools] Synthetic recording written: {path}\n" +
                      "Set UserPointerManager Input Source = NuitrackRecording, point the Recording Player at this file, and enter Play.");
        }

        /// <summary>Author one 30 Hz pose of the synthetic user in room space, output as sensor-space millimeters.</summary>
        static void AuthorFrame(float t, Matrix4x4 sensorFromRoom, float[] confidences, Vector3[] realMm)
        {
            // Body: slow lateral stroll, drifting between the Hover and Select depth zones.
            float bodyX = 0.8f * Mathf.Sin(2f * Mathf.PI * t / 20f);
            float bodyZ = 2.0f + 0.6f * Mathf.Sin(2f * Mathf.PI * t / 15f);

            Vector3 torso = new Vector3(bodyX, 1.2f, bodyZ);
            Vector3 waist = new Vector3(bodyX, 1.0f, bodyZ);
            Vector3 neck = new Vector3(bodyX, 1.5f, bodyZ);
            Vector3 head = new Vector3(bodyX, 1.65f, bodyZ);

            // Aim: fully extended right arm at a Lissajous target on the screen (center ~(0, 1.625, -0.05)).
            Vector3 target = new Vector3(
                1.2f * Mathf.Sin(2f * Mathf.PI * t / 8f),
                1.625f + 0.6f * Mathf.Sin(2f * Mathf.PI * t / 5f),
                -0.05f);
            Vector3 rShoulder = torso + new Vector3(0.18f, 0.25f, 0f);
            Vector3 aimDir = (target - rShoulder).normalized;
            Vector3 rElbow = rShoulder + aimDir * 0.30f;
            Vector3 rWrist = rShoulder + aimDir * 0.55f;
            Vector3 rHand = rShoulder + aimDir * 0.62f;

            // Left arm hangs.
            Vector3 lShoulder = torso + new Vector3(-0.18f, 0.25f, 0f);
            Vector3 lElbow = lShoulder + new Vector3(-0.02f, -0.30f, 0f);
            Vector3 lWrist = lShoulder + new Vector3(-0.02f, -0.50f, 0f);
            Vector3 lHand = lShoulder + new Vector3(-0.02f, -0.57f, 0f);

            for (int i = 0; i < NuitrackRecordingFormat.JointCount; i++)
            {
                confidences[i] = 0f;
                realMm[i] = Vector3.zero;
            }

            void Set(nuitrack.JointType type, Vector3 room)
            {
                int i = (int)type;
                confidences[i] = 0.9f;
                realMm[i] = sensorFromRoom.MultiplyPoint3x4(room) * 1000f;
            }

            Set(nuitrack.JointType.Head, head);
            Set(nuitrack.JointType.Neck, neck);
            Set(nuitrack.JointType.Torso, torso);
            Set(nuitrack.JointType.Waist, waist);
            Set(nuitrack.JointType.LeftShoulder, lShoulder);
            Set(nuitrack.JointType.LeftElbow, lElbow);
            Set(nuitrack.JointType.LeftWrist, lWrist);
            Set(nuitrack.JointType.LeftHand, lHand);
            Set(nuitrack.JointType.RightShoulder, rShoulder);
            Set(nuitrack.JointType.RightElbow, rElbow);
            Set(nuitrack.JointType.RightWrist, rWrist);
            Set(nuitrack.JointType.RightHand, rHand);
            Set(nuitrack.JointType.LeftHip, new Vector3(bodyX - 0.12f, 0.95f, bodyZ));
            Set(nuitrack.JointType.LeftKnee, new Vector3(bodyX - 0.12f, 0.50f, bodyZ));
            Set(nuitrack.JointType.LeftAnkle, new Vector3(bodyX - 0.12f, 0.10f, bodyZ));
            Set(nuitrack.JointType.RightHip, new Vector3(bodyX + 0.12f, 0.95f, bodyZ));
            Set(nuitrack.JointType.RightKnee, new Vector3(bodyX + 0.12f, 0.50f, bodyZ));
            Set(nuitrack.JointType.RightAnkle, new Vector3(bodyX + 0.12f, 0.10f, bodyZ));
        }
    }

    /// <summary>
    /// Player inspector: Browse button for the recording path (stores just the filename when
    /// the file lives in the default recordings directory) and play-mode transport controls.
    /// </summary>
    [CustomEditor(typeof(NuitrackRecordingPlayer))]
    public class NuitrackRecordingPlayerEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var player = (NuitrackRecordingPlayer)target;

            if (GUILayout.Button("Browse…"))
            {
                string dir = NuitrackRecordingFormat.DefaultDirectory;
                Directory.CreateDirectory(dir);
                string picked = EditorUtility.OpenFilePanel(
                    "Select Nuitrack recording", dir, NuitrackRecordingFormat.Extension.TrimStart('.'));
                if (!string.IsNullOrEmpty(picked))
                {
                    string fullDir = Path.GetFullPath(dir);
                    string fullPicked = Path.GetFullPath(picked);
                    Undo.RecordObject(player, "Set recording path");
                    player.filePath = fullPicked.StartsWith(fullDir)
                        ? Path.GetFileName(fullPicked)
                        : fullPicked;
                    EditorUtility.SetDirty(player);
                }
            }

            if (!Application.isPlaying)
                return;

            EditorGUILayout.Space();
            string status = !player.IsLoaded ? "No recording loaded"
                : $"{(player.IsPlaying ? "Playing" : "Paused/stopped")}  {player.PlaybackTime:F1}s / {player.Duration:F1}s  ({player.Users.Count} users)";
            EditorGUILayout.HelpBox(status, MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Play")) player.Play();
                if (GUILayout.Button("Pause")) player.Pause();
                if (GUILayout.Button("Restart")) player.Restart();
                if (GUILayout.Button("Stop")) player.Stop();
            }
            Repaint(); // keep the time readout live
        }
    }
}
