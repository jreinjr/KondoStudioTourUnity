using System;
using UnityEngine;

namespace Kondo.Core
{
    /// <summary>
    /// Tuning parameters for a One Euro filter (Casiez et al. 2012).
    /// </summary>
    [Serializable]
    public struct OneEuroParams
    {
        [Tooltip("Cutoff frequency (Hz) at rest. Lower = smoother but laggier when holding still.")]
        [Min(0.01f)] public float minCutoff;

        [Tooltip("Speed coefficient. Higher = snappier response during fast motion (lag is traded away first where it is most visible).")]
        [Min(0f)] public float beta;

        [Tooltip("Cutoff frequency (Hz) for the internal velocity estimate. 1.0 is a good default; rarely needs tuning.")]
        [Min(0.01f)] public float dCutoff;

        public OneEuroParams(float minCutoff, float beta, float dCutoff = 1f)
        {
            this.minCutoff = minCutoff;
            this.beta = beta;
            this.dCutoff = dCutoff;
        }

        public static float Alpha(float cutoff, float dt)
        {
            float tau = 1f / (2f * Mathf.PI * Mathf.Max(cutoff, 1e-3f));
            return 1f / (1f + tau / dt);
        }
    }

    /// <summary>
    /// One Euro filter for Vector3. A single adaptive cutoff is derived from the full
    /// vector speed so all components share the same lag (per-axis cutoffs would bend
    /// a filtered direction vector).
    /// </summary>
    public class OneEuroFilterVector3
    {
        Vector3 prevRaw;
        Vector3 value;
        float speedEstimate;
        bool hasPrev;

        public Vector3 Value => value;
        public bool IsInitialized => hasPrev;

        public Vector3 Filter(Vector3 x, float dt, in OneEuroParams p)
        {
            if (dt <= 1e-6f)
                return hasPrev ? value : x;

            if (!hasPrev)
            {
                hasPrev = true;
                prevRaw = x;
                value = x;
                speedEstimate = 0f;
                return x;
            }

            float speed = (x - prevRaw).magnitude / dt;
            prevRaw = x;
            speedEstimate += OneEuroParams.Alpha(p.dCutoff, dt) * (speed - speedEstimate);

            float cutoff = p.minCutoff + p.beta * speedEstimate;
            value += OneEuroParams.Alpha(cutoff, dt) * (x - value);
            return value;
        }

        public void Reset()
        {
            hasPrev = false;
        }
    }

    /// <summary>
    /// One Euro filter for Vector2 (see <see cref="OneEuroFilterVector3"/>).
    /// </summary>
    public class OneEuroFilterVector2
    {
        Vector2 prevRaw;
        Vector2 value;
        float speedEstimate;
        bool hasPrev;

        public Vector2 Value => value;
        public bool IsInitialized => hasPrev;

        public Vector2 Filter(Vector2 x, float dt, in OneEuroParams p)
        {
            if (dt <= 1e-6f)
                return hasPrev ? value : x;

            if (!hasPrev)
            {
                hasPrev = true;
                prevRaw = x;
                value = x;
                speedEstimate = 0f;
                return x;
            }

            float speed = (x - prevRaw).magnitude / dt;
            prevRaw = x;
            speedEstimate += OneEuroParams.Alpha(p.dCutoff, dt) * (speed - speedEstimate);

            float cutoff = p.minCutoff + p.beta * speedEstimate;
            value += OneEuroParams.Alpha(cutoff, dt) * (x - value);
            return value;
        }

        public void Reset()
        {
            hasPrev = false;
        }
    }
}
