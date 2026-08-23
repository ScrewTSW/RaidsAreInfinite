using System;
using System.Reflection;
using EFT;
using HarmonyLib;
using JsonType;

namespace RaidsAreInfinite.Client;

/// <summary>
/// Start the raid at the host's real system time, running 1:1 with the system clock, and let
/// Fika's own machinery carry that time to every client.
///
/// The write happens in a prefix on TarkovApplication.LocalGameCreate, which Fika itself
/// prefixes to divert into its async CreateFikaGame. Placement in that sequence is the point:
///
///     LocalGameCreate(gameWorld, timeAndWeather, ...)   <-- we write here, Priority.First
///       -> Fika's prefix returns false, calls CreateFikaGame(timeAndWeather, raidSettings)
///            await session.SendRaidSettings(raidSettings)  <-- host publishes to the server
///            if (!isServer) {                              <-- client fetches back
///                var resp = await FikaRequestHandler.GetRaidSettings(...);
///                timeAndWeather.HourOfDay    = resp.HourOfDay;
///                timeAndWeather.TimeFlowType = resp.TimeFlowType;
///            }
///            CoopGame.Create(..., timeAndWeather, ...)     <-- both sides seed their clock
///
/// Writing before SendRaidSettings is what lets the host's value reach clients at all. Note it
/// must go into BOTH the timeAndWeather parameter (what the HOST's own Create reads) and
/// raidSettings.TimeAndWeatherSettings (what SendRaidSettings actually publishes) -- they are
/// separate copies, and writing only the parameter left every client on the backend default
/// with no "Using custom time" line at all.
///
/// Targeting LocalGameCreate rather than CreateFikaGame is deliberate: the latter is `async`
/// and compiled into a state machine, where a prefix is unreliable. LocalGameCreate is an
/// ordinary method whose parameter can be rewritten by ref before Fika ever sees it.
///
/// This gets the CLOCK right on every machine. It does not by itself fix client LIGHTING --
/// TOD_Time keeps its own GameDateTime reference, which ClientTimeSyncPatch rebinds.
///
/// Two constraints on the values themselves:
///
/// 1. HourOfDay must be non-(-1) for CoopGame.Create's custom-time branch. Fika's
///    OfflineRaidSettingsMenuPatch_Override hard-sets it to -1 in a postfix on
///    RaidSettingsWindow.Show, and RaidSettingsSkipper can stop Show running at all, so
///    neither that window nor LocationConditionsPanel is a reliable place to write.
///
/// 2. TimeFlowType must differ from x1, because Create only applies a custom TimeFactor when
///    it does. Note ETimeFlowType is inverted from what the names suggest: x1 maps to
///    TimeFactor 7f (normal accelerated Tarkov time) and x0_14 maps to 1f (real time).
/// </summary>
[HarmonyPatch]
public static class RaidStartTimePatch
{
    /// <summary>Raid start time chosen by the host, used to seed the clock precisely.</summary>
    public static DateTime PendingStart { get; private set; }

    static bool Prepare()
    {
        if (TargetMethod() == null)
        {
            Plugin.Log.LogError("RaidStartTimePatch: TarkovApplication.LocalGameCreate did not " +
                                "resolve -- raid start time will NOT be applied.");
            return false;
        }
        return true;
    }

    // TarkovApplication.LocalGameCreate, NOT Fika's CreateFikaGame. Fika prefixes
    // LocalGameCreate and hands timeAndWeather straight to its own async CreateFikaGame, so
    // both sit at the same point in the sequence -- but CreateFikaGame is `async`, which the
    // compiler rewrites into a state machine, making a prefix on it unreliable. LocalGameCreate
    // is an ordinary synchronous method whose parameter we can safely rewrite by ref.
    static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(TarkovApplication), "LocalGameCreate");

    // Runs before Fika's own prefix, which returns false and diverts execution into its async
    // CreateFikaGame -- so this must be Priority.First to get the write in beforehand. Fika
    // then passes the mutated timeAndWeather onward, publishes it via SendRaidSettings, and
    // every client fetches the same value back through GetRaidSettings.
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    public static void Prefix(TarkovApplication __instance, ref TimeAndWeatherSettings timeAndWeather)
    {
        var raidSettings = __instance?._raidSettings;
        try
        {
            // Only the host decides the time. Clients receive it through Fika's own
            // GetRaidSettings round-trip a few lines further into this same method.
            if (!FikaRole.IsAuthoritative)
            {
                Plugin.Trace("raid-setup: CLIENT, will take HourOfDay from the host via Fika");
                return;
            }

            // Factory has no day/night clock -- it uses two fixed hardcoded times.
            string id = raidSettings?.SelectedLocation?.Id;
            if (id == "factory4_day" || id == "factory4_night")
            {
                Plugin.Trace($"raid-setup: {id} is factory, leaving time alone");
                return;
            }

            // Honour the day/night choice made on the map screen. MapTimePatch shows the CURR
            // slot as system time and the PAST slot as system time -12h, so the raid starts at
            // whichever the host actually picked.
            EDateTime selected = raidSettings?.SelectedDateTime ?? EDateTime.CURR;
            DateTime now = DateTime.Now;
            DateTime start = selected == EDateTime.PAST ? now.AddHours(-12.0) : now;
            PendingStart = start;

            // BOTH copies have to be written, and they are not the same object.
            //
            //   timeAndWeather                    -> the parameter CoopGame.Create seeds from,
            //                                        i.e. what the HOST's own clock uses.
            //   raidSettings.TimeAndWeatherSettings -> the field SendRaidSettings publishes to
            //                                        the server, i.e. what CLIENTS fetch back
            //                                        through GetRaidSettings.
            //
            // Writing only the parameter left the host correct and every client on the backend
            // default: host raid clock 16:00:23 while the client's sky sat at 05:15:58, with no
            // "Using custom time" line on the client at all because its HourOfDay was still -1.
            timeAndWeather.HourOfDay = start.Hour;
            timeAndWeather.TimeFlowType = ETimeFlowType.x0_14;

            if (raidSettings != null)
            {
                raidSettings.TimeAndWeatherSettings.HourOfDay = start.Hour;
                raidSettings.TimeAndWeatherSettings.TimeFlowType = ETimeFlowType.x0_14;
            }

            Plugin.Trace($"raid-setup: [HOST] location={id ?? "<null>"} selected={selected} " +
                         $"HourOfDay={start.Hour} timeFlow=x0_14(1f) " +
                         $"start={start:HH:mm:ss} system={now:HH:mm:ss} " +
                         $"(param+raidSettings written, raidSettings={(raidSettings != null ? "yes" : "NULL")})");
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
/// -- it takes the hour but keeps the backend's minutes and seconds, so HourOfDay alone yields
/// 16:47 when the clock says 16:02. Postfixing Create re-seeds the constructed GameDateTime
/// with full precision, after Fika's own init rather than racing it.
///
/// Host-only. The client's clock is owned by the HourOfDay it received from the host plus
/// Fika's SetClientTime sync, and re-seeding it locally would reintroduce per-machine drift.
/// </summary>
[HarmonyPatch]
public static class RaidClockPrecisionPatch
{
    static bool Prepare()
    {
        var t = AccessTools.TypeByName("Fika.Core.Main.GameMode.CoopGame");
        if (t == null)
        {
            Plugin.Log.LogInfo("RaidClockPrecisionPatch: Fika not present, skipping.");
            return false;
        }
        if (TargetMethod() == null)
        {
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
            if (!FikaRole.IsAuthoritative)
            {
                Plugin.Trace("raid-clock: CLIENT, using the host's HourOfDay and synced clock");
                return;
            }

            DateTime start = RaidStartTimePatch.PendingStart;
            if (start == default)
            {
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

            // Reset(DateTime) silently does nothing while the clock is Locked, so report that
            // case rather than logging a change that never happened.
            if (gdt.Locked)
            {
                Plugin.Trace($"raid-clock: LOCKED, reset skipped (wanted {seeded:HH:mm:ss})");
                return;
            }

            gdt.Reset(seeded);

            Plugin.Trace($"raid-clock: [HOST] seeded {current:HH:mm:ss} -> {gdt.Calculate():HH:mm:ss} " +
                         $"(system={start:HH:mm:ss}) timeFactor={gdt.TimeFactor}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"RaidClockPrecisionPatch: {e}");
        }
    }
}
