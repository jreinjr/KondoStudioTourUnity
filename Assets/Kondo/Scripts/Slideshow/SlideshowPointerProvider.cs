using System.Collections.Generic;
using Kondo.Pointing;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Kondo.Slideshow
{
    /// <summary>
    /// Unifies hover sources into screen-pixel points (bottom-left origin): the
    /// mouse, counted only while it has moved recently (kiosk mice park), and the
    /// active Nuitrack user's cursor UV. Hotspot hit-testing consumes ScreenPoints.
    /// </summary>
    public class SlideshowPointerProvider : MonoBehaviour
    {
        public SlideshowStyle style;
        public UserPointerManager pointerManager;

        readonly List<Vector2> points = new List<Vector2>();
        Vector2 lastMousePos;
        float lastMouseMoveTime = float.NegativeInfinity;

        public IReadOnlyList<Vector2> ScreenPoints => points;

        void Update()
        {
            points.Clear();

            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                Vector2 pos = mouse.position.ReadValue();
                float threshold = style != null ? style.mouseMoveThresholdPixels : 2f;
                if ((pos - lastMousePos).sqrMagnitude > threshold * threshold)
                {
                    lastMouseMoveTime = Time.time;
                    lastMousePos = pos;
                }
                float activeWindow = style != null ? style.mouseActiveSeconds : 4f;
                if (Time.time - lastMouseMoveTime < activeWindow)
                    points.Add(pos);
            }

            if (pointerManager != null && pointerManager.ActiveUserId >= 0 &&
                pointerManager.States.TryGetValue(pointerManager.ActiveUserId, out var state) &&
                state.HasUv && state.TimeSinceUV <= pointerManager.cursorHoldSeconds)
            {
                // Same mapping PointerCursorView uses: UV 0..1, bottom-left origin, full screen.
                points.Add(new Vector2(
                    Mathf.Clamp01(state.Uv.x) * Screen.width,
                    Mathf.Clamp01(state.Uv.y) * Screen.height));
            }
        }
    }
}
