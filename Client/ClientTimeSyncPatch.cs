using System;
using System.Reflection;
using EFT;
using HarmonyLib;

namespace RaidsAreInfinite.Client;

/// <summary>
/// Bind the client's SKY to the host's synced clock, and log the sync.
///
/// Fika already makes the host authoritative for the clock itself: the host puts its live
/// GameDateTime into every InformationPacket and BaseGameController.SetClientTime installs it,
/// replacing CoopGame.GameDateTime and GameWorld.GameDateTime. Delivery was never the problem.
///
/// Lighting is the problem. TOD_Time.Update recomputes TODSkyProvider.Instance.Cycle.DateTime
/// every frame from TOD_Time's OWN GameDateTime field, which Fika does not replace. So a client
/// ends up with a corrected clock and a stale sky -- observed as host 16:20:37 against a client
/// sky of 07:31:11, i.e. standing in darkness inside a daytime raid.
///
/// Assigning the REFERENCE is what makes this hold. Reset()-ing the sky's clock to the right
/// value only survives until the next frame, because Update recomputes from whatever instance
/// the field points at; repointing the field makes the sky track the synced clock permanently.
/// </summary>
[HarmonyPatch]
public static class ClientTimeSyncPatch
{
    private static float _lastTrace;

    static bool Prepare()
    {
        var t = AccessTools.TypeByName("Fika.Core.Main.GameMode.BaseGameController");
        if (t == null)
        {
            Plugin.Log.LogInfo("ClientTimeSyncPatch: Fika not present, skipping.");
            return false;
        }
        return TargetMethod() != null;
    }

    static MethodBase TargetMethod()
    {
        var t = AccessTools.TypeByName("Fika.Core.Main.GameMode.BaseGameController");
        return t == null ? null : AccessTools.Method(t, "SetClientTime");
    }

    [HarmonyPostfix]
    public static void Postfix(GameDateTime gameDateTime)
    {
        try
        {
            if (gameDateTime == null) return;

            DateTime fromHost = gameDateTime.Calculate();

            // Point the SKY at the same GameDateTime object Fika just installed.
            //
            // TOD_Time.Update drives TODSkyProvider.Instance.Cycle.DateTime every frame from
            // its OWN GameDateTime field (via CalculateTaxonomyDate), and Fika's SetClientTime
            // only replaces CoopGame.GameDateTime and GameWorld.GameDateTime. So the client's
            // clock gets corrected to the host's time while the sky keeps recomputing from the
            // instance it was seeded with at raid creation -- host at 16:20:37, client sky
            // stuck at 07:31:11.
            //
            // Assigning the REFERENCE rather than Reset()-ing a value is what makes this stick:
            // the sky then recomputes from the synced clock every frame, including any later
            // correction, instead of being nudged once and drifting again.
            bool skyBound = false;
            if (TODSkyProvider.IsAvailable)
            {
                var todTime = TODSkyProvider.Instance?.CurrentTime;
                if (todTime != null && !ReferenceEquals(todTime.GameDateTime, gameDateTime))
                {
                    todTime.GameDateTime = gameDateTime;
                    skyBound = true;
                }
            }

            if (!Plugin.EnableLogging.Value) return;
            if (!skyBound && UnityEngine.Time.unscaledTime - _lastTrace < Plugin.LogIntervalSeconds.Value) return;
            _lastTrace = UnityEngine.Time.unscaledTime;

            // Report the sky's own clock alongside the synced one. Lighting runs off a third
            // reference -- TODSkyProvider.Instance.CurrentTime.GameDateTime -- which Fika never
            // touches, so it only matches when the client seeded from the host's HourOfDay in
            // the first place. A mismatch here means a client is standing in the wrong lighting
            // for the raid, which is exactly the darkness-in-a-daytime-raid bug.
            string sky = "n/a";
            if (TODSkyProvider.IsAvailable && TODSkyProvider.Instance?.CurrentTime?.GameDateTime != null)
                sky = TODSkyProvider.Instance.CurrentTime.GameDateTime.Calculate().ToString("HH:mm:ss");

            Plugin.Trace($"time-sync: [{FikaRole.Describe()}] host raid clock {fromHost:HH:mm:ss} " +
                         $"sky={sky}{(skyBound ? " (BOUND to synced clock)" : "")} " +
                         $"(local system {DateTime.Now:HH:mm:ss}) " +
                         $"timeFactor={gameDateTime.TimeFactor}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"ClientTimeSyncPatch: {e}");
        }
    }
}
