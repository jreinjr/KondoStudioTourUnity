using System.Collections;
using RenderHeads.Media.AVProVideo;
using UnityEngine;

namespace Kondo.Slideshow
{
    /// <summary>
    /// A <see cref="SlideFadeInElement"/> that hosts a small windowed AVPro video
    /// (not the full-screen slide background). Typically listed in a hotspot's
    /// <c>overlayElements</c> so a ShowOverlay action fades the window in and starts
    /// the clip, then fades it out and releases the decoder when the overlay reverses.
    /// Lives outside the slide's <c>ZoomRoot</c> so the overlay zoom never scales it.
    /// </summary>
    public class SlidePopupVideoElement : SlideFadeInElement
    {
        [Header("Popup Video")]
        public MediaPlayer player;
        public DisplayUGUI display;
        [Tooltip("Video path relative to StreamingAssets, e.g. Slides/wheel_popup.mp4.")]
        public string videoPath;
        public bool loop = true;

        bool configured;

        void Awake()
        {
            Configure();
        }

        void Configure()
        {
            if (configured || player == null)
                return;
            player.AutoOpen = false;
            player.AutoStart = false;
            player.Loop = loop;
            if (display != null)
            {
                display.NoDefaultDisplay = true;
                display.Player = player;
            }
            configured = true;
        }

        public override void SetHidden()
        {
            base.SetHidden();
            // Don't open the decoder until the overlay actually fires.
            if (player != null && player.Control != null && player.Control.IsPlaying())
                player.Stop();
        }

        public override void Play(MonoBehaviour host)
        {
            Configure();
            if (player != null)
            {
                if (string.IsNullOrEmpty(videoPath))
                    Debug.LogWarning($"[SlidePopupVideoElement] {name}: no videoPath set — the popup will stay blank.", this);
                else
                    player.OpenMedia(MediaPathType.RelativeToStreamingAssetsFolder, videoPath, autoPlay: true);
            }
            base.Play(host);
        }

        public override void PlayOut(MonoBehaviour host)
        {
            base.PlayOut(host);
            // Hold the frame through the fade, then release the decoder so a parked
            // slide isn't keeping media open. CloseAfter runs on this element (it stays
            // active while its slide is current), surviving the host's other coroutines.
            if (player != null && isActiveAndEnabled)
                StartCoroutine(CloseAfter(Duration));
            else if (player != null)
                player.CloseMedia();
        }

        IEnumerator CloseAfter(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            if (player != null)
                player.CloseMedia();
        }
    }
}
