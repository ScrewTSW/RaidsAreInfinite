using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Server;

namespace RaidsAreInfinite;

public record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "eu.thescrewcollab.raidsareinfinite";
    public override string Name { get; init; } = "RaidsAreInfinite";
    public override string Author { get; init; } = "ScrewTSW";
    public override List<string>? Contributors { get; init; }
    public override SemanticVersioning.Version Version { get; init; } = new("1.0.0");
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.0");
    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public override bool? IsBundleMod { get; init; } = false;
    public override string? Url { get; init; }
    public override string License { get; init; } = "MIT";
}

[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 1)]
public class RaidsAreInfinite(
    DatabaseTables databaseTables,
    ISptLogger<RaidsAreInfinite> logger) : IOnLoad
{
    private const int TimeLimit = 99999;

    public Task OnLoad()
    {
        var patched = 0;

        foreach (var (name, location) in databaseTables.Locations.GetDictionary())
        {
            if (location?.Base == null) continue;

            var current = location.Base.EscapeTimeLimit;
            if (current is null or 0 or 99999) continue;

            location.Base.EscapeTimeLimit = TimeLimit;
            location.Base.EscapeTimeLimitCoop = TimeLimit;
            location.Base.EscapeTimeLimitPVE = TimeLimit;
            patched++;

            logger.Info($"  {name}: {current} -> {TimeLimit}");
        }

        logger.Success($"RaidsAreInfinite: patched {patched} locations");
        return Task.CompletedTask;
    }
}
