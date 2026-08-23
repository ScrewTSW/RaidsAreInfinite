using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace RaidsAreInfinite.Client;

[BepInPlugin("eu.thescrewcollab.raidsareinfinite.client", "RaidsAreInfinite.Client", "1.0.0")]
public class Plugin : BaseUnityPlugin
{
    public static ManualLogSource Log;
    public static ConfigEntry<bool> EnableLogging;
    public static ConfigEntry<float> LogIntervalSeconds;
    public static ConfigEntry<bool> ShowClockInRaid;

    /// <summary>Verbose trace, silent unless the user opts in via config.</summary>
    public static void Trace(string message)
    {
        if (EnableLogging != null && EnableLogging.Value)
            Log.LogInfo(message);
    }

    public void Awake()
    {
        Log = Logger;

        // Infinite raids are not configurable -- that is the whole point of the mod.

        ShowClockInRaid = Config.Bind("Raid", "ShowClockInRaid", true,
            "Show the current system time on the in-raid timer instead of a countdown. The " +
            "timer panel normally stays hidden until the last 10 minutes of a raid, so this " +
            "also forces it visible.");

        EnableLogging = Config.Bind("Logging", "EnableLogging", false,
            "Write detailed trace output to the BepInEx log. Off by default; turn on only when diagnosing.");
        LogIntervalSeconds = Config.Bind("Logging", "LogIntervalSeconds", 5f,
            new ConfigDescription(
                "Minimum seconds between repeats of per-frame trace lines. The map screen updates every " +
                "frame, so unthrottled logging would flood the log.",
                new AcceptableValueRange<float>(0.5f, 60f)));

        // PatchAll throws if any single patch fails to resolve its target, which aborts
        // Awake and silently leaves the whole plugin inert -- BepInEx logs nothing about
        // it. Wrapping it means a broken patch is reported instead of disappearing.
        try
        {
            var harmony = new Harmony("eu.thescrewcollab.raidsareinfinite.client");
            harmony.PatchAll();

            // Always list the patched methods, not just under EnableLogging: a patch that
            // fails to attach is the single most expensive failure mode this plugin has had,
            // and it is invisible without this.
            var patched = harmony.GetPatchedMethods().ToList();
            Logger.LogInfo($"RaidsAreInfinite.Client loaded. logging={EnableLogging.Value} " +
                           $"patches={patched.Count}");
            foreach (var m in patched)
                Logger.LogInfo($"  patched: {m.DeclaringType?.FullName}.{m.Name}");
        }
        catch (System.Exception e)
        {
            Logger.LogError($"RaidsAreInfinite.Client FAILED to patch: {e}");
        }
    }
}
