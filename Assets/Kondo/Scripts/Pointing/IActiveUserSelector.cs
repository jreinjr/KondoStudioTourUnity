using System.Collections.Generic;
using UnityEngine;

namespace Kondo.Pointing
{
    /// <summary>Which user owns the single slideshow-driving cursor. Selected on <see cref="UserPointerManager"/>.</summary>
    public enum ActiveUserMode
    {
        [Tooltip("The user pointing at the screen who stands closest to the screen's center axis (sticky hand-off). The original behavior.")]
        CentralPointing,
        [Tooltip("Whoever physically stands closest to the screen (smallest room-space distance to the wall), regardless of pointing.")]
        ClosestToScreen,
        [Tooltip("Most central user (closest to the screen's center axis) within the closest occupied depth zone: Select zone first, then Hover zone, then any detected user.")]
        CentralClosestZone,
        [Tooltip("Ignores all active-user logic; locks the cursor to the first detected skeleton. Single-visitor / diagnostic mode.")]
        SingleUser,
        [Tooltip("First-come, first-served: the earliest-seen user within hover distance owns the cursor and keeps it while they stay within hover distance; when they leave it, the next-earliest user within hover distance takes over.")]
        FirstSeenInHover,
    }

    /// <summary>
    /// Chooses the active user each frame from the current pointer states. Returns the chosen
    /// user id, or -1 for none; <see cref="UserPointerManager"/> applies the result (and stamps
    /// the active-since time on a change). Implementations read their tuning from the manager.
    /// </summary>
    public interface IActiveUserSelector
    {
        int Select(IReadOnlyDictionary<int, UserPointerManager.PointerState> states, int currentActiveId, float now);
    }
}
