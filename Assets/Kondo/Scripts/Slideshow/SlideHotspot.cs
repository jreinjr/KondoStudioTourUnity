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

        [Header("Overlay (ShowOverlay action)")]
        [Tooltip("Elements (focus mask, text block) revealed by this hotspot. Listed elements are excluded from the slide's enter fades automatically.")]
        public SlideFadeInElement[] overlayElements;
        public bool overrideOverlayDuration;
        [Min(0f)] public float overlayDurationOverride = 6f;

        [Header("Dwell Override")]
        public bool overrideDwell;
        [Min(0.05f)] public float dwellSecondsOverride = 0.8f;

        float dwell01;
        float highlight01;

        public float Dwell01 => dwell01;
        /// <summary>
        /// Visual highlight level (0..1) driving the in-image alpha and the bottom-row label
        /// background. Tracks the dwell curve in the Select zone, but snaps to 1 on a Hover-zone
        /// hover (highlight without dwell), so the choice previews before the dwell ring appears.
        /// </summary>
        public float Highlight01 => highlight01;
        /// <summary>True while a pointer holds this hotspot (including the hysteresis annulus).</summary>
        public bool IsHovered { get; private set; }

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

        float DwellSeconds => overrideDwell || style == null ? dwellSecondsOverride : style.dwellSeconds;

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
        /// hover (hovered but not dwellEnabled) highlights without dwelling. Returns true exactly
        /// once, on the frame dwell completes.
        /// </summary>
        public bool UpdateHover(bool hovered, bool dwellEnabled, float dt)
        {
            IsHovered = hovered;
            bool canDwell = hovered && dwellEnabled;
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
            if (group != null)
                group.alpha = Mathf.Lerp(idle, max, highlight01);
            if (indicator != null && DriveIndicator)
                indicator.SetProgress(dwell01);

            return canDwell && dwell01 >= 1f && before < 1f;
        }

        public void ResetDwell(bool snapAlpha = true)
        {
            dwell01 = 0f;
            highlight01 = 0f;
            IsHovered = false;
            if (snapAlpha && group != null)
                group.alpha = style != null ? style.hotspotIdleAlpha : 0.25f;
            if (indicator != null)
                indicator.HideImmediate();
        }

        public void ApplyStyle()
        {
            if (style == null)
                return;
            if (image != null)
            {
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
            Vector3 world = PointWorld;
            float worldRadius = Radius * transform.lossyScale.x;
            UnityEditor.Handles.color = Color.cyan;
            UnityEditor.Handles.DrawWireDisc(world, Vector3.forward, worldRadius);
            string caption = action == HotspotAction.ShowOverlay
                ? $"overlay: {DisplayLabel}"
                : target != null && target.targetSlide != null
                    ? $"{DisplayLabel} → {target.targetSlide.name}"
                    : $"{DisplayLabel} → (no target)";
            UnityEditor.Handles.Label(world + Vector3.up * worldRadius, caption);
        }
#endif
    }
}
