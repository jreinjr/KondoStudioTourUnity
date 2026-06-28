using System.Collections.Generic;

namespace Kondo.Pointing
{
    /// <summary>
    /// Active-user selection by centrality within the closest occupied depth zone: the active
    /// user is the most central (closest to the screen's center axis) user standing in the
    /// nearest zone that anyone occupies — the Select zone first, then the Hover zone, then any
    /// detected user. Centrality reuses <see cref="UserPointerManager.PointerState.Centrality"/>
    /// (smaller = more central); zones compare <see cref="UserPointerManager.PointerState.BodyPosition"/>.z
    /// against the manager's MaxSelectZ / MaxHoverZ. Light stickiness (reusing the central
    /// selector's steal margin / min hold) keeps two near-equally-central users from flickering
    /// the cursor. Pointing is not required (pairs with the "where you stand" pointing modes).
    /// </summary>
    public class CentralClosestZoneSelector : IActiveUserSelector
    {
        readonly UserPointerManager manager;

        public CentralClosestZoneSelector(UserPointerManager manager)
        {
            this.manager = manager;
        }

        public int Select(IReadOnlyDictionary<int, UserPointerManager.PointerState> states, int currentActiveId, float now)
        {
            // Most central user within each cumulative depth tier (Select ⊂ Hover ⊂ any).
            UserPointerManager.PointerState bestSelect = null, bestHover = null, bestAny = null;
            foreach (UserPointerManager.PointerState st in states.Values)
            {
                if (!st.HasBody)
                    continue;
                if (st.BodyPosition.z <= manager.maxSelectZ)
                    Pick(ref bestSelect, st);
                if (st.BodyPosition.z <= manager.maxHoverZ)
                    Pick(ref bestHover, st);
                Pick(ref bestAny, st);
            }

            UserPointerManager.PointerState best = bestSelect ?? bestHover ?? bestAny;
            if (best == null)
                return -1;

            // Light stickiness: keep the current holder while they still lead the winning tier,
            // unless a challenger is a margin more central and the minimum hold has elapsed.
            if (currentActiveId >= 0 && states.TryGetValue(currentActiveId, out var active) && active.HasBody)
            {
                bool activeInWinningTier =
                    (best == bestSelect && active.BodyPosition.z <= manager.maxSelectZ) ||
                    (best == bestHover && bestSelect == null && active.BodyPosition.z <= manager.maxHoverZ) ||
                    (best == bestAny && bestSelect == null && bestHover == null);
                bool stolen = best.UserId != active.UserId
                           && best.Centrality < active.Centrality - manager.stealMarginMeters
                           && now - active.ActiveSince >= manager.minHoldSeconds;
                if (activeInWinningTier && !stolen)
                    return active.UserId;
            }

            return best.UserId;
        }

        /// <summary>Keep the more-central (smaller Centrality) of the current best and the candidate.</summary>
        static void Pick(ref UserPointerManager.PointerState best, UserPointerManager.PointerState candidate)
        {
            if (best == null || candidate.Centrality < best.Centrality)
                best = candidate;
        }
    }
}
