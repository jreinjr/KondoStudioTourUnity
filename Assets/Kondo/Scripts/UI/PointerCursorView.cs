using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

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
        float styleBlend;
        /// <summary>0 at the hover threshold (far), 1 at the select threshold (close); drives the inner dot's growth.</summary>
        float proximity;
        Color userTint = Color.white;
        float trailTimer;
        readonly List<TrailGhost> ghosts = new List<TrailGhost>();

        public void Init(RectTransform canvasRect)
        {
            this.canvasRect = canvasRect;
            rect = (RectTransform)transform;
            styleBlend = 0f;
            ApplyStyle();
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

        void Update()
        {
            float target = isActive ? 1f : 0f;
            styleBlend = Mathf.MoveTowards(styleBlend, target, Time.deltaTime / Mathf.Max(styleLerpSeconds, 1e-3f));
            ApplyStyle();
            UpdateTrail();
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

            float size = Mathf.Lerp(inactiveSize, activeSize, styleBlend);
            size *= Mathf.Lerp(sizeScaleAtHover, sizeScaleAtSelect, proximity);
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
