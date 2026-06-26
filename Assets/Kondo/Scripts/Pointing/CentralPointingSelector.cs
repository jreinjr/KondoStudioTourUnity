using System.Collections.Generic;

namespace Kondo.Pointing
{
    /// <summary>
    /// Active-user selection by centrality: the active user is the pointing skeleton standing
    /// closest to the screen's center axis, with sticky hand-off (a challenger must be a margin
    /// more central, and the holder keeps status for a minimum hold and a grace period after it
    /// stops pointing). This is the original <c>UserPointerManager.SelectActiveUser</c> logic,
    /// reading its thresholds from the manager so on-site tuning is preserved.
    /// </summary>
    public class CentralPointingSelector : IActiveUserSelector
    {
        readonly UserPointerManager manager;

        public CentralPointingSelector(UserPointerManager manager)
        {
            this.manager = manager;
        }

        public int Select(IReadOnlyDictionary<int, UserPointerManager.PointerState> states, int currentActiveId, float now)
        {
            UserPointerManager.PointerState active = null;
            if (currentActiveId >= 0)
                states.TryGetValue(currentActiveId, out active);

            // Best challenger: most central user that has been pointing long enough.
            UserPointerManager.PointerState best = null;
            foreach (UserPointerManager.PointerState st in states.Values)
            {
                if (st.UserId == currentActiveId)
                    continue;
                if (!st.Sample.IsPointing || st.PointingDuration < manager.candidateMinPointSeconds)
                    continue;
                if (best == null || st.Centrality < best.Centrality)
                    best = st;
            }

            if (active != null)
            {
                bool stillPointing = active.Sample.IsPointing
                                  || now - active.LastPointingTime <= manager.activeLossGraceSeconds;
                if (!stillPointing)
                    return best?.UserId ?? -1;
                if (best != null
                    && best.Centrality < active.Centrality - manager.stealMarginMeters
                    && now - active.ActiveSince >= manager.minHoldSeconds)
                    return best.UserId;
                return active.UserId;
            }

            return best?.UserId ?? -1;
        }
    }
}
