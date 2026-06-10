using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Kondo.Slideshow
{
    /// <summary>
    /// Diagnostic overlay that works in builds (toggle with F1): controller state,
    /// pointer positions, and per-hotspot point/radius/distance/dwell so hover
    /// problems are visible without the editor.
    /// </summary>
    public class SlideshowDebugHud : MonoBehaviour
    {
        public SlideshowController controller;
        public SlideshowPointerProvider pointers;
        public bool visible;

        readonly StringBuilder text = new StringBuilder(512);

        void Update()
        {
            if (Keyboard.current != null && Keyboard.current.f1Key.wasPressedThisFrame)
                visible = !visible;
        }

        void OnGUI()
        {
            if (!visible || controller == null)
                return;

            text.Length = 0;
            text.AppendLine($"state: {controller.State}");

            var points = pointers != null ? pointers.ScreenPoints : null;
            if (points != null)
                for (int i = 0; i < points.Count; i++)
                    text.AppendLine($"pointer[{i}]: ({points[i].x:F0}, {points[i].y:F0})");

            Slide slide = controller.CurrentSlide;
            if (slide != null)
            {
                foreach (SlideHotspot hotspot in slide.Hotspots)
                {
                    float minDist = float.PositiveInfinity;
                    if (points != null)
                        for (int i = 0; i < points.Count; i++)
                            minDist = Mathf.Min(minDist, Vector2.Distance(points[i], hotspot.ScreenPoint));
                    Vector2 p = hotspot.ScreenPoint;
                    string dist = float.IsPositiveInfinity(minDist) ? "-" : minDist.ToString("F0");
                    text.AppendLine($"{hotspot.name} [{hotspot.action}]: pt=({p.x:F0}, {p.y:F0}) r={hotspot.ScreenRadius:F0} dist={dist} dwell={hotspot.Dwell01:F2}");
                }
                float auto = slide.AutoAdvanceTimeRemaining;
                if (!float.IsPositiveInfinity(auto))
                    text.AppendLine($"autoAdvance in: {auto:F1}s");
            }

            string s = text.ToString();
            var rect = new Rect(14f, 14f, 1200f, 800f);
            GUI.color = Color.black;
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), s);
            GUI.color = Color.green;
            GUI.Label(rect, s);
            GUI.color = Color.white;
        }
    }
}
