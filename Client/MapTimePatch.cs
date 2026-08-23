using System;
using EFT;
using EFT.UI.Matchmaker;
using HarmonyLib;
using JsonType;

namespace RaidsAreInfinite.Client;

/// <summary>
/// Show real system time on the raid setup screen instead of the accelerated Tarkov clock.
///
/// Vanilla UpdateConditions writes GetCurrentLocationTime, which comes off the backend
/// session and advances at TimeFactor (7x), so the seconds visibly drift away from the
/// system clock. Update() calls UpdateConditions() every frame, so a postfix here re-writes
/// the same fields every frame and the displayed value stays locked to DateTime.Now.
///
/// Mirrors vanilla's own layout exactly: the alternate slot is the primary slot shifted by
/// -12h, and both branches are keyed off the same _takeFromCurrent / _selectedDateTime that
/// vanilla uses. Factory is left alone -- it shows two fixed hardcoded times, not a clock.
/// </summary>
[HarmonyPatch(typeof(LocationConditionsPanel), nameof(LocationConditionsPanel.UpdateConditions))]
public static class MapTimePatch
{
    // Mirrors the private _needTimerUpdate, which is false only for factory4_day/night.
    // Set() is the sole writer of that flag, so tracking Set() keeps us in step with it
    // without reflecting into a private field every frame.
    private static bool _isFactory;

    private static float _lastTrace;

    [HarmonyPatch(typeof(LocationConditionsPanel), nameof(LocationConditionsPanel.Set))]
    [HarmonyPostfix]
    public static void TrackFactory(RaidSettings raidSettings, bool takeFromCurrent)
    {
        string id = raidSettings?.SelectedLocation?.Id;
        _isFactory = id == "factory4_day" || id == "factory4_night";
        Plugin.Trace($"Set: location={id ?? "<null>"} isFactory={_isFactory} " +
                     $"takeFromCurrent={takeFromCurrent} selected={raidSettings?.SelectedDateTime}");
    }

    [HarmonyPostfix]
    public static void Postfix(LocationConditionsPanel __instance,
                               bool ____takeFromCurrent,
                               EDateTime ____selectedDateTime)
    {
        // Factory is logged in TrackFactory rather than here -- this runs every frame.
        if (_isFactory) return;

        try
        {
            // What vanilla just wrote, captured before we overwrite it. If the screen ever
            // shows the wrong clock, this line says whether we lost a write race or never ran.
            string vanilla = __instance._currentPhaseTime?.text;

            DateTime now = DateTime.Now;
            string written;

            if (____takeFromCurrent)
            {
                written = now.ToString("HH:mm:ss");
                __instance._currentPhaseTime.SetMonospaceText(written);
                if (__instance._nextPhaseTime != null)
                    __instance._nextPhaseTime.SetMonospaceText(now.AddHours(-12.0).ToString("HH:mm:ss"));
            }
            else
            {
                DateTime shown = ____selectedDateTime == EDateTime.CURR ? now : now.AddHours(-12.0);
                written = shown.ToString("HH:mm:ss");
                __instance._currentPhaseTime.SetMonospaceText(written);
            }

            if (Plugin.EnableLogging.Value &&
                UnityEngine.Time.unscaledTime - _lastTrace >= Plugin.LogIntervalSeconds.Value)
            {
                _lastTrace = UnityEngine.Time.unscaledTime;
                Plugin.Trace($"UpdateConditions: takeFromCurrent={____takeFromCurrent} " +
                             $"selected={____selectedDateTime} vanillaShowed={vanilla} " +
                             $"weWrote={written} system={now:HH:mm:ss}");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"MapTimePatch: {e}");
        }
    }
}
