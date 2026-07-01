using TMPro;
using UnityEngine;

namespace Kondo.Slideshow
{
    /// <summary>
    /// How navigation is guarded right after a slide becomes interactive, so a cursor parked
    /// over the same screen spot doesn't immediately re-trigger the next slide. None = off;
    /// RequireReentry = a hotspot can't dwell until the cursor has left its hover zone once;
    /// TimedDebounce = dwelling is suppressed for a fixed window after the slide loads.
    /// </summary>
    public enum NavigationGuardMode
    {
        None,
        RequireReentry,
        TimedDebounce,
    }

    /// <summary>
    /// Globally shared look and timing for the slideshow. One asset restyles the
    /// whole show; prefab components apply it in OnValidate (editor preview) and
    /// Awake (runtime source of truth).
    /// </summary>
    [CreateAssetMenu(fileName = "SlideshowStyle", menuName = "Kondo/Slideshow Style")]
    public class SlideshowStyle : ScriptableObject
    {
        [Header("Hotspot")]
        public Color hotspotColor = Color.white;
        [Range(0f, 1f)] public float hotspotIdleAlpha = 0.25f;
        [Range(0f, 1f)] public float hotspotHoverMaxAlpha = 0.85f;
        [Tooltip("Hover radius around the hotspot point, in canvas units (2880x2160 reference space).")]
        [Min(1f)] public float hotspotRadius = 225f;
        [Tooltip("Seconds the cursor must dwell on a hotspot to trigger its transition.")]
        [Min(0.05f)] public float dwellSeconds = 0.8f;
        [Tooltip("How much faster dwell drains than it fills once the cursor leaves.")]
        [Min(0f)] public float dwellDecayMultiplier = 2f;
        [Tooltip("Once hovered, a hotspot stays hovered until the pointer leaves its radius times this multiplier, so edge jitter doesn't reset the dwell. 1 = off.")]
        [Min(1f)] public float hotspotExitRadiusMultiplier = 1.3f;
        [Tooltip("How strongly the DISPLAYED cursor is pulled toward a hovered hotspot's center (0 = off). Hit-testing is unaffected.")]
        [Range(0f, 1f)] public float hotspotMagnetStrength = 0.25f;
        [Tooltip("Maps dwell progress (0..1) to the hotspot's idle-to-hover alpha blend.")]
        public AnimationCurve dwellAlphaCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);

        [Header("Navigation Guard")]
        [Tooltip("Prevents a cursor parked over the same spot from immediately re-triggering the next slide. " +
                 "RequireReentry: a hotspot can't dwell until the cursor has left its hover zone once since the slide loaded. " +
                 "TimedDebounce: dwelling is suppressed for the window below after the slide becomes interactive.")]
        public NavigationGuardMode navigationGuard = NavigationGuardMode.TimedDebounce;
        [Tooltip("TimedDebounce only: seconds after a slide becomes interactive during which no hotspot can dwell/fire.")]
        [Min(0f)] public float navigationDebounceSeconds = 1f;

        [Header("Dwell Indicator")]
        public Color indicatorColor = Color.white;
        [Min(0.01f)] public float indicatorFadeSeconds = 0.15f;

        [Header("Slide Timing")]
        [Min(0.01f)] public float slideFadeOutSeconds = 0.5f;
        [Min(0.01f)] public float slideFadeInSeconds = 0.5f;

        [Header("Fade-In Elements (text, focus masks)")]
        [Min(0f)] public float elementDefaultDelay = 0.4f;
        [Min(0.01f)] public float elementDefaultFadeSeconds = 0.6f;

        [Header("Text")]
        [Tooltip("Leave empty to keep the TextMeshPro default font.")]
        public TMP_FontAsset font;
        [Min(1f)] public float fontSize = 42f;
        public Color textColor = Color.white;
        public Color textBackgroundColor = new Color(0f, 0f, 0f, 0.6f);
        [Tooltip("Horizontal / vertical padding between the text and its background rect.")]
        public Vector2 textBackgroundPadding = new Vector2(24f, 16f);

        [Header("Focus Mask")]
        [Tooltip("Tint multiplied over the authored cutout sprite (alpha lives in the PNG).")]
        public Color maskTint = Color.white;

        [Header("Overlay Hotspots")]
        [Tooltip("How long an overlay holds on screen after its elements finish fading in.")]
        [Min(0f)] public float overlayDurationSeconds = 6f;
        [Tooltip("Scale the slide content zooms to while an overlay is showing (1.1 = 10%).")]
        [Min(1f)] public float overlayZoomScale = 1.1f;
        [Min(0.01f)] public float overlayZoomSeconds = 0.6f;
        [Tooltip("Fade for the hotspots disappearing/reappearing around an overlay.")]
        [Min(0.01f)] public float overlayHotspotsFadeSeconds = 0.3f;

        [Header("Auto Advance")]
        [Min(0.1f)] public float autoAdvanceDefaultSeconds = 10f;
        [Tooltip("The radial indicator fills during this many final seconds before an auto advance fires.")]
        [Min(0f)] public float autoIndicatorWindowSeconds = 3f;

        [Header("Transitions")]
        [Tooltip("Default total duration of a fade-through-black transition.")]
        [Min(0.05f)] public float fadeThroughBlackSeconds = 1f;
        [Tooltip("How long to wait for a video (transition or slide background) before giving up.")]
        [Min(0.5f)] public float videoPrepTimeoutSeconds = 3f;

        [Header("Input")]
        [Tooltip("The mouse only counts as a hover source for this long after it last moved (kiosk mice park).")]
        [Min(0f)] public float mouseActiveSeconds = 4f;
        [Min(0f)] public float mouseMoveThresholdPixels = 2f;

        [Header("Hotspot Row (bottom-row selection mode)")]
        [Tooltip("Height of the bottom selection row, in design units (2880x2160 reference).")]
        [Min(1f)] public float rowHeightDesign = 180f;
        [Tooltip("Gap between the row and the bottom screen edge, in design units.")]
        [Min(0f)] public float rowBottomMarginDesign = 40f;
        [Tooltip("Horizontal gap between adjacent label slots, in design units.")]
        [Min(0f)] public float rowLabelSpacingDesign = 16f;
        [Tooltip("Optional font for the row labels (leave empty for the TMP default).")]
        public TMP_FontAsset rowFont;
        [Min(1f)] public float rowFontSize = 54f;
        public Color rowLabelColor = Color.white;
        [Tooltip("Label background at rest.")]
        public Color rowLabelBg = new Color(0f, 0f, 0f, 0.55f);
        [Tooltip("Label background at full dwell (the cursor brightens the label as it selects).")]
        public Color rowLabelHoverBg = new Color(0.2f, 0.45f, 0.7f, 0.9f);
        [Tooltip("Overall opacity of the row while visible.")]
        [Range(0f, 1f)] public float rowIdleAlpha = 0.95f;
        [Tooltip("Seconds the whole row fades in/out when shown or hidden (transitions/overlays).")]
        [Min(0.01f)] public float rowFadeSeconds = 0.25f;

        [Header("Helper Text")]
        [Tooltip("Optional font for the helper text line (leave empty for the TMP default).")]
        public TMP_FontAsset helperFont;
        [Min(1f)] public float helperFontSize = 56f;
        public Color helperColor = Color.white;
        [Tooltip("Gap between the bottom of the helper text and the top of the hotspot row, in design units (2880x2160).")]
        [Min(0f)] public float helperGapDesign = 24f;
        [Min(1f)] public float helperHeightDesign = 90f;
        [Tooltip("Seconds the helper text fades in/out when shown or hidden (transitions/overlays).")]
        [Min(0.01f)] public float helperFadeSeconds = 0.25f;
        [Tooltip("Shown when the active user is not pointing at a hotspot (or too far to interact).")]
        public string helperIdleText = "Stand in front of your desired action.";
        [Tooltip("Shown when the active user points at a hotspot from the Hover zone. {0} = hotspot label.")]
        public string helperHoverFormat = "Step forward to proceed to {0}";
        [Tooltip("Shown when the active user points at a hotspot from the Select zone. {0} = hotspot label.")]
        public string helperSelectFormat = "Proceeding to {0}";
    }
}
