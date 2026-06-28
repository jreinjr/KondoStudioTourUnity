using System.Collections.Generic;

namespace Kondo.Pointing
{
    /// <summary>
    /// Single-user / diagnostic active-user selection: ignores centrality, depth zones, pointing,
    /// and stickiness entirely and simply locks the cursor to the first skeleton in the state set.
    /// Use it to drive a single-visitor installation, or to isolate whether a missing cursor is a
    /// selection problem (cursor returns under this mode) or an upstream pointing/tracking problem
    /// (still no cursor — the chosen user just isn't producing a screen aim).
    /// </summary>
    public class SingleUserSelector : IActiveUserSelector
    {
        public int Select(IReadOnlyDictionary<int, UserPointerManager.PointerState> states, int currentActiveId, float now)
        {
            foreach (UserPointerManager.PointerState st in states.Values)
                return st.UserId;
            return -1;
        }
    }
}
