using System;
using Comfort.Common;
using EFT;
using HarmonyLib;

namespace RaidsAreInfinite.Client;

/// <summary>
/// Make the raid effectively endless without breaking anything that depends on the timer.
///
/// Approach borrowed from ImmersiveRaidTime: EXTEND SessionTime rather than clear it.
///
/// An earlier version of this mod set GameTimer.SessionTime = null, reasoning that every
/// end-of-raid check is guarded by SessionTime.HasValue. That broke extraction, for two
/// reasons that only show up off the main path:
///
///   * Fika's FikaServer reads gameTimer.SessionTime.Value UNGUARDED when building an
///     InformationPacket, so a null throws on the host on every broadcast.
///   * Fika's FikaTimeManager.Update is a third MIA site, separate from EndByTimerScenario
///     and CoopGame.Stop, and it force-extracts the player.
///
/// Pushing SessionTime out to the year 2200 keeps it non-null, so every one of those readers
/// keeps working normally, while `PastTime >= SessionTime` stays false for ~174 years. That
/// disarms all three MIA sites at once without patching any of them.
///
/// ChangeSessionTime also re-derives _escapeDateTime from StartDateTime + SessionTime and
/// raises SessionTimeChanged, so the timer panel updates itself -- no reflection needed.
/// </summary>
[HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
public static class InfiniteRaidPatch
{
    private static readonly DateTime FarFuture = new DateTime(2200, 1, 1);

    [HarmonyPostfix]
    public static void Postfix()
    {
        try
        {
            // Deliberately NOT host-gated. EndByTimerScenario and FikaTimeManager run on every
            // machine and evaluate that machine's own GameTimer, so a client whose timer still
            // holds the original session would be force-extracted as MIA even though the host
            // carries on. The host's SessionTime is not synced to GameTimer -- SetClientTime
            // assigns BaseGameController.SessionTime, a different field.

            if (!Singleton<AbstractGame>.Instantiated)
            {
                Plugin.Trace("infinite-raid: AbstractGame not instantiated, cannot extend timer");
                return;
            }

            var timer = Singleton<AbstractGame>.Instance.GameTimer;
            if (timer == null)
            {
                Plugin.Trace("infinite-raid: GameTimer was null, raid MAY still end as MIA");
                return;
            }

            TimeSpan? before = timer.SessionTime;
            TimeSpan infinite = FarFuture - (timer.StartDateTime ?? DateTime.UtcNow);
            timer.ChangeSessionTime(infinite);

            // ChangeSessionTime updates SessionTime and _escapeDateTime, but MainTimerPanel
            // counts against its OWN _dateTime, cached when Show() was called. Without this
            // the panel keeps counting down the original session and would still reach zero,
            // even though MIA itself is disarmed. ImmersiveRaidTime carries the same line
            // with the comment "this can't be deleted".
            var panel = UnityEngine.Object.FindObjectOfType<EFT.UI.BattleTimer.MainTimerPanel>();
            if (panel != null && timer.EscapeDateTime.HasValue)
            {
                AccessTools.Field(typeof(EFT.UI.BattleTimer.TimerPanel), "_dateTime")
                           ?.SetValue(panel, timer.EscapeDateTime.Value);
                Plugin.Trace($"infinite-raid: panel _dateTime re-pointed to {timer.EscapeDateTime.Value:yyyy-MM-dd HH:mm}");
            }
            else
            {
                Plugin.Trace($"infinite-raid: MainTimerPanel not found (panel={panel != null}), " +
                             "its countdown still targets the ORIGINAL session end");
            }

            Plugin.Trace($"infinite-raid: [{FikaRole.Describe()}] SessionTime " +
                         $"{before?.ToString() ?? "<null>"} -> {timer.SessionTime} " +
                         $"(ends {FarFuture:yyyy-MM-dd}, MIA disarmed)");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"InfiniteRaidPatch: {e}");
        }
    }
}
