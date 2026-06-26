using System.Collections.Generic;

namespace Kondo.Pointing
{
    /// <summary>
    /// Active-user selection by depth: the active user is whoever physically stands closest to
    /// the screen. Room space has +Z pointing into the room and the wall at negative Z, so the
    /// smallest <see cref="UserPointerManager.PointerState.BodyPosition"/>.z is the nearest body.
    /// Sticky: a challenger must stand a margin closer, and the holder keeps status for a minimum
    /// hold, so two people at similar distance don't cause the cursor to flicker between them.
    /// Pointing is not required (pairs with the horizontal "where you stand" pointing modes).
    /// </summary>
    public class ClosestToScreenSelector : IActiveUserSelector
    {
        readonly UserPointerManager manager;

        public ClosestToScreenSelector(UserPointerManager manager)
        {
            this.manager = manager;
        }

        public int Select(IReadOnlyDictionary<int, UserPointerManager.PointerState> states, int currentActiveId, float now)
        {
            UserPointerManager.PointerState active = null;
            if (currentActiveId >= 0)
                states.TryGetValue(currentActiveId, out active);
            bool activeValid = active != null && active.HasBody;

            UserPointerManager.PointerState best = null;
            foreach (UserPointerManager.PointerState st in states.Values)
            {
                if (!st.HasBody)
                    continue;
                if (best == null || st.BodyPosition.z < best.BodyPosition.z)
                    best = st;
            }

            if (!activeValid)
                return best?.UserId ?? -1;

            // active is valid, so best is at least the active user itself.
            if (best.UserId != active.UserId
                && best.BodyPosition.z < active.BodyPosition.z - manager.closestDepthStealMarginMeters
                && now - active.ActiveSince >= manager.closestMinHoldSeconds)
                return best.UserId;

            return active.UserId;
        }
    }
}
