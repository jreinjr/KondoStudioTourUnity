using UnityEngine;
using UnityEngine.UI;

namespace Kondo.UI
{
    /// <summary>
    /// A vertical stack of 1–3 upward-pointing triangles that "beckon" the user forward. Each
    /// triangle is drawn as three stacked layers (back → front): a <b>progress fill</b>, an outline
    /// stroke, and a <b>beckon fill</b>. So there are two independent fills:
    ///  • the <b>beckon fill</b> pulses its opacity over time between <see cref="minAlpha"/> and
    ///    <see cref="fillMaxOpacity"/>, bottom-first with a phase delay up the stack (the classic
    ///    "beckon" animation, purely time-driven);
    ///  • the <b>progress fill</b> sits behind the arrows and fills <i>solid</i> from the bottom
    ///    arrow upward through the top arrow, driven by the hover→select proximity fed in via
    ///    <see cref="SetProximity"/>. The fill is spread across the whole stack, so with N arrows
    ///    each arrow covers a 1/N band of the progression regardless of N.
    ///
    /// The three triangle cells are authored children (bottom → top) wired into <see cref="cells"/>
    /// / <see cref="outlines"/> / <see cref="fills"/> / <see cref="progressFills"/> by the builder.
    /// Each cell's size and position are authored on the prefab (set them manually); this component
    /// only enables the first <see cref="triangleCount"/>, applies the shared sprites, animates the
    /// beckon fills, and drives the progress fills.
    /// </summary>
    public class BeckonGraphic : MonoBehaviour
    {
        [Header("Sprites (shared across the stack)")]
        public Sprite outlineSprite;
        public Sprite fillSprite;
        [Tooltip("Sprite for the progress-fill layer behind each arrow (usually the same shape as the beckon fill).")]
        public Sprite progressFillSprite;
        public Color outlineColor = Color.white;
        public Color fillColor = Color.white;
        [Tooltip("Tint of the solid bottom→top progress fill.")]
        public Color progressFillColor = Color.white;

        [Header("Stack (bottom → top)")]
        [Tooltip("How many triangles are shown, from the bottom up. Each triangle's size/position is authored on the prefab.")]
        [Range(1, 3)] public int triangleCount = 3;

        [Header("Beckon fill (time-pulsing opacity)")]
        [Tooltip("Pulse cycles per second.")]
        [Min(0f)] public float speed = 1.5f;
        [Tooltip("Phase offset per step up the stack, in cycles (the bottom triangle leads). " +
                 "0.25 = a quarter-cycle between neighbours.")]
        public float phaseDelay = 0.25f;
        [Tooltip("Beckon-fill opacity at the dim end of the pulse (set to 0 to pulse from fully transparent).")]
        [Range(0f, 1f)] public float minAlpha = 0.1f;
        [Tooltip("Max opacity of the time-pulsing beckon fill (the bright end of the pulse).")]
        [Range(0f, 1f)] public float fillMaxOpacity = 1f;

        [Header("References (bottom → top; wired by the builder)")]
        public RectTransform[] cells;
        public Image[] outlines;
        public Image[] fills;
        [Tooltip("Solid progress-fill image behind each arrow; fills bottom→top across the whole stack with proximity.")]
        public Image[] progressFills;

        float phase; // accumulated time, in cycles
        float proximity; // 0 at the hover threshold (far), 1 at the select threshold (close)

        // Global flip flag: gameplay code (the overlay routine) flips every live cursor's beckon at
        // once via SetPointingDown, without holding references to the dynamically-spawned cursors.
        // Each instance mirrors it onto its own vertical scale.
        static bool s_pointDown;
        bool appliedPointDown;

        // Per-cursor horizontal mirror of the arrow (nav hotspot left↔right). Only the arrow layers
        // (outline + beckon fill) flip on X; the progress fill keeps its own bottom→top direction,
        // so the arrow's pointing direction is decoupled from the fill direction.
        bool mirrored;
        bool appliedMirror;

        /// <summary>
        /// Flip every beckon graphic upside down so it points toward the bottom of the screen
        /// (used during an Overlay action), or restore it to pointing up. Global — affects all cursors.
        /// </summary>
        public static void SetPointingDown(bool down) => s_pointDown = down;

        /// <summary>True while the beckon is flipped to point down (an Overlay / auto-advance slide is holding).</summary>
        public static bool PointingDown => s_pointDown;

        /// <summary>
        /// Mirror this cursor's arrow horizontally (left ↔ right) — e.g. to point a Navigation
        /// hotspot's beckon toward the side it leads. Only the arrow flips; the progress fill still
        /// fills bottom → top regardless.
        /// </summary>
        public void SetMirrored(bool m) => mirrored = m;

        void Awake() => Apply();

        void OnEnable()
        {
            phase = 0f;
            appliedPointDown = !s_pointDown; // force ApplyFlip to reapply on the next Update
            appliedMirror = !mirrored;       // force ApplyMirror to reapply on the next Update
            ApplyFlip();
            ApplyMirror();
        }

        /// <summary>Enable the first N triangles and apply the shared sprites/colors (size/position are authored on the prefab).</summary>
        void Apply()
        {
            if (cells == null)
                return;
            int count = Mathf.Clamp(triangleCount, 1, 3);

            for (int i = 0; i < cells.Length; i++)
            {
                bool on = i < count;
                if (cells[i] != null)
                    cells[i].gameObject.SetActive(on);
                if (outlines != null && i < outlines.Length && outlines[i] != null)
                {
                    outlines[i].sprite = outlineSprite;
                    outlines[i].color = outlineColor;
                    outlines[i].raycastTarget = false;
                }
                if (fills != null && i < fills.Length && fills[i] != null)
                {
                    fills[i].sprite = fillSprite;
                    fills[i].raycastTarget = false;
                    SetFillAlpha(i, EvaluateAlpha(i)); // static pose (mid-pulse) for the editor
                }
                if (progressFills != null && i < progressFills.Length && progressFills[i] != null)
                {
                    Image pf = progressFills[i];
                    pf.sprite = progressFillSprite;
                    pf.color = progressFillColor;
                    pf.raycastTarget = false;
                    pf.type = Image.Type.Filled;
                    pf.fillMethod = Image.FillMethod.Vertical;
                    pf.fillOrigin = (int)Image.OriginVertical.Bottom;
                }
            }
            ApplyProgress(count);
        }

        void Update()
        {
            ApplyFlip();
            ApplyMirror();
            phase += Time.deltaTime * speed;
            int count = Mathf.Clamp(triangleCount, 1, 3);
            for (int i = 0; i < count && fills != null && i < fills.Length; i++)
                SetFillAlpha(i, EvaluateAlpha(i));
        }

        /// <summary>
        /// Feed the hover→select proximity (0 at the hover threshold, 1 at select). Fills the arrow
        /// stack solid from the bottom arrow up: the progression is spread evenly across the active
        /// arrows, so each arrow fills over its own 1/count band regardless of how many are shown.
        /// </summary>
        public void SetProximity(float t01)
        {
            proximity = Mathf.Clamp01(t01);
            ApplyProgress(Mathf.Clamp(triangleCount, 1, 3));
        }

        /// <summary>Distribute <see cref="proximity"/> across the active arrows' progress fills, bottom → top.</summary>
        void ApplyProgress(int count)
        {
            if (progressFills == null)
                return;
            for (int i = 0; i < count && i < progressFills.Length; i++)
                if (progressFills[i] != null)
                    progressFills[i].fillAmount = Mathf.Clamp01(proximity * count - i);
        }

        /// <summary>Mirror the global point-down flag onto this graphic's vertical scale (only when it changes).</summary>
        void ApplyFlip()
        {
            if (appliedPointDown == s_pointDown)
                return;
            appliedPointDown = s_pointDown;
            Vector3 scale = transform.localScale;
            scale.y = Mathf.Abs(scale.y) * (s_pointDown ? -1f : 1f);
            transform.localScale = scale;
        }

        /// <summary>
        /// Flip only the arrow layers (outline + beckon fill) horizontally when <see cref="mirrored"/>
        /// changes. The progress fill is deliberately left untouched so its bottom→top fill direction
        /// is independent of which way the arrow points. A horizontal (X) flip doesn't affect the
        /// vertical fill anyway; flipping the layers rather than the root keeps that explicit.
        /// </summary>
        void ApplyMirror()
        {
            if (appliedMirror == mirrored)
                return;
            appliedMirror = mirrored;
            float sx = mirrored ? -1f : 1f;
            ApplyLayerFlipX(outlines, sx);
            ApplyLayerFlipX(fills, sx);
        }

        static void ApplyLayerFlipX(Image[] layers, float sx)
        {
            if (layers == null)
                return;
            for (int i = 0; i < layers.Length; i++)
            {
                if (layers[i] == null)
                    continue;
                Vector3 s = layers[i].rectTransform.localScale;
                s.x = Mathf.Abs(s.x) * sx;
                layers[i].rectTransform.localScale = s;
            }
        }

        /// <summary>0..1 fill opacity for triangle <paramref name="i"/> at the current phase (bottom leads).</summary>
        float EvaluateAlpha(int i)
        {
            float t = phase - i * phaseDelay;
            float wave = 0.5f * (1f + Mathf.Sin(t * Mathf.PI * 2f));
            return Mathf.Lerp(minAlpha, fillMaxOpacity, wave);
        }

        void SetFillAlpha(int i, float alpha)
        {
            if (fills == null || i >= fills.Length || fills[i] == null)
                return;
            Color c = fillColor;
            c.a *= alpha;
            fills[i].color = c;
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            if (isActiveAndEnabled)
                Apply();
        }
#endif
    }
}
