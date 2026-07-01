using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Kondo.Pointing;

namespace Kondo.UI
{
    /// <summary>
    /// Visual for one user's pointer on the overlay canvas: positions itself from
    /// normalized screen UV, blends between active/inactive styles, fades via its
    /// CanvasGroup, and optionally leaves a short ghost trail.
    /// </summary>
    public class PointerCursorView : MonoBehaviour
    {
        [Header("References (wired by the scene builder)")]
        public RectTransform ringRect;
        public Image ringImage;
        public RectTransform dotRect;
        public Image dotImage;
        public CanvasGroup canvasGroup;

        [Header("Active Style")]
        public Color activeRingColor = Color.white;
        public Color activeDotColor = new Color(1f, 0.55f, 0.1f);
        [Tooltip("Cursor diameter in canvas pixels when active.")]
        [Min(4f)] public float activeSize = 80f;

        [Header("Inactive Style")]
        public Color inactiveRingColor = new Color(0.7f, 0.7f, 0.7f, 0.45f);
        public Color inactiveDotColor = new Color(0.9f, 0.9f, 0.9f, 0.6f);
        [Min(4f)] public float inactiveSize = 56f;

        [Tooltip("How strongly the per-user palette color tints the inactive cursor (0 = plain grey, 1 = full user color).")]
        [Range(0f, 1f)] public float userTintStrength = 0.6f;

        [Header("Overall Size Growth (driven by depth between the hover and select thresholds)")]
        [Tooltip("Overall cursor-size multiplier when the user is at the hover threshold (far).")]
        [Min(0.01f)] public float sizeScaleAtHover = 1f;

        [Tooltip("Overall cursor-size multiplier when the user is at the select threshold (close).")]
        [Min(0.01f)] public float sizeScaleAtSelect = 1f;

        [Header("Inner Dot Growth (driven by depth between the hover and select thresholds)")]
        [Tooltip("Inner dot size as a fraction of the ring when the user is at the hover threshold (far). 0 = a point.")]
        [Range(0f, 1f)] public float dotScaleAtHover = 0f;

        [Tooltip("Inner dot size as a fraction of the ring when the user is at the select threshold (close). 1 = fills the ring.")]
        [Range(0f, 1f)] public float dotScaleAtSelect = 1f;

        [Tooltip("Seconds to blend between active and inactive styles.")]
        [Min(0.01f)] public float styleLerpSeconds = 0.15f;

        [Header("Animated Mode (procedural)")]
        [Tooltip("Optional fill graphic (animated cursor only). Driven each frame with the hover→select " +
                 "proximity and the hovered hotspot kind. Leave null on the classic ring/dot cursor. The " +
                 "beckon graphic, if present, animates itself and needs no wiring here.")]
        public FillGraphic fillGraphic;

        [Header("Animated Mode — inactive fallback")]
        [Tooltip("Plain dot shown only while this cursor is inactive (a non-active user); the beckon + fill " +
                 "are hidden then. Tinted with the Inactive Style dot color. Animated cursor only — if left " +
                 "unwired on an animated cursor it is created automatically at runtime.")]
        public RectTransform inactiveDotRect;
        public Image inactiveDotImage;
        [Tooltip("Diameter of the inactive dot, in canvas pixels.")]
        [Min(1f)] public float inactiveDotSize = 28f;

        [Header("Trail (optional)")]
        public bool enableTrail = false;
        [Tooltip("Seconds between trail ghost stamps.")]
        [Min(0.01f)] public float trailInterval = 0.03f;
        [Tooltip("Seconds a trail ghost lives before fading out completely.")]
        [Min(0.05f)] public float trailLife = 0.25f;
        [Range(0f, 1f)] public float trailAlpha = 0.35f;
        [Range(2, 64)] public int trailCapacity = 12;

        class TrailGhost
        {
            public RectTransform Rect;
            public Image Image;
            public float Age;
            public bool Alive;
        }

        RectTransform rect;
        RectTransform canvasRect;
        bool isActive;
        /// <summary>True for the procedural (beckon/fill) cursor, which has no ring/dot and instead
        /// scales its whole root with the hover→select multiplier.</summary>
        bool isAnimated;
        float styleBlend;
        /// <summary>0 at the hover threshold (far), 1 at the select threshold (close); drives the inner dot's growth.</summary>
        float proximity;
        /// <summary>0 at the select line (close), 1 at the back-out line (far); drives the beckon's fill while it is flipped down.</summary>
        float backoutProximity;
        /// <summary>Kind of hotspot currently under this cursor; forwarded to the Animator in animated mode.</summary>
        CursorHotspotKind hotspotKind = CursorHotspotKind.None;
        /// <summary>Whether the beckon arrow is mirrored to point right (nav hotspot direction).</summary>
        bool beckonMirror;
        Color userTint = Color.white;
        float trailTimer;
        readonly List<TrailGhost> ghosts = new List<TrailGhost>();
        // The self-animating beckon stack (animated cursor only), auto-found so it's hidden while
        // inactive without needing to be wired — it lives inside a nested prefab that's awkward to reference.
        BeckonGraphic beckon;

        public void Init(RectTransform canvasRect)
        {
            this.canvasRect = canvasRect;
            rect = (RectTransform)transform;
            styleBlend = 0f;
            beckon = GetComponentInChildren<BeckonGraphic>(true);
            isAnimated = beckon != null || fillGraphic != null;
            EnsureInactiveDot();
            ApplyStyle();
            ApplyActiveGraphicSwap();
        }

        /// <summary>
        /// Animated cursors need a plain dot to fall back to while inactive. If the prefab predates it
        /// (nothing wired), build one at runtime so the feature works regardless of prefab vintage. The
        /// classic ring/dot cursor — which has no beckon/fill — is left alone.
        /// </summary>
        void EnsureInactiveDot()
        {
            if (inactiveDotRect != null || (beckon == null && fillGraphic == null))
                return;

            var go = new GameObject("InactiveDot", typeof(RectTransform), typeof(Image));
            var dotRect = (RectTransform)go.transform;
            dotRect.SetParent(transform, false);
            dotRect.anchorMin = dotRect.anchorMax = new Vector2(0.5f, 0.5f);
            dotRect.pivot = new Vector2(0.5f, 0.5f);
            dotRect.anchoredPosition = Vector2.zero;
            dotRect.sizeDelta = new Vector2(inactiveDotSize, inactiveDotSize);
            var img = go.GetComponent<Image>();
            img.sprite = CircleSprite();
            img.raycastTarget = false;
            inactiveDotRect = dotRect;
            inactiveDotImage = img;
        }

        /// <summary>Position the cursor. uv outside 0..1 (edge-margin pinning) is clamped to the canvas.</summary>
        public void SetUV(Vector2 uv)
        {
            if (canvasRect == null)
                return;
            uv.x = Mathf.Clamp01(uv.x);
            uv.y = Mathf.Clamp01(uv.y);
            rect.anchoredPosition = Vector2.Scale(uv, canvasRect.rect.size);
        }

        public void SetActive(bool active) => isActive = active;

        public void SetAlpha(float alpha)
        {
            if (canvasGroup != null)
                canvasGroup.alpha = alpha;
        }

        public void SetUserTint(Color tint) => userTint = tint;

        /// <summary>Depth progress 0..1: 0 at the hover threshold (far), 1 at the select threshold (close). Grows the inner dot.</summary>
        public void SetProximity(float t01) => proximity = Mathf.Clamp01(t01);

        /// <summary>Back-out progress 0..1: 0 at the select line (close), 1 at the back-out line (far). Drives the beckon's fill while it is flipped down.</summary>
        public void SetBackoutProximity(float t01) => backoutProximity = Mathf.Clamp01(t01);

        /// <summary>The kind of hotspot currently under this cursor (None when not hovering one). Drives the Animator in animated mode.</summary>
        public void SetHotspotKind(CursorHotspotKind kind) => hotspotKind = kind;

        /// <summary>Mirror the beckon arrow horizontally (nav hotspot pointing right instead of left). Animated cursor only.</summary>
        public void SetBeckonMirrored(bool mirrored) => beckonMirror = mirrored;

        void Update()
        {
            float target = isActive ? 1f : 0f;
            styleBlend = Mathf.MoveTowards(styleBlend, target, Time.deltaTime / Mathf.Max(styleLerpSeconds, 1e-3f));
            ApplyStyle();
            ApplyActiveGraphicSwap();
            DriveAnimated();
            UpdateTrail();
        }

        /// <summary>
        /// Animated cursor only: show the beckon/fill graphics while this cursor is the active user,
        /// and a plain dot while it is inactive. A no-op on the classic ring/dot cursor (nothing wired).
        /// </summary>
        void ApplyActiveGraphicSwap()
        {
            // Only the animated cursor has an inactive dot; the classic ring/dot cursor leaves it null
            // and is left untouched.
            if (inactiveDotRect == null)
                return;

            // Active user: the beckon + fill are shown. Inactive: hide both and show the plain dot instead.
            SetGoActive(beckon != null ? beckon.gameObject : null, isActive);
            SetGoActive(fillGraphic != null ? fillGraphic.gameObject : null, isActive);
            if (inactiveDotRect.gameObject.activeSelf == isActive)
                inactiveDotRect.gameObject.SetActive(!isActive);
        }

        static void SetGoActive(GameObject go, bool active)
        {
            if (go != null && go.activeSelf != active)
                go.SetActive(active);
        }

        // Simple filled-circle sprite for the runtime-created inactive dot, generated once and shared.
        static Sprite s_circleSprite;
        static Sprite CircleSprite()
        {
            if (s_circleSprite != null)
                return s_circleSprite;

            const int size = 64;
            const float r = size * 0.5f;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "CursorDot" };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x + 0.5f - r) * (x + 0.5f - r) + (y + 0.5f - r) * (y + 0.5f - r));
                    float a = Mathf.Clamp01(r - d); // 1px antialiased edge
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            s_circleSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            s_circleSprite.name = "CursorDot";
            return s_circleSprite;
        }

        void DriveAnimated()
        {
            // The beckon stack drives its own progress fill (bottom→top solid fill) from proximity,
            // and mirrors its arrow left/right per the hovered nav hotspot. While flipped down
            // (Overlay / auto-advance), it instead fills on back-out progress: empty at the select
            // line, full at the back-out line, so the fill tracks the user's step-back to exit.
            if (beckon != null)
            {
                beckon.SetProximity(BeckonGraphic.PointingDown ? backoutProximity : proximity);
                beckon.SetMirrored(beckonMirror);
            }
            // The legacy proximity meter is hidden/unwired on the current cursor, but keep driving it
            // when present so older prefabs still work.
            if (fillGraphic != null)
            {
                fillGraphic.SetProximity(proximity);
                fillGraphic.SetKind(hotspotKind);
            }
        }

        void ApplyStyle()
        {
            Color tintedRing = Color.Lerp(inactiveRingColor,
                new Color(userTint.r, userTint.g, userTint.b, inactiveRingColor.a), userTintStrength);
            Color tintedDot = Color.Lerp(inactiveDotColor,
                new Color(userTint.r, userTint.g, userTint.b, inactiveDotColor.a), userTintStrength);

            if (ringImage != null)
                ringImage.color = Color.Lerp(tintedRing, activeRingColor, styleBlend);
            if (dotImage != null)
                dotImage.color = Color.Lerp(tintedDot, activeDotColor, styleBlend);

            // Inactive-fallback dot (animated cursor): always the plain inactive color — it's only
            // ever visible while inactive, so it doesn't blend toward the active style.
            if (inactiveDotImage != null)
                inactiveDotImage.color = tintedDot;
            if (inactiveDotRect != null)
                inactiveDotRect.sizeDelta = new Vector2(inactiveDotSize, inactiveDotSize);

            float overallScale = Mathf.Lerp(sizeScaleAtHover, sizeScaleAtSelect, proximity);

            // Animated cursor: no ring/dot to resize, so drive the overall size by scaling the whole
            // root with the same hover→select multiplier. Blend it out to 1 while inactive so the plain
            // inactive-fallback dot keeps its authored size. The beckon's point-down flip lives on its
            // own child transform, so it composes with this root scale untouched.
            if (isAnimated && rect != null)
                rect.localScale = Vector3.one * Mathf.Lerp(1f, overallScale, styleBlend);

            float size = Mathf.Lerp(inactiveSize, activeSize, styleBlend);
            size *= overallScale;
            if (ringRect != null)
                ringRect.sizeDelta = new Vector2(size, size);
            if (dotRect != null)
            {
                float dotFraction = Mathf.Lerp(dotScaleAtHover, dotScaleAtSelect, proximity);
                float dotSize = size * dotFraction;
                dotRect.sizeDelta = new Vector2(dotSize, dotSize);
            }
        }

        void UpdateTrail()
        {
            float dt = Time.deltaTime;
            for (int i = 0; i < ghosts.Count; i++)
            {
                TrailGhost g = ghosts[i];
                if (!g.Alive)
                    continue;
                g.Age += dt;
                if (g.Age >= trailLife)
                {
                    g.Alive = false;
                    g.Image.enabled = false;
                    continue;
                }
                float life = 1f - g.Age / trailLife;
                Color c = g.Image.color;
                c.a = trailAlpha * life * (canvasGroup != null ? canvasGroup.alpha : 1f);
                g.Image.color = c;
                g.Rect.localScale = Vector3.one * Mathf.Lerp(0.4f, 1f, life);
            }

            if (!enableTrail || canvasGroup == null || canvasGroup.alpha < 0.05f)
                return;

            trailTimer += dt;
            if (trailTimer >= trailInterval)
            {
                trailTimer = 0f;
                StampGhost();
            }
        }

        void StampGhost()
        {
            TrailGhost ghost = null;
            for (int i = 0; i < ghosts.Count; i++)
            {
                if (!ghosts[i].Alive)
                {
                    ghost = ghosts[i];
                    break;
                }
            }

            if (ghost == null)
            {
                if (ghosts.Count >= trailCapacity)
                    return;
                var go = new GameObject("TrailGhost", typeof(RectTransform), typeof(Image));
                var ghostRect = (RectTransform)go.transform;
                ghostRect.SetParent(rect.parent, false);
                ghostRect.SetAsFirstSibling();
                ghostRect.anchorMin = ghostRect.anchorMax = Vector2.zero;
                var img = go.GetComponent<Image>();
                img.raycastTarget = false;
                ghost = new TrailGhost { Rect = ghostRect, Image = img };
                ghosts.Add(ghost);
            }

            ghost.Alive = true;
            ghost.Age = 0f;
            ghost.Image.enabled = true;
            ghost.Image.sprite = dotImage != null ? dotImage.sprite : null;
            Color baseColor = dotImage != null ? dotImage.color : Color.white;
            baseColor.a = trailAlpha;
            ghost.Image.color = baseColor;
            ghost.Rect.sizeDelta = dotRect != null ? dotRect.sizeDelta : new Vector2(20f, 20f);
            ghost.Rect.anchoredPosition = rect.anchoredPosition;
            ghost.Rect.localScale = Vector3.one;
        }

        void OnDestroy()
        {
            foreach (TrailGhost g in ghosts)
                if (g.Rect != null)
                    Destroy(g.Rect.gameObject);
        }
    }
}
