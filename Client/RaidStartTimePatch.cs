using System;
using System.Reflection;
using EFT;
using HarmonyLib;
using JsonType;

namespace RaidsAreInfinite.Client;

/// <summary>
/// Start the raid at the real system time, running at 1:1 with the system clock.
///
/// Two things have to happen, in this order, and both are driven from RaidSettings:
///
/// 1. HourOfDay must be non-(-1) so Fika's CoopGame.Create takes its custom-time branch.
///    Fika's own OfflineRaidSettingsMenuPatch_Override postfixes RaidSettingsWindow.Show and
///    hard-sets HourOfDay = -1. Show runs AFTER LocationConditionsPanel.Set, so writing this
///    from the panel is always discarded -- we have to postfix Show as well and run last.
///
/// 2. TimeFlowType must NOT be x1. Fika forces it to x1 for the same reason, and
///    CoopGame.Create only applies a custom TimeFactor when it differs from x1. Setting
///    x0_14 re-enables Fika's own code path rather than fighting it.
///
/// Note the enum is inverted from what the names suggest: ETimeFlowType.x1 maps to
/// TimeFactor 7f (normal accelerated Tarkov time) and x0_14 maps to 1f (real time, 1 second
/// per second). x0_14 is the value that keeps the raid clock aligned with the system clock.
///
/// Minutes and seconds are handled separately -- see RaidClockPrecisionPatch below.
/// Without Fika this patch still applies; vanilla reads the same RaidSettings fields.
/// </summary>
[HarmonyPatch]
public static class RaidStartTimePatch
{
    /// <summary>Real time captured at raid creation, used to seed the raid clock.</summary>
    public static DateTime PendingStart { get; private set; }

    static bool Prepare()
    {
        var t = AccessTools.TypeByName("Fika.Core.Main.GameMode.CoopGame");
        if (t == null)
        {
            Plugin.Log.LogInfo("RaidStartTimePatch: Fika not present, skipping.");
            return false;
        }
        if (TargetMethod() == null)
        {
            Plugin.Log.LogError("RaidStartTimePatch: CoopGame found but Create() did not " +
                                "resolve -- raid start time will NOT be applied.");
            return false;
        }
        return true;
    }

    static MethodBase TargetMethod()
    {
        var t = AccessTools.TypeByName("Fika.Core.Main.GameMode.CoopGame");
        return t == null ? null : AccessTools.Method(t, "Create");
    }

    // Prefix on Create, NOT a postfix on RaidSettingsWindow.Show.
    //
    // Show is the natural place to write raid settings, but it is not reliable: mods such as
    // RaidSettingsSkipper bypass that window entirely, so the postfix never runs, HourOfDay
    // stays -1 and Fika silently takes its default-time branch. Create is the method that
    // actually READS these values, so writing them here works whether or not the settings UI
    // was ever shown. timeAndWeather is a by-value struct parameter, so `ref` rewrites the
    // copy Create is about to use.
    [HarmonyPrefix]
    public static void Prefix(ref TimeAndWeatherSettings timeAndWeather,
                              LocationSettings.Location location,
                              EDateTime dateTime)
    {
        try
        {
            // Factory has no day/night clock -- it uses two fixed hardcoded times.
            string id = location?.Id;
            if (id == "factory4_day" || id == "factory4_night")
            {
                Plugin.Trace($"raid-setup: {id} is factory, leaving time alone");
                return;
            }

            // Honour the day/night choice made on the map screen. MapTimePatch displays the
            // CURR slot as system time and the PAST slot as system time -12h, so the raid has
            // to start at whichever of those the player actually picked -- using DateTime.Now
            // unconditionally ignores the selection and always starts a "day" raid.
            DateTime now = DateTime.Now;
            DateTime start = dateTime == EDateTime.PAST ? now.AddHours(-12.0) : now;
            PendingStart = start;

            timeAndWeather.HourOfDay = start.Hour;
            timeAndWeather.TimeFlowType = ETimeFlowType.x0_14;

            Plugin.Trace($"raid-setup: location={id ?? "<null>"} selected={dateTime} " +
                         $"HourOfDay={start.Hour} timeFlow=x0_14(1f) " +
                         $"start={start:HH:mm:ss} system={now:HH:mm:ss}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"RaidStartTimePatch: {e}");
        }
    }
}

/// <summary>
/// Carry minutes and seconds into the raid clock.
///
/// Fika's custom-time branch rebuilds the start DateTime as
///   new DateTime(stated.Year, stated.Month, stated.Day, HourOfDay, stated.Minute, stated.Second, ...)
/// -- it takes our hour but keeps the backend's minutes and seconds, so HourOfDay alone
/// yields 16:47 when the system clock says 16:02. Postfixing Create lets us re-seed the
/// already-constructed GameDateTime with full precision.
///
/// This runs as a postfix on Create, i.e. after Fika has finished its own time setup, so it
/// never races Fika's init. That ordering is the whole point: an earlier version of this mod
/// reset the clock from a different hook and landed raids at 00:45.
/// </summary>
[HarmonyPatch]
public static class RaidClockPrecisionPatch
{
    static bool Prepare()
    {
        var t = AccessTools.TypeByName("Fika.Core.Main.GameMode.CoopGame");
        if (t == null)
        {
            // No Fika installed -- expected, nothing to do.
            Plugin.Log.LogInfo("RaidClockPrecisionPatch: Fika not present, skipping.");
            return false;
        }
        if (TargetMethod() == null)
        {
            // Fika IS present but Create moved or changed shape. Silently skipping here is
            // how this mod previously shipped a build that did nothing, so say it loudly.
            Plugin.Log.LogError("RaidClockPrecisionPatch: CoopGame found but Create() did not " +
                                "resolve -- raid minutes/seconds will NOT be applied.");
            return false;
        }
        return true;
    }

    static MethodBase TargetMethod()
    {
        var t = AccessTools.TypeByName("Fika.Core.Main.GameMode.CoopGame");
        return t == null ? null : AccessTools.Method(t, "Create");
    }

    [HarmonyPostfix]
    public static void Postfix(object __result)
    {
        try
        {
            DateTime start = RaidStartTimePatch.PendingStart;
            if (start == default)
            {
                // The Create prefix never ran (or bailed, e.g. factory), so there is no
                // captured system time to apply. Say so -- otherwise minutes and seconds
                // silently fall back to the backend's values with no trace of why.
                Plugin.Trace("raid-clock: no PendingStart captured, leaving Fika's value");
                return;
            }

            var game = __result as AbstractGame;
            if (game == null)
            {
                Plugin.Trace($"raid-clock: Create returned {__result?.GetType().Name ?? "null"}, " +
                             "not an AbstractGame -- cannot reach GameDateTime");
                return;
            }

            var gdtProp = AccessTools.Property(game.GetType(), "GameDateTime")
                          ?? AccessTools.Property(typeof(BaseLocalGame<EftGamePlayerOwner>), "GameDateTime");
            var gdt = gdtProp?.GetValue(game) as GameDateTime;
            if (gdt == null)
            {
                Plugin.Trace("raid-clock: GameDateTime not reachable, leaving Fika's value");
                return;
            }

            // Keep the in-raid DATE that Fika/the backend established and correct only the
            // time of day. Overriding the date too would desync anything keyed off it.
            DateTime current = gdt.Calculate();
            DateTime seeded = new DateTime(current.Year, current.Month, current.Day,
                                           start.Hour, start.Minute, start.Second, start.Millisecond);

            // Reset(DateTime) silently does nothing while the clock is Locked, so report
            // that case rather than logging a change that never happened.
            if (gdt.Locked)
            {
                Plugin.Trace($"raid-clock: LOCKED, reset skipped (wanted {seeded:HH:mm:ss})");
                return;
            }

            gdt.Reset(seeded);

            Plugin.Trace($"raid-clock: seeded {current:HH:mm:ss} -> {gdt.Calculate():HH:mm:ss} " +
                         $"(system={start:HH:mm:ss}) timeFactor={gdt.TimeFactor}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"RaidClockPrecisionPatch: {e}");
        }
    }
}
