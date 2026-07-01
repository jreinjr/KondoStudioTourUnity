using UnityEngine;
using UnityEngine.UI;

namespace Kondo.UI
{
    /// <summary>
    /// A vertical stack of 1–3 upward-pointing triangles whose fills pulse in sequence
    /// (bottom-first) to produce a "beckoning" arrow animation. Each triangle is an outline
    /// image with a fill image on top; only the fill's opacity is modulated over time, with a
    /// phase delay up the stack. Purely time-driven — it needs no proximity/kind input. The
    /// outline + fill sprites are shared across the stack (assign them once here).
    ///
    /// The three triangle cells are authored children (bottom → top) wired into <see cref="cells"/>
    /// / <see cref="outlines"/> / <see cref="fills"/> by the builder. Each cell's size and position
    /// are authored on the prefab (set them manually); this component only enables the first
    /// <see cref="triangleCount"/>, applies the shared sprites, and animates the fills.
    /// </summary>
    public class BeckonGraphic : MonoBehaviour
    {
        [Header("Sprites (shared across the stack)")]
        public Sprite outlineSprite;
        public Sprite fillSprite;
        public Color outlineColor = Color.white;
        public Color fillColor = Color.white;

        [Header("Stack (bottom → top)")]
        [Tooltip("How many triangles are shown, from the bottom up. Each triangle's size/position is authored on the prefab.")]
        [Range(1, 3)] public int triangleCount = 3;

        [Header("Animation")]
        [Tooltip("Pulse cycles per second.")]
        [Min(0f)] public float speed = 1.5f;
        [Tooltip("Phase offset per step up the stack, in cycles (the bottom triangle leads). " +
                 "0.25 = a quarter-cycle between neighbours.")]
        public float phaseDelay = 0.25f;
        [Tooltip("Fill opacity at the dim end of the pulse.")]
        [Range(0f, 1f)] public float minAlpha = 0.1f;
        [Tooltip("Fill opacity at the bright end of the pulse.")]
        [Range(0f, 1f)] public float maxAlpha = 1f;

        [Header("References (bottom → top; wired by the builder)")]
        public RectTransform[] cells;
        public Image[] outlines;
        public Image[] fills;

        float phase; // accumulated time, in cycles

        void Awake() => Apply();
        void OnEnable() => phase = 0f;

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
            }
        }

        void Update()
        {
            phase += Time.deltaTime * speed;
            int count = Mathf.Clamp(triangleCount, 1, 3);
            for (int i = 0; i < count && fills != null && i < fills.Length; i++)
                SetFillAlpha(i, EvaluateAlpha(i));
        }

        /// <summary>0..1 fill opacity for triangle <paramref name="i"/> at the current phase (bottom leads).</summary>
        float EvaluateAlpha(int i)
        {
            float t = phase - i * phaseDelay;
            float wave = 0.5f * (1f + Mathf.Sin(t * Mathf.PI * 2f));
            return Mathf.Lerp(minAlpha, maxAlpha, wave);
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
