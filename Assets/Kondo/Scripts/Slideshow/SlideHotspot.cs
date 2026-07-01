using UnityEngine;
using UnityEngine.UI;

namespace Kondo.Slideshow
{
    public enum HotspotAction
    {
        Transition,
        ShowOverlay,
    }

    /// <summary>
    /// A hoverable hotspot that triggers once the cursor has dwelt near its point
    /// long enough. The graphic is typically a full-screen alpha PNG, so hover is
    /// detected against a 2D point + radius rather than the rect. Firing either
    /// transitions to another slide or reveals an in-slide overlay (mask/text).
    /// Opacity and the radial indicator track dwell progress.
    /// </summary>
    public class SlideHotspot : MonoBehaviour
    {
        public SlideshowStyle style;
        public CanvasGroup group;
        public Image image;
        public DwellIndicator indicator;

        [Header("Hotspot Type")]
        [Tooltip("Unchecked = Navigation (moves the tour to another slide); checked = Investigation " +
                 "(a closer look, typically an in-slide overlay). This tag drives the bottom-row label " +
                 "prefab used for this option and is reported to the cursor (e.g. to switch an animated " +
                 "cursor's look) — it does NOT change firing behavior, which stays driven by 'action'.")]
        public bool isInvestigation;

        [Tooltip("Navigation only: point the cursor's beckon arrow right (checked) instead of left (unchecked). " +
                 "This only mirrors the arrow horizontally — it is independent of the label's Fill Direction and " +
                 "does not affect the beckon's bottom→top progress fill.")]
        public bool arrowPointsRight;

        [Header("Row Placeholder")]
        [Tooltip("Make this a blank, non-interactive bottom-row spacer: it reserves a slot in the selection row " +
                 "(keeping the other options aligned) but shows no label and can never be hovered or fired. " +
                 "E.g. lay a 2-option slide out as 3 with the middle blank — just add a placeholder child between " +
                 "the two real hotspots. In the in-image selection mode it is simply invisible and inert.")]
        public bool isPlaceholder;

        [Header("Hover Point")]
        [Tooltip("Hover point in normalized rect coordinates (0..1, bottom-left origin). Drag the scene-view handle.")]
        public Vector2 point = new Vector2(0.5f, 0.5f);
        public bool overrideRadius;
        [Min(1f)] public float radiusOverride = 150f;

        [Header("Action")]
        public HotspotAction action = HotspotAction.Transition;
        public SlideTransitionTarget target = new SlideTransitionTarget();

        [Header("Label")]
        [Tooltip("Name shown in the bottom-row selection mode. Falls back to the target slide's name, then this object's name.")]
        public string label;
        [Tooltip("Direction the bottom-row label's secondary background fill grows as the user approaches select range. " +
                 "Authored here (not on the row-item prefab) because that prefab is instantiated at runtime and can't take scene edits.")]
        public RowFillDirection fillDirection = RowFillDirection.LeftToRight;

        [Header("Overlay (ShowOverlay action)")]
        [Tooltip("Elements (focus mask, text block) revealed by this hotspot. Listed elements are excluded from the slide's enter fades automatically.")]
        public SlideFadeInElement[] overlayElements;
        public bool overrideOverlayDuration;
        [Min(0f)] public float overlayDurationOverride = 6f;
        [Tooltip("Override the zoom level this overlay zooms to (1 = no zoom). Otherwise uses the style's overlayZoomScale.")]
        public bool overrideOverlayZoom;
        [Min(1f)] public float overlayZoomScaleOverride = 1.1f;

        [Header("Dwell Override")]
        public bool overrideDwell;
        [Min(0.05f)] public float dwellSecondsOverride = 0.8f;

        float dwell01;
        float highlight01;
        // RequireReentry guard: stays false until the cursor is seen off this hotspot at least
        // once since the last ResetDwell (i.e. since the slide loaded / an overlay closed), so a
        // cursor parked over the spot from the previous slide can't immediately re-dwell.
        bool sawReleaseSinceReset;

        public float Dwell01 => dwell01;
        /// <summary>
        /// Visual highlight level (0..1) driving the in-image alpha and the bottom-row label
        /// background. Tracks the dwell curve in the Select zone, but snaps to 1 on a Hover-zone
        /// hover (highlight without dwell), so the choice previews before the dwell ring appears.
        /// </summary>
        public float Highlight01 => highlight01;
        /// <summary>True while a pointer holds this hotspot (including the hysteresis annulus).</summary>
        public bool IsHovered { get; private set; }

        /// <summary>True for a blank bottom-row spacer (see <see cref="isPlaceholder"/>).</summary>
        public bool IsPlaceholder => isPlaceholder;
        /// <summary>False for placeholders — they are never hovered, highlighted, or fired.</summary>
        public bool IsInteractable => !isPlaceholder;

        /// <summary>
        /// Whether this hotspot drives its own in-image radial dwell indicator. The bottom-row
        /// selector turns this off and shows the ring on the label instead (the in-image
        /// highlight graphic still brightens). Reset to true on each fresh slide instance.
        /// </summary>
        public bool DriveIndicator { get; set; } = true;

        /// <summary>Text used by the bottom-row selector: explicit label, else target slide name, else object name.</summary>
        public string DisplayLabel =>
            !string.IsNullOrEmpty(label) ? label
            : target != null && target.targetSlide != null ? target.targetSlide.name
            : name;

        /// <summary>
        /// <see cref="DisplayLabel"/> normalized for the helper text: lower-cased and with a
        /// leading "to " stripped, so "To Drying Room" reads "drying room" (avoiding
        /// "Proceeding to to drying room").
        /// </summary>
        public string ProceedLabel
        {
            get
            {
                string s = (DisplayLabel ?? string.Empty).Trim().ToLowerInvariant();
                if (s.StartsWith("to "))
                    s = s.Substring(3).TrimStart();
                return s;
            }
        }

        public RectTransform Rect => (RectTransform)transform;
        public SlideTransitionTarget Target => target;
        public float Radius => overrideRadius || style == null ? radiusOverride : style.hotspotRadius;
        public float OverlayDuration => overrideOverlayDuration || style == null ? overlayDurationOverride : style.overlayDurationSeconds;
        public float OverlayZoomScale => overrideOverlayZoom || style == null ? overlayZoomScaleOverride : style.overlayZoomScale;

        float DwellSeconds => overrideDwell || style == null ? dwellSecondsOverride : style.dwellSeconds;

        /// <summary>Resting CanvasGroup alpha: the style's idle alpha, or 0 for an invisible placeholder.</summary>
        float IdleAlpha => isPlaceholder ? 0f : (style != null ? style.hotspotIdleAlpha : 0.25f);

        /// <summary>The hover point in world space (overlay canvas world == screen pixels at scale 1).</summary>
        public Vector3 PointWorld
        {
            get
            {
                var corners = new Vector3[4];
                Rect.GetWorldCorners(corners); // 0 BL, 1 TL, 2 TR, 3 BR
                return corners[0] + (corners[3] - corners[0]) * point.x + (corners[1] - corners[0]) * point.y;
            }
        }

        public Vector2 ScreenPoint => RectTransformUtility.WorldToScreenPoint(null, PointWorld);

        /// <summary>Hover radius in screen pixels (canvas-unit radius times the canvas scale factor).</summary>
        public float ScreenRadius => Radius * transform.lossyScale.x;

        void Awake()
        {
            ApplyStyle();
            PositionIndicator();
        }

        /// <summary>
        /// Advance or drain the dwell and update the highlight. Dwell only accumulates (and the
        /// ring only shows) when <paramref name="dwellEnabled"/> — the Select zone; a Hover-zone
        /// hover (hovered but not dwellEnabled) highlights without dwelling. When
        /// <paramref name="requireRelease"/> (the RequireReentry guard), dwell is also held off
        /// until the cursor has been seen off this hotspot at least once since the last reset.
        /// Returns true exactly once, on the frame dwell completes.
        /// </summary>
        public bool UpdateHover(bool hovered, bool dwellEnabled, bool requireRelease, float dt)
        {
            if (isPlaceholder)
            {
                // Blank row spacer: inert and invisible no matter what hover bookkeeping arrives.
                IsHovered = false;
                dwell01 = 0f;
                highlight01 = 0f;
                if (group != null)
                    group.alpha = 0f;
                return false;
            }

            if (!hovered)
                sawReleaseSinceReset = true; // an off-hotspot frame satisfies the re-entry guard

            IsHovered = hovered;
            bool canDwell = hovered && dwellEnabled && (!requireRelease || sawReleaseSinceReset);
            float before = dwell01;
            float rate = dt / DwellSeconds;
            float decay = style != null ? style.dwellDecayMultiplier : 2f;
            dwell01 = canDwell
                ? Mathf.Min(1f, dwell01 + rate)
                : Mathf.Max(0f, dwell01 - rate * decay);

            // Hover-zone hover = full highlight without dwell; otherwise track the dwell curve.
            float blend = style != null ? style.dwellAlphaCurve.Evaluate(dwell01) : dwell01;
            highlight01 = (hovered && !dwellEnabled) ? 1f : blend;

            float idle = style != null ? style.hotspotIdleAlpha : 0.25f;
            float max = style != null ? style.hotspotHoverMaxAlpha : 0.85f;
            // Navigation hotspots don't brighten their in-image graphic on hover — the only hover
            // feedback is the cursor's beckon fill (driven by proximity). They hold their idle alpha.
            // Investigation hotspots still fade up with the highlight.
            float graphicHighlight = isInvestigation ? highlight01 : 0f;
            if (group != null)
                group.alpha = Mathf.Lerp(idle, max, graphicHighlight);
            if (indicator != null && DriveIndicator)
                indicator.SetProgress(dwell01);

            return canDwell && dwell01 >= 1f && before < 1f;
        }

        public void ResetDwell(bool snapAlpha = true)
        {
            dwell01 = 0f;
            highlight01 = 0f;
            IsHovered = false;
            sawReleaseSinceReset = false; // re-arm the re-entry guard for the fresh slide/overlay
            if (snapAlpha && group != null)
                group.alpha = IdleAlpha;
            if (indicator != null)
                indicator.HideImmediate();
        }

        public void ApplyStyle()
        {
            if (isPlaceholder)
            {
                // Blank spacer: no graphic, no presence.
                if (image != null)
                    image.enabled = false;
                if (group != null)
                    group.alpha = 0f;
                return;
            }
            if (style == null)
                return;
            if (image != null)
            {
                image.enabled = true; // in case this was toggled back from a placeholder
                Color c = style.hotspotColor;
                c.a = 1f; // visibility is driven by the CanvasGroup, not the image alpha
                image.color = c;
                image.raycastTarget = false;
            }
            if (group != null)
                group.alpha = style.hotspotIdleAlpha;
        }

        /// <summary>Keep the radial indicator sitting on the hover point.</summary>
        public void PositionIndicator()
        {
            if (indicator != null)
                indicator.transform.position = PointWorld;
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            point.x = Mathf.Clamp01(point.x);
            point.y = Mathf.Clamp01(point.y);
            if (group == null)
                group = GetComponent<CanvasGroup>();
            if (image == null)
                image = GetComponent<Image>();
            if (action == HotspotAction.Transition && overlayElements != null && overlayElements.Length > 0)
                Debug.LogWarning($"[SlideHotspot] {name}: overlay elements are assigned but the action is Transition — they will never show. Did you mean ShowOverlay?", this);
            ApplyStyle();
        }

        void OnDrawGizmosSelected()
        {
            if (isPlaceholder)
            {
                UnityEditor.Handles.color = Color.gray;
                UnityEditor.Handles.Label(PointWorld, "row placeholder (blank slot)");
                return;
            }

            Vector3 world = PointWorld;
            float worldRadius = Radius * transform.lossyScale.x;
            UnityEditor.Handles.color = Color.cyan;
            UnityEditor.Handles.DrawWireDisc(world, Vector3.forward, worldRadius);
            string kind = isInvestigation ? "investigation" : "navigation";
            string caption = action == HotspotAction.ShowOverlay
                ? $"[{kind}] overlay: {DisplayLabel}"
                : target != null && target.targetSlide != null
                    ? $"[{kind}] {DisplayLabel} → {target.targetSlide.name}"
                    : $"[{kind}] {DisplayLabel} → (no target)";
            UnityEditor.Handles.Label(world + Vector3.up * worldRadius, caption);
        }
#endif
    }
}
