using System.Collections.Generic;
using UnityEngine;

namespace Kondo.Slideshow
{
    /// <summary>Where a slide's hotspots are selected from. Selected on <see cref="SlideshowController"/>.</summary>
    public enum HotspotSelectionMode
    {
        [Tooltip("Hover the highlight graphic in the image (point + radius). The original behavior.")]
        InImage,
        [Tooltip("Hover a named label in a row along the bottom of the screen; the highlight graphic stays in the image.")]
        BottomRow,
    }

    /// <summary>
    /// Decides, per slide, where the hit-test zone for each hotspot lives. The controller keeps
    /// owning dwell, firing, hysteresis, and magnetism; the selector only relocates the zone
    /// (and, for the bottom row, owns the on-screen label UI). Zones are reported in screen
    /// pixels (bottom-left origin), matching <see cref="SlideshowPointerProvider.ScreenPoints"/>.
    /// </summary>
    public interface IHotspotSelector
    {
        /// <summary>A new slide became current: (re)build any per-slide UI / zone mapping.</summary>
        void OnSlideChanged(Slide slide);

        /// <summary>Show or hide the selection UI (hidden during transitions/overlays).</summary>
        void SetVisible(bool visible);

        /// <summary>Screen-pixel center of the hotspot's hit zone.</summary>
        Vector2 ZonePoint(SlideHotspot hotspot);

        /// <summary>Screen-pixel radius of the hotspot's hit zone.</summary>
        float ZoneRadius(SlideHotspot hotspot);

        /// <summary>Per-frame update of selection visuals (e.g. label dwell progress). Idle only.</summary>
        void Tick(IReadOnlyList<SlideHotspot> hotspots, float dt);
    }

    /// <summary>
    /// Original in-image selection: the hit zone is the hotspot's own point + radius, so the
    /// highlight graphic and the hit zone are one and the same. No UI of its own.
    /// </summary>
    public class InImageHotspotSelector : IHotspotSelector
    {
        public void OnSlideChanged(Slide slide)
        {
            // Restore in-image dwell rings in case we switched away from the bottom row mid-slide.
            if (slide == null)
                return;
            foreach (SlideHotspot h in slide.Hotspots)
                if (h != null)
                    h.DriveIndicator = true;
        }

        public void SetVisible(bool visible) { }
        public Vector2 ZonePoint(SlideHotspot hotspot) => hotspot.ScreenPoint;
        public float ZoneRadius(SlideHotspot hotspot) => hotspot.ScreenRadius;
        public void Tick(IReadOnlyList<SlideHotspot> hotspots, float dt) { }
    }
}
