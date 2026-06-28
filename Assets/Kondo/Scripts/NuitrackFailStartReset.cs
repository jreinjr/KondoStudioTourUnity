using UnityEngine;

namespace Kondo
{
    /// <summary>
    /// Clears Nuitrack's <c>failStart</c> PlayerPref before any scene loads, every launch.
    /// <para>
    /// Nuitrack sets <c>failStart=1</c> just before its risky native sensor init and only resets it
    /// to 0 on success (<c>NuitrackManager.NuitrackInit</c>). When a native init crash takes the
    /// editor/player down, that reset never runs, so <c>failStart</c> stays 1 and the <b>next</b>
    /// launch returns early without initializing Nuitrack at all — skeletons silently stop working
    /// until PlayerPrefs is cleared by hand.
    /// </para>
    /// <para>
    /// This forces a clean slate so every launch actually attempts init. Trade-off: it removes the
    /// crash-guard, so a bad launch may crash rather than coming up dormant — preferred here because
    /// a relaunch is cheaper than silently-dead tracking on a kiosk. Lives under Assets/Kondo so an
    /// SDK reimport can't overwrite it. Note: with <c>asyncInit=true</c> the guard is bypassed anyway;
    /// this protects the synchronous path if async is ever toggled off.
    /// </para>
    /// </summary>
    static class NuitrackFailStartReset
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void ClearFailStart()
        {
            if (PlayerPrefs.GetInt("failStart", 0) != 0)
            {
                PlayerPrefs.SetInt("failStart", 0);
                PlayerPrefs.Save();
                Debug.Log("[Kondo] Cleared stale Nuitrack 'failStart' flag (prior init crash). Re-attempting sensor init this launch.");
            }
        }
    }
}
