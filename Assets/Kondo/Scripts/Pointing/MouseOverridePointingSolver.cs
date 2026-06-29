using UnityEngine;
using UnityEngine.InputSystem;

namespace Kondo.Pointing
{
    /// <summary>
    /// Development/override pointing: the cursor follows the mouse, full stop. The skeleton is
    /// ignored entirely — no arm aim, no joints, no standing distance — so this drives the show
    /// from the mouse regardless of where (or whether) anyone is tracked. The mouse pixel
    /// position maps straight to screen UV (bottom-left origin, the same convention the cursor
    /// view and hit-testing use).
    ///
    /// To stay depth-independent it reports a body reference pinned to the screen's center axis,
    /// so active-user selection (which scores by centrality / distance) always treats it as the
    /// most central, perpetually-pointing user. When the mouse is unavailable it produces no UV.
    /// </summary>
    public class MouseOverridePointingSolver : IPointingSolver
    {
        public bool HasBody { get; private set; }
        public Vector3 BodyPosition { get; private set; }

        public AimSample Update(in PointingFrame frame)
        {
            // Pin the body to the screen's center axis so centrality is always zero: this mode
            // deliberately ignores how close the user is standing.
            float centerX = frame.Screen != null ? frame.Screen.screenLateralOffsetMeters : 0f;
            BodyPosition = new Vector3(centerX, 0f, 0f);
            HasBody = true;

            Mouse mouse = Mouse.current;
            if (mouse == null || Screen.width <= 0 || Screen.height <= 0)
            {
                HasBody = false;
                return new AimSample { HasRay = false, IsPointing = false, HasScreenUV = false };
            }

            Vector2 pixels = mouse.position.ReadValue();
            Vector2 uv = new Vector2(pixels.x / Screen.width, pixels.y / Screen.height);

            return new AimSample
            {
                HasRay = false,
                IsPointing = true,
                Quality = 1f,
                HasScreenUV = true,
                ScreenUV = new Vector2(Mathf.Clamp01(uv.x), Mathf.Clamp01(uv.y)),
                OnScreen = uv.x >= 0f && uv.x <= 1f && uv.y >= 0f && uv.y <= 1f,
            };
        }
    }
}
