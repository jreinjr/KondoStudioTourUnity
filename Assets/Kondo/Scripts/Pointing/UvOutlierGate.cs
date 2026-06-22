using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kondo.Pointing
{
    [Serializable]
    public class UvOutlierGateConfig
    {
        [Tooltip("Discard a raw sample that lands farther than this (normalized screen units) from the moving average of recently accepted samples. 0 disables the gate.")]
        [Min(0f)] public float maxJumpFromAverage = 0.4f;

        [Tooltip("Accepted samples older than this (seconds) drop out of the moving average. Longer windows reject more, but the average lags farther behind a fast sweep.")]
        [Min(0.02f)] public float averageWindowSeconds = 0.15f;

        [Tooltip("After this many consecutive discards the sample is accepted anyway — a sustained displacement is a real retarget, not a glitch — and the average restarts there.")]
        [Min(1)] public int maxConsecutiveDiscards = 3;
    }

    /// <summary>
    /// Single-frame glitch rejection for the cursor. The One Euro filter's speed-adaptive
    /// cutoff deliberately tracks fast motion, which also makes it follow tracking spikes;
    /// this gate discards a raw UV sample that lands implausibly far from the moving
    /// average of recently accepted samples, so the cursor holds instead of leaping.
    /// Consecutive discards are capped: once the cap is hit the displacement is treated
    /// as a genuine retarget, accepted, and the average restarts at the new position.
    /// </summary>
    public class UvOutlierGate
    {
        struct Entry
        {
            public float Age;
            public Vector2 Uv;
        }

        readonly List<Entry> history = new List<Entry>();
        int consecutiveDiscards;

        /// <summary>True when the sample should be used, false to discard it.</summary>
        public bool Accept(Vector2 uv, float dt, UvOutlierGateConfig cfg)
        {
            for (int i = history.Count - 1; i >= 0; i--)
            {
                Entry e = history[i];
                e.Age += dt;
                if (e.Age > cfg.averageWindowSeconds)
                    history.RemoveAt(i);
                else
                    history[i] = e;
            }

            if (cfg.maxJumpFromAverage > 0f && history.Count > 0)
            {
                Vector2 average = Vector2.zero;
                for (int i = 0; i < history.Count; i++)
                    average += history[i].Uv;
                average /= history.Count;

                if ((uv - average).magnitude > cfg.maxJumpFromAverage)
                {
                    if (consecutiveDiscards < cfg.maxConsecutiveDiscards)
                    {
                        consecutiveDiscards++;
                        return false;
                    }
                    history.Clear();
                }
            }

            consecutiveDiscards = 0;
            history.Add(new Entry { Uv = uv });
            return true;
        }

        public void Reset()
        {
            history.Clear();
            consecutiveDiscards = 0;
        }
    }
}
