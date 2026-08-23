using System;
using EFT;
using EFT.UI.BattleTimer;
using HarmonyLib;

namespace RaidsAreInfinite.Client;

/// <summary>
/// Show the current system clock on the in-raid timer instead of a countdown.
///
/// Approach borrowed from ImmersiveRaidTime, which is simpler and more robust than writing
/// the text directly: mutate the `ref TimeSpan` that SetTimerText receives and let vanilla
/// render it. Vanilla formats the span as Hours:Minutes:Seconds, so handing it
/// DateTime.Now.TimeOfDay produces a wall clock for free -- no manual SetMonospaceText, and
/// no need to force the panel visible, because a positive span passes vanilla's
/// `timeSpan.TotalSeconds <= 0` guard on its own.
///
/// Hook point matters. MainTimerPanel.UpdateTimer reads the protected TimeSpan field to drive
///     _canvasGroup.ignoreParentGroups / blocksRaycasts   (600s threshold)
///     the red warning colour and the one-shot ForcePull reveal
/// but that field is set in UpdateTimer BEFORE SetTimerText is called, so changing the value
/// here affects rendering only. Overriding the field itself would pin blocksRaycasts and
/// leave an invisible raycast blocker over the end-of-raid screen -- a bug this mod shipped
/// once already.
///
/// SetTimerText is declared on TimerPanel and inherited unchanged by ExitTimerPanel,
/// TransitTimerPanel and CustomTimerPanel, so the instance check keeps extraction and transit
/// countdowns counting down.
/// </summary>
[HarmonyPatch(typeof(TimerPanel), nameof(TimerPanel.SetTimerText))]
public static class RaidTimerPatch
{
    private static float _lastTrace;
    private static bool _loggedOtherPanel;

    [HarmonyPrefix]
    public static void Prefix(TimerPanel __instance, ref TimeSpan timeSpan)
    {
        if (!Plugin.ShowClockInRaid.Value) return;

        // Extraction/transit countdowns must keep counting down.
        if (!(__instance is MainTimerPanel))
        {
            // Once per session is enough -- these tick every second while extracting.
            if (!_loggedOtherPanel)
            {
                _loggedOtherPanel = true;
                Plugin.Trace($"timer: leaving {__instance.GetType().Name} alone (not the raid timer)");
            }
            return;
        }

        try
        {
            TimeSpan countdown = timeSpan;

            // Show the RAID clock, not DateTime.Now. The raid starts at the host's selected
            // time (which may be system time -12h for a night raid) and Fika syncs that one
            // GameDateTime to every client, so this is the value that matches the sky and is
            // identical on all machines. Rendering the local system clock instead made a 15:32
            // daytime raid display 03:36, and made two players agree only because their PCs
            // happened to share a timezone.
            var world = Comfort.Common.Singleton<GameWorld>.Instance;
            var raidClock = world?.GameDateTime;
            timeSpan = raidClock != null ? raidClock.Calculate().TimeOfDay : DateTime.Now.TimeOfDay;

            if (Plugin.EnableLogging.Value &&
                UnityEngine.Time.unscaledTime - _lastTrace >= Plugin.LogIntervalSeconds.Value)
            {
                _lastTrace = UnityEngine.Time.unscaledTime;
                // countdown= is what vanilla would have rendered; a value that keeps shrinking
                // toward zero means the infinite-raid patch is not holding.
                Plugin.Trace($"timer: [{FikaRole.Describe()}] wrote {timeSpan:hh\\:mm\\:ss} " +
                             $"(raidClock={(raidClock != null ? "yes" : "NO-fallback-to-system")}, " +
                             $"system={DateTime.Now:HH:mm:ss}, vanilla countdown={countdown})");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"RaidTimerPatch: {e}");
        }
    }
}
