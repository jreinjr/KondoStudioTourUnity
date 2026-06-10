using UnityEditor;
using UnityEngine;
using Kondo.Slideshow;

namespace Kondo.EditorTools
{
    /// <summary>
    /// Scene-view widget for the hotspot's hover point: a draggable handle plus the
    /// hover-radius disc, writing back to the normalized <see cref="SlideHotspot.point"/>.
    /// </summary>
    [CustomEditor(typeof(SlideHotspot))]
    [CanEditMultipleObjects]
    public class SlideHotspotEditor : Editor
    {
        void OnSceneGUI()
        {
            var hotspot = (SlideHotspot)target;
            RectTransform rect = hotspot.Rect;
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners); // 0 BL, 1 TL, 2 TR, 3 BR

            Vector3 world = hotspot.PointWorld;
            float worldRadius = hotspot.Radius * hotspot.transform.lossyScale.x;

            Handles.color = Color.cyan;
            Handles.DrawWireDisc(world, Vector3.forward, worldRadius);

            EditorGUI.BeginChangeCheck();
            float handleSize = HandleUtility.GetHandleSize(world) * 0.08f;
            Vector3 moved = Handles.FreeMoveHandle(world, handleSize, Vector3.zero, Handles.SphereHandleCap);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(hotspot, "Move Hotspot Point");
                hotspot.point = new Vector2(
                    Mathf.Clamp01(Mathf.InverseLerp(corners[0].x, corners[3].x, moved.x)),
                    Mathf.Clamp01(Mathf.InverseLerp(corners[0].y, corners[1].y, moved.y)));
                if (hotspot.indicator != null)
                    Undo.RecordObject(hotspot.indicator.transform, "Move Hotspot Point");
                hotspot.PositionIndicator();
                EditorUtility.SetDirty(hotspot);
                PrefabUtility.RecordPrefabInstancePropertyModifications(hotspot);
            }
        }
    }
}
