using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Kondo.Core;
using Kondo.Pointing;
using NuitrackSDK;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Kondo.Debugging
{
    /// <summary>
    /// Runtime on-screen debug overlay (works in builds, unlike scene gizmos): draws every
    /// detected user's skeleton — joints as dots, bones as lines — orthographically projected
    /// onto the screen via <see cref="ProjectionScreen.RoomToScreenUV"/>, plus the BoxCursor
    /// interaction box for each user. Toggled with F2. Lives on the debug canvas; pools UI
    /// Images so it allocates nothing per frame.
    /// </summary>
    public class SkeletonDebugOverlay : MonoBehaviour
    {
        [Header("References")]
        public SensorPoseCalibrator calibrator;
        public ProjectionScreen screen;
        public UserPointerManager pointerManager;
        [Tooltip("Full-screen container the dots/lines are drawn into (anchored stretch, pivot center).")]
        public RectTransform container;

        [Header("Display")]
        public bool show = false;
        [Tooltip("Toggle the overlay with F2 at runtime.")]
        public bool hotkeyEnabled = true;
        [Min(0f)] public float jointSizeDesign = 22f;
        [Min(0f)] public float boneThicknessDesign = 7f;
        [Min(0f)] public float boxThicknessDesign = 5f;
        [Range(0f, 1f)] public float confidenceThreshold = 0.15f;
        public Color boxColor = new Color(0f, 1f, 1f, 0.9f);
        public Color fallbackUserColor = new Color(0.4f, 1f, 0.6f, 0.95f);

        static readonly nuitrack.JointType[] Joints =
        {
            nuitrack.JointType.Head, nuitrack.JointType.Neck, nuitrack.JointType.Torso, nuitrack.JointType.Waist,
            nuitrack.JointType.LeftShoulder, nuitrack.JointType.LeftElbow, nuitrack.JointType.LeftWrist, nuitrack.JointType.LeftHand,
            nuitrack.JointType.RightShoulder, nuitrack.JointType.RightElbow, nuitrack.JointType.RightWrist, nuitrack.JointType.RightHand,
            nuitrack.JointType.LeftHip, nuitrack.JointType.LeftKnee, nuitrack.JointType.LeftAnkle,
            nuitrack.JointType.RightHip, nuitrack.JointType.RightKnee, nuitrack.JointType.RightAnkle,
        };

        static readonly (nuitrack.JointType, nuitrack.JointType)[] Bones =
        {
            (nuitrack.JointType.Head, nuitrack.JointType.Neck),
            (nuitrack.JointType.Neck, nuitrack.JointType.Torso),
            (nuitrack.JointType.Torso, nuitrack.JointType.Waist),
            (nuitrack.JointType.Neck, nuitrack.JointType.LeftShoulder),
            (nuitrack.JointType.LeftShoulder, nuitrack.JointType.LeftElbow),
            (nuitrack.JointType.LeftElbow, nuitrack.JointType.LeftWrist),
            (nuitrack.JointType.LeftWrist, nuitrack.JointType.LeftHand),
            (nuitrack.JointType.Neck, nuitrack.JointType.RightShoulder),
            (nuitrack.JointType.RightShoulder, nuitrack.JointType.RightElbow),
            (nuitrack.JointType.RightElbow, nuitrack.JointType.RightWrist),
            (nuitrack.JointType.RightWrist, nuitrack.JointType.RightHand),
            (nuitrack.JointType.Waist, nuitrack.JointType.LeftHip),
            (nuitrack.JointType.LeftHip, nuitrack.JointType.LeftKnee),
            (nuitrack.JointType.LeftKnee, nuitrack.JointType.LeftAnkle),
            (nuitrack.JointType.Waist, nuitrack.JointType.RightHip),
            (nuitrack.JointType.RightHip, nuitrack.JointType.RightKnee),
            (nuitrack.JointType.RightKnee, nuitrack.JointType.RightAnkle),
            (nuitrack.JointType.LeftShoulder, nuitrack.JointType.RightShoulder),
            (nuitrack.JointType.LeftHip, nuitrack.JointType.RightHip),
        };

        readonly List<Image> pool = new List<Image>();
        int used;
        readonly Dictionary<nuitrack.JointType, Vector3> jointRoom = new Dictionary<nuitrack.JointType, Vector3>();

        void LateUpdate()
        {
#if ENABLE_INPUT_SYSTEM
            if (hotkeyEnabled && Keyboard.current != null && Keyboard.current.f2Key.wasPressedThisFrame)
                show = !show;
#endif
            bool active = show && container != null && screen != null
                          && calibrator != null && calibrator.IsCalibrated
                          && NuitrackManager.sensorsData != null && NuitrackManager.sensorsData.Count > 0;
            if (!active)
            {
                if (used > 0)
                    HideUnused(0);
                used = 0;
                return;
            }

            used = 0;
            Matrix4x4 roomFromSensor = calibrator.RoomFromSensor;
            Vector2 size = container.rect.size;
            BoxCursorConfig boxCfg = pointerManager != null ? pointerManager.boxCursor : null;

            foreach (UserData user in NuitrackManager.sensorsData[0].Users)
            {
                if (user == null || user.Skeleton == null)
                    continue;

                jointRoom.Clear();
                foreach (nuitrack.JointType jt in Joints)
                {
                    UserData.SkeletonData.Joint joint = user.Skeleton.GetJoint(jt);
                    if (joint == null || joint.Confidence < confidenceThreshold)
                        continue;
                    jointRoom[jt] = roomFromSensor.MultiplyPoint3x4(joint.Position);
                }

                Color color = UserColor(user.ID);

                foreach (var bone in Bones)
                    if (jointRoom.TryGetValue(bone.Item1, out Vector3 a) && jointRoom.TryGetValue(bone.Item2, out Vector3 b))
                        DrawLine(screen.RoomToScreenUV(a), screen.RoomToScreenUV(b), color, boneThicknessDesign, size);

                if (boxCfg != null)
                    DrawBox(boxCfg, roomFromSensor, size);

                foreach (var kv in jointRoom)
                    DrawDot(screen.RoomToScreenUV(kv.Value), color, size);
            }

            HideUnused(used);
        }

        void DrawBox(BoxCursorConfig cfg, Matrix4x4 roomFromSensor, Vector2 size)
        {
            // (Re)compute the box from the same joints the solver uses, from raw skeleton data.
            if (!jointRoom.TryGetValue(nuitrack.JointType.Torso, out Vector3 torso))
                torso = Vector3.zero;
            bool hasTorso = jointRoom.ContainsKey(nuitrack.JointType.Torso);
            bool hasLs = jointRoom.TryGetValue(nuitrack.JointType.LeftShoulder, out Vector3 ls);
            bool hasRs = jointRoom.TryGetValue(nuitrack.JointType.RightShoulder, out Vector3 rs);

            if (!cfg.TryComputeBox(hasTorso, torso, hasLs, ls, hasRs, rs, screen,
                    out Vector3 center, out float width, out float height))
                return;

            float hw = width * 0.5f, hh = height * 0.5f;
            Vector3 tl = new Vector3(center.x - hw, center.y + hh, center.z);
            Vector3 tr = new Vector3(center.x + hw, center.y + hh, center.z);
            Vector3 br = new Vector3(center.x + hw, center.y - hh, center.z);
            Vector3 bl = new Vector3(center.x - hw, center.y - hh, center.z);

            Vector2 utl = screen.RoomToScreenUV(tl), utr = screen.RoomToScreenUV(tr);
            Vector2 ubr = screen.RoomToScreenUV(br), ubl = screen.RoomToScreenUV(bl);
            DrawLine(utl, utr, boxColor, boxThicknessDesign, size);
            DrawLine(utr, ubr, boxColor, boxThicknessDesign, size);
            DrawLine(ubr, ubl, boxColor, boxThicknessDesign, size);
            DrawLine(ubl, utl, boxColor, boxThicknessDesign, size);
        }

        Color UserColor(int id)
        {
            Color[] pal = pointerManager != null ? pointerManager.userPalette : null;
            if (pal != null && pal.Length > 0)
            {
                int i = ((id - 1) % pal.Length + pal.Length) % pal.Length;
                Color c = pal[i];
                c.a = 0.95f;
                return c;
            }
            return fallbackUserColor;
        }

        static Vector2 ToLocal(Vector2 uv, Vector2 size) =>
            new Vector2((uv.x - 0.5f) * size.x, (uv.y - 0.5f) * size.y);

        void DrawDot(Vector2 uv, Color color, Vector2 size)
        {
            Image img = Next(color);
            RectTransform rt = img.rectTransform;
            rt.localRotation = Quaternion.identity;
            rt.sizeDelta = new Vector2(jointSizeDesign, jointSizeDesign);
            rt.anchoredPosition = ToLocal(uv, size);
        }

        void DrawLine(Vector2 uvA, Vector2 uvB, Color color, float thickness, Vector2 size)
        {
            Vector2 a = ToLocal(uvA, size);
            Vector2 b = ToLocal(uvB, size);
            Vector2 d = b - a;
            float len = d.magnitude;
            Image img = Next(color);
            RectTransform rt = img.rectTransform;
            rt.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
            rt.sizeDelta = new Vector2(len, thickness);
            rt.anchoredPosition = (a + b) * 0.5f;
        }

        Image Next(Color color)
        {
            Image img;
            if (used < pool.Count)
                img = pool[used];
            else
            {
                img = CreateImage();
                pool.Add(img);
            }
            used++;
            if (!img.gameObject.activeSelf)
                img.gameObject.SetActive(true);
            img.color = color;
            return img;
        }

        Image CreateImage()
        {
            var go = new GameObject("dbg", typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(container, false);
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            var img = go.GetComponent<Image>();
            img.raycastTarget = false;
            return img;
        }

        void HideUnused(int from)
        {
            for (int i = from; i < pool.Count; i++)
                if (pool[i].gameObject.activeSelf)
                    pool[i].gameObject.SetActive(false);
        }
    }
}
