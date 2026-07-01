using System.Collections.Generic;

namespace Kondo.Pointing
{
    /// <summary>
    /// First-come, first-served active-user selection: the earliest-seen user standing within
    /// hover distance of the screen owns the cursor and keeps it while they remain visible and
    /// within hover distance — regardless of centrality or pointing. The moment the holder steps
    /// back out of hover distance (or their skeleton is lost) control transfers to the next
    /// earliest-seen user who is currently within hover distance, or to no one if the hover zone
    /// is empty. "Within hover distance" means room-space <see cref="UserPointerManager.PointerState.BodyPosition"/>.z
    /// is at or nearer than the manager's MaxHoverZ (wall is at negative Z, so smaller = closer);
    /// "earliest seen" uses the monotonic <see cref="UserPointerManager.PointerState.FirstSeenArrival"/>
    /// stamp, which survives Nuitrack id recycling. Depth hysteresis comes from a small exit
    /// margin: the holder is retained until they step past MaxHoverZ + HoverExitMarginMeters,
    /// while a fresh user must be within MaxHoverZ to acquire — preventing boundary flicker. No
    /// steal margin or hold timer is applied.
    /// </summary>
    public class FirstSeenInHoverSelector : IActiveUserSelector
    {
        readonly UserPointerManager manager;

        public FirstSeenInHoverSelector(UserPointerManager manager)
        {
            this.manager = manager;
        }

        public int Select(IReadOnlyDictionary<int, UserPointerManager.PointerState> states, int currentActiveId, float now)
        {
            // Retain the current holder until they step past the hover edge plus the exit margin.
            if (currentActiveId >= 0
                && states.TryGetValue(currentActiveId, out var active)
                && IsWithinHover(active, manager.maxHoverZ + manager.hoverExitMarginMeters))
                return active.UserId;

            // Otherwise hand off to the earliest-seen user strictly within hover distance.
            UserPointerManager.PointerState first = null;
            foreach (UserPointerManager.PointerState st in states.Values)
            {
                if (!IsWithinHover(st, manager.maxHoverZ))
                    continue;
                if (first == null || st.FirstSeenArrival < first.FirstSeenArrival)
                    first = st;
            }

            return first?.UserId ?? -1;
        }

        static bool IsWithinHover(UserPointerManager.PointerState st, float maxZ) =>
            st.HasBody && st.BodyPosition.z <= maxZ;
    }
}
