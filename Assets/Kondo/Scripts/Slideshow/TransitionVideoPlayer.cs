using System;
using System.Collections;
using System.IO;
using RenderHeads.Media.AVProVideo;
using UnityEngine;

namespace Kondo.Slideshow
{
    /// <summary>
    /// The single shared player for transition videos. Opens media paused so the
    /// first frame sits ready under the slide before it fades out, and holds the
    /// last frame after playback so the next slide can fade in over it.
    /// </summary>
    public class TransitionVideoPlayer : MonoBehaviour
    {
        public MediaPlayer mediaPlayer;
        public DisplayUGUI display;

        Action onFinished;
        bool finishedFired;
        float prepareTime;
        bool nudged;
        double lastPosition;
        float lastProgressTime;
        bool rekicked;

        public string PreparedPath { get; private set; }
        public bool IsFirstFrameReady { get; private set; }
        public bool HasError { get; private set; }

        void Awake()
        {
            mediaPlayer.AutoOpen = false;
            mediaPlayer.AutoStart = false;
            mediaPlayer.Loop = false;
            mediaPlayer.Events.AddListener(OnMediaEvent);
            if (display != null)
            {
                display.NoDefaultDisplay = true;
                display.Player = mediaPlayer;
            }
        }

        void Update()
        {
            // Safety net in case the FinishedPlaying event is dropped by a backend.
            if (onFinished != null && !finishedFired &&
                mediaPlayer.Control != null && mediaPlayer.Control.IsFinished())
                FireFinished();

            // Stall watchdog: if playback was requested but the position stops
            // advancing, re-kick Play() once, then give up and skip to the end so
            // the show never hangs on a frozen frame.
            if (onFinished != null && !finishedFired && mediaPlayer.Control != null)
            {
                double pos = mediaPlayer.Control.GetCurrentTime();
                if (pos != lastPosition)
                {
                    lastPosition = pos;
                    lastProgressTime = Time.time;
                }
                else if (Time.time - lastProgressTime > 1.5f)
                {
                    lastProgressTime = Time.time;
                    if (!rekicked)
                    {
                        rekicked = true;
                        Debug.LogWarning($"[TransitionVideoPlayer] Playback stalled at {pos:F2}s on '{PreparedPath}' — re-issuing Play().");
                        mediaPlayer.Play();
                    }
                    else
                    {
                        Debug.LogError($"[TransitionVideoPlayer] Playback never advanced on '{PreparedPath}' — skipping to the end of the transition.");
                        FireFinished();
                    }
                }
            }

            // Some backends only upload the first frame once playback starts. If the
            // frame hasn't arrived shortly after the media is ready, nudge it with a
            // one-frame play/pause (FirstFrameReady then fires via the event).
            if (PreparedPath != null && !IsFirstFrameReady && !HasError && !nudged && onFinished == null &&
                Time.time - prepareTime > 0.75f &&
                mediaPlayer.Control != null && mediaPlayer.Control.CanPlay())
            {
                nudged = true;
                StartCoroutine(PlayPauseNudge());
            }
        }

        /// <summary>Open (paused) the video at the StreamingAssets-relative path. No-op if already prepared.</summary>
        public void Prepare(string relPath)
        {
            if (relPath == PreparedPath && !HasError)
                return;

            PreparedPath = relPath;
            IsFirstFrameReady = false;
            HasError = false;
            finishedFired = false;
            nudged = false;
            onFinished = null;
            prepareTime = Time.time;

            string fullPath = Path.Combine(Application.streamingAssetsPath, relPath ?? string.Empty);
            if (string.IsNullOrEmpty(relPath) || !File.Exists(fullPath))
            {
                Debug.LogError($"[TransitionVideoPlayer] Missing transition video: '{fullPath}'");
                HasError = true;
                return;
            }

            mediaPlayer.OpenMedia(MediaPathType.RelativeToStreamingAssetsFolder, relPath, autoPlay: false);
        }

        /// <summary>Play the prepared video; the callback fires once when it ends (paused on its last frame).</summary>
        public void Play(Action handleFinished)
        {
            bool canPlay = mediaPlayer.Control != null && mediaPlayer.Control.CanPlay();
            Debug.Log($"[TransitionVideoPlayer] Play requested '{PreparedPath}' canPlay={canPlay} t={Time.time:F2}");
            onFinished = handleFinished;
            finishedFired = false;
            lastPosition = -1.0;
            lastProgressTime = Time.time;
            rekicked = false;
            mediaPlayer.Play();
        }

        public void Close()
        {
            mediaPlayer.CloseMedia();
            PreparedPath = null;
            IsFirstFrameReady = false;
            HasError = false;
            onFinished = null;
        }

        void OnMediaEvent(MediaPlayer mp, MediaPlayerEvent.EventType evt, ErrorCode error)
        {
            // Event timeline in the log is the primary diagnostic for backend
            // differences between the editor and builds.
            Debug.Log($"[TransitionVideoPlayer] {evt} '{PreparedPath}' t={Time.time:F2}");
            switch (evt)
            {
                case MediaPlayerEvent.EventType.FirstFrameReady:
                    IsFirstFrameReady = true;
                    break;
                case MediaPlayerEvent.EventType.FinishedPlaying:
                    if (onFinished != null)
                        FireFinished();
                    break;
                case MediaPlayerEvent.EventType.Error:
                    Debug.LogError($"[TransitionVideoPlayer] Error '{error}' on '{PreparedPath}'");
                    HasError = true;
                    break;
            }
        }

        IEnumerator PlayPauseNudge()
        {
            mediaPlayer.Play();
            yield return null;
            yield return null;
            // Only complete the nudge if playback wasn't requested in the meantime.
            if (onFinished == null)
            {
                mediaPlayer.Pause();
                mediaPlayer.Control?.SeekFast(0.0);
            }
        }

        void FireFinished()
        {
            if (finishedFired)
                return;
            finishedFired = true;
            Debug.Log($"[TransitionVideoPlayer] Finished '{PreparedPath}' — holding last frame t={Time.time:F2}");
            mediaPlayer.Pause(); // hold the last frame
            Action callback = onFinished;
            onFinished = null;
            callback?.Invoke();
        }
    }
}
