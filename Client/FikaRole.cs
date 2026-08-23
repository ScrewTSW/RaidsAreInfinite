using HarmonyLib;

namespace RaidsAreInfinite.Client;

/// <summary>
/// Host/client detection, resolved by reflection so the plugin still loads without Fika.
///
/// Fika already synchronises the raid clock: the host puts its live GameDateTime into every
/// InformationPacket, and FikaClient hands it to BaseGameController.SetClientTime, which
/// replaces both CoopGame.GameDateTime and GameWorld.GameDateTime on the client.
///
/// That makes the host authoritative for free -- but only if the client does not also seed
/// its own clock. CoopGame.Create runs on clients too, so seeding there would set the raid to
/// the CLIENT's system time and then race Fika's incoming sync, leaving every machine on a
/// slightly different clock. The time patches are therefore host-only, and clients simply
/// accept what the host sends.
/// </summary>
public static class FikaRole
{
    /// <summary>
    /// True when this machine is the raid host, or when Fika is not installed at all
    /// (singleplayer, where the local player is trivially authoritative).
    /// </summary>
    public static bool IsAuthoritative
    {
        get
        {
            var t = AccessTools.TypeByName("Fika.Core.Main.Utils.FikaBackendUtils");
            if (t == null) return true; // no Fika -> singleplayer

            var prop = AccessTools.Property(t, "IsServer");
            if (prop == null)
            {
                // Fika present but the property moved. Assume authoritative rather than
                // silently disabling the clock, and say so once at load.
                Plugin.Log.LogError("FikaRole: FikaBackendUtils.IsServer did not resolve, " +
                                    "treating this client as authoritative.");
                return true;
            }

            return (bool)prop.GetValue(null);
        }
    }

    /// <summary>Human-readable role for trace lines.</summary>
    public static string Describe()
    {
        var t = AccessTools.TypeByName("Fika.Core.Main.Utils.FikaBackendUtils");
        if (t == null) return "NO-FIKA";
        return IsAuthoritative ? "HOST" : "CLIENT";
    }
}
