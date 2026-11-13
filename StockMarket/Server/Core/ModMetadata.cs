using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Spt.Mod;

namespace TarkFX.StockMarket.Server.Core;

[Injectable]
public sealed record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.hiddencirno.tarkfx.stockmarket";
    public string Name { get; init; } = "TarkFX Stock Market";
    public string Author { get; init; } = "HiddenCirno";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("0.1.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
    public bool HasPrepatcher { get; init; }
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; } = "https://github.com/HiddenCirno/TarkFX";
    public string License { get; init; } = "AGPL-3.0";
}
