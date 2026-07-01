using UnityEngine;
using UnityEngine.UI;
using Kondo.Pointing;

namespace Kondo.UI
{
    /// <summary>
    /// A vertical progress meter (outline sprite + fill sprite) that fills bottom→top with the
    /// hover→select proximity (0 at the hover distance, 1 at the select distance). The sprites
    /// differ per hotspot kind — Navigation vs Investigation each get their own outline + fill —
    /// and the graphic hides while the cursor is over no hotspot (kind = None).
    /// Driven each frame by <see cref="PointerCursorView"/>.
    /// </summary>
    public class FillGraphic : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Outline/stroke image (swapped per hotspot kind).")]
        public Image outlineImage;
        [Tooltip("Fill image — set to a Filled/Vertical/Bottom-origin image; its fillAmount tracks proximity.")]
        public Image fillImage;
        [Tooltip("Optional: fades the whole graphic out when the cursor is over no hotspot.")]
        public CanvasGroup group;

        [Header("Navigation sprites")]
        public Sprite navOutline;
        public Sprite navFill;

        [Header("Investigation sprites")]
        public Sprite investigationOutline;
        public Sprite investigationFill;

        [Tooltip("Hide the graphic entirely when the cursor is over no hotspot (kind = None).")]
        public bool hideWhenNone = true;

        CursorHotspotKind kind = CursorHotspotKind.None;

        void Awake()
        {
            ConfigureFillImage();
            ApplyKind();
        }

        void ConfigureFillImage()
        {
            if (fillImage == null)
                return;
            fillImage.type = Image.Type.Filled;
            fillImage.fillMethod = Image.FillMethod.Vertical;
            fillImage.fillOrigin = (int)Image.OriginVertical.Bottom;
            fillImage.raycastTarget = false;
        }

        /// <summary>Set the fill level (0..1) — the normalized distance between the hover and select thresholds.</summary>
        public void SetProximity(float t01)
        {
            if (fillImage != null)
                fillImage.fillAmount = Mathf.Clamp01(t01);
        }

        /// <summary>Choose the sprite variant for the hotspot currently under the cursor (or hide for None).</summary>
        public void SetKind(CursorHotspotKind newKind)
        {
            if (newKind == kind)
                return;
            kind = newKind;
            ApplyKind();
        }

        void ApplyKind()
        {
            bool nav = kind == CursorHotspotKind.Navigation;
            bool inv = kind == CursorHotspotKind.Investigation;
            Sprite outline = nav ? navOutline : inv ? investigationOutline : null;
            Sprite fill = nav ? navFill : inv ? investigationFill : null;

            if (outlineImage != null)
            {
                outlineImage.sprite = outline;
                outlineImage.enabled = outline != null;
            }
            if (fillImage != null)
            {
                fillImage.sprite = fill;
                fillImage.enabled = fill != null;
            }

            bool visible = !hideWhenNone || kind != CursorHotspotKind.None;
            if (group != null)
                group.alpha = visible ? 1f : 0f;
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            ConfigureFillImage();
        }
#endif
    }
}
