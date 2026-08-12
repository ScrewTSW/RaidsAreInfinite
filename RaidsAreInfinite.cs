using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace RaidsAreInfinite;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "eu.thescrewcollab.raidsareinfinite";
    public string Name { get; init; } = "RaidsAreInfinite";
    public string Author { get; init; } = "ScrewTSW";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("1.0.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
    public bool HasPrepatcher { get; init; } = false;
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "MIT";
}

[Injectable(TypePriority = OnLoadOrder.PostLoad + 1)]
public class RaidsAreInfinite(
    LocationTable locationTable,
    ISptLogger<RaidsAreInfinite> logger) : IOnLoad
{
    private const int TimeLimit = 99999;

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var patched = 0;

        foreach (var (name, location) in locationTable.GetDictionary())
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
