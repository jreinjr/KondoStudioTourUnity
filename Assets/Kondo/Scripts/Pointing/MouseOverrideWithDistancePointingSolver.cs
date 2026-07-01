using UnityEngine;
using UnityEngine.InputSystem;

namespace Kondo.Pointing
{
    /// <summary>
    /// Development/override pointing that also simulates the user's standing distance from the
    /// mouse, so the depth-zone (Hover/Select) interaction can be exercised without the sensor.
    /// Like <see cref="MouseOverridePointingSolver"/> the skeleton is ignored: the horizontal mouse
    /// position drives the cursor's X, but the vertical mouse position is repurposed to drive a
    /// virtual room-space depth (the body's Z) instead of the cursor's Y.
    ///
    /// The vertical remap is unclipped and matches the horizontal-pointing modes this stands in for:
    /// 10% of the screen height (from the bottom) is the Hover-zone boundary (<see cref="PointingFrame.HoverZ"/>)
    /// and 50% is the Select-zone boundary (<see cref="PointingFrame.SelectZ"/>). So dragging the
    /// mouse to the very bottom pushes the virtual user beyond the Hover zone (no interaction), and
    /// anywhere in the top half places them within the Select zone (dwell fires). The cursor's own Y
    /// rides the hover→select line from that same depth, exactly as the real horizontal modes do.
    /// </summary>
    public class MouseOverrideWithDistancePointingSolver : IPointingSolver
    {
        // Screen-height fractions (from the bottom) that map to the Hover and Select depth boundaries.
        const float HoverAtV = 0.10f;
        const float SelectAtV = 0.50f;

        readonly HorizontalPointingConfig config;

        public MouseOverrideWithDistancePointingSolver(HorizontalPointingConfig config)
        {
            this.config = config;
        }

        public bool HasBody { get; private set; }
        public Vector3 BodyPosition { get; private set; }

        public AimSample Update(in PointingFrame frame)
        {
            float centerX = frame.Screen != null ? frame.Screen.screenLateralOffsetMeters : 0f;

            Mouse mouse = Mouse.current;
            if (mouse == null || Screen.width <= 0 || Screen.height <= 0)
            {
                HasBody = false;
                return new AimSample { HasRay = false, IsPointing = false, HasScreenUV = false };
            }

            Vector2 pixels = mouse.position.ReadValue();
            float uvX = Mathf.Clamp01(pixels.x / Screen.width);
            float uvY = pixels.y / Screen.height;

            // Remap the vertical mouse position to a virtual standing distance (room-space Z),
            // unclipped: rise = 0 at the Hover line (HoverZ, far), 1 at the Select line (SelectZ,
            // near). Below the Hover line rise < 0 (beyond hover); above the Select line rise > 1
            // (deeper into select). Wall is at negative Z, so closer means smaller Z.
            float rise = (uvY - HoverAtV) / (SelectAtV - HoverAtV);
            float virtualZ = Mathf.LerpUnclamped(frame.HoverZ, frame.SelectZ, rise);

            // Body pinned to the screen's center axis (centrality always zero) but at the virtual
            // depth, so the zone gating and active-user depth logic see a real standing distance.
            BodyPosition = new Vector3(centerX, 0f, virtualZ);
            HasBody = true;

            // Cursor X follows the mouse; cursor Y rides the hover→select line from the virtual
            // depth, mirroring the horizontal-pointing modes (the vertical position encodes depth,
            // not aim), so hotspot hover matches on horizontal alignment alone.
            float v = Mathf.Lerp(config.hoverLineV01, config.selectLineV01, Mathf.Clamp01(rise));

            return new AimSample
            {
                HasRay = false,
                IsPointing = true,
                Quality = 1f,
                HasScreenUV = true,
                ScreenUV = new Vector2(uvX, Mathf.Clamp01(v)),
                OnScreen = uvX >= 0f && uvX <= 1f,
            };
        }
    }
}
