using System.Globalization;

namespace KspFlightPlanner.Core.Ksp;

/// <summary>A resource such as LiquidFuel: how heavy one unit is and how it flows between parts.</summary>
/// <param name="FlowMode">KSP's flow rule: NO_FLOW (stays in its part, e.g. SolidFuel), STACK_PRIORITY_SEARCH (through
/// parts that allow crossfeed, e.g. LiquidFuel), ALL_VESSEL / STAGE_PRIORITY_FLOW (anywhere on the vessel, e.g. MonoPropellant).</param>
public sealed record KspResource(string Name, string DisplayName, double DensityT, double UnitCost, string FlowMode = "STACK_PRIORITY_SEARCH")
{
    public ResourceFlow Flow => FlowMode switch
    {
        "NO_FLOW" => ResourceFlow.OwnPart,
        "ALL_VESSEL" or "ALL_VESSEL_BALANCE" or "STAGE_PRIORITY_FLOW" or "STAGE_PRIORITY_FLOW_BALANCE" => ResourceFlow.WholeVessel,
        _ => ResourceFlow.Crossfeed,
    };
}

/// <summary>Which tanks an engine can draw a resource from.</summary>
public enum ResourceFlow
{
    /// <summary>Only the engine's own part (solid fuel).</summary>
    OwnPart,
    /// <summary>Parts connected through crossfeed (stack, radial, open decouplers) plus fuel lines (liquid fuel, oxidizer).</summary>
    Crossfeed,
    /// <summary>Any part still attached (monopropellant, xenon, electric charge).</summary>
    WholeVessel,
}

/// <summary>A resource a part carries when built (a full tank).</summary>
public sealed record PartResource(string Name, double Amount, double MaxAmount);

/// <summary>A propellant an engine burns, in volume ratio to its other propellants (LiquidFuel 0.9 : Oxidizer 1.1).</summary>
public sealed record Propellant(string Name, double Ratio);

/// <summary>One engine module on a part (multi-mode engines have one per mode).</summary>
public sealed record PartEngine(string? EngineId, double MaxThrustKn, double IspVacuumS, double IspSeaLevelS, IReadOnlyList<Propellant> Propellants)
{
    /// <summary>Jets need air: they produce no thrust in a vacuum.</summary>
    public bool BreathesAir => Propellants.Any(p => p.Name == "IntakeAir");
}

/// <summary>An antenna: DIRECT/RELAY/INTERNAL, its power (range rating, metres) and whether several combine.</summary>
public sealed record PartAntenna(string Type, double Power, bool Combinable, double CombinableExponent);

/// <summary>A part definition from GameData, as KSP uses it after mods have patched it.</summary>
/// <param name="Name">KSP's internal name with '_' written as '.', as saves and craft files write it (microEngine.v2).</param>
/// <param name="FuelCrossfeed">Whether fuel passes through the part (false for decouplers unless crossfeed is switched on).</param>
public sealed record KspPart(
    string Name,
    string Title,
    string? Category,
    string? Manufacturer,
    double MassT,
    double Cost,
    int CrewCapacity,
    IReadOnlyList<PartResource> Resources,
    IReadOnlyList<PartEngine> Engines,
    IReadOnlyList<PartAntenna> Antennas,
    bool FuelCrossfeed = true);

/// <summary>Every part and resource of a KSP install. Built by <see cref="PartCatalogReader"/>, stored in the planner database.</summary>
public sealed class PartCatalog(IEnumerable<KspPart> parts, IEnumerable<KspResource> resources)
{
    public static readonly PartCatalog Empty = new([], []);

    public IReadOnlyDictionary<string, KspPart> Parts { get; } =
        parts.GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.Last());

    public IReadOnlyDictionary<string, KspResource> Resources { get; } =
        resources.GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.Last());

    public bool IsEmpty => Parts.Count == 0;

    public KspPart? Part(string name) => Parts.GetValueOrDefault(NormalizeName(name));

    /// <summary>Mass of one unit in tonnes; stock values when the catalog doesn't know the resource.</summary>
    public double Density(string resource) =>
        Resources.TryGetValue(resource, out var r) ? r.DensityT : StockDensities.GetValueOrDefault(resource);

    public string DisplayName(string resource) => Resources.TryGetValue(resource, out var r) ? r.DisplayName : resource;

    /// <summary>How a resource flows; stock rules when the catalog doesn't know it.</summary>
    public ResourceFlow Flow(string resource) =>
        Resources.TryGetValue(resource, out var r) ? r.Flow : resource switch
        {
            "SolidFuel" => ResourceFlow.OwnPart,
            "LiquidFuel" or "Oxidizer" => ResourceFlow.Crossfeed,
            _ => ResourceFlow.WholeVessel,
        };

    /// <summary>KSP turns '_' in a part's config name into '.' (microEngine_v2 → microEngine.v2).</summary>
    public static string NormalizeName(string name) => name.Trim().Replace('_', '.');

    private static readonly Dictionary<string, double> StockDensities = new()
    {
        ["LiquidFuel"] = 0.005, ["Oxidizer"] = 0.005, ["MonoPropellant"] = 0.004, ["SolidFuel"] = 0.0075,
        ["XenonGas"] = 0.0001, ["Ore"] = 0.01, ["Ablator"] = 0.001, ["ElectricCharge"] = 0, ["IntakeAir"] = 0,
    };
}

/// <summary>
/// Reads part and resource definitions from a KSP install. Prefers ModuleManager's cache (GameData/ModuleManager.ConfigCache),
/// which holds every config after mods have patched it; without it, reads the raw .cfg files (patches not applied).
/// </summary>
public static class PartCatalogReader
{
    public const string ModuleManagerCache = "ModuleManager.ConfigCache";

    /// <summary>Bumped when the reader starts collecting new fields, so stored catalogs are read again.</summary>
    public const int FormatVersion = 2;

    /// <summary>The file or folder the catalog is read from, and when it last changed (to know when to read again).</summary>
    public static (string Path, DateTime LastWriteUtc)? Source(string installPath)
    {
        var gameData = Path.Combine(installPath, "GameData");
        var cache = Path.Combine(gameData, ModuleManagerCache);
        if (File.Exists(cache)) return (cache, File.GetLastWriteTimeUtc(cache));
        if (!Directory.Exists(gameData)) return null;
        var newest = Directory.EnumerateFiles(gameData, "*.cfg", SearchOption.AllDirectories).Select(File.GetLastWriteTimeUtc)
            .DefaultIfEmpty(DateTime.MinValue).Max();
        return (gameData, newest);
    }

    public static PartCatalog Read(string installPath)
    {
        var gameData = Path.Combine(installPath, "GameData");
        var cache = Path.Combine(gameData, ModuleManagerCache);
        if (File.Exists(cache)) return Parse(ReadShared(cache));

        // No ModuleManager: every .cfg as written (nodes starting with @, + or ! are patches and are skipped).
        var parts = new List<KspPart>();
        var resources = new List<KspResource>();
        foreach (var file in Directory.EnumerateFiles(gameData, "*.cfg", SearchOption.AllDirectories))
        {
            var root = ConfigNode.Parse(ReadShared(file));
            parts.AddRange(root.GetNodes("PART").Select(ReadPart).OfType<KspPart>());
            resources.AddRange(root.GetNodes("RESOURCE_DEFINITION").Select(ReadResource).OfType<KspResource>());
        }
        return new PartCatalog(parts, resources);
    }

    /// <summary>Parses ModuleManager's cache: UrlConfig { parentUrl = …  PART { … } } blocks.</summary>
    public static PartCatalog Parse(string configCache)
    {
        var configs = ConfigNode.Parse(configCache).GetNodes("UrlConfig").SelectMany(u => u.Nodes).ToList();
        return new PartCatalog(
            configs.Where(n => n.Name == "PART").Select(ReadPart).OfType<KspPart>(),
            configs.Where(n => n.Name == "RESOURCE_DEFINITION").Select(ReadResource).OfType<KspResource>());
    }

    private static KspPart? ReadPart(ConfigNode p)
    {
        if (p.GetValue("name") is not { Length: > 0 } name) return null;
        var modules = p.GetNodes("MODULE").ToList();
        var engines = modules
            .Where(m => m.GetValue("name") is "ModuleEngines" or "ModuleEnginesFX" or "ModuleEnginesRF")
            .Select(m =>
            {
                var curve = (m.GetNode("atmosphereCurve")?.Values ?? []).Where(v => v.Key == "key")
                    .Select(v => v.Value.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
                    .Where(k => k.Length >= 2)
                    .Select(k => (Pressure: Number(k[0]), Isp: Number(k[1]))).ToList();
                double IspAt(double pressure) => curve.FirstOrDefault(k => k.Pressure == pressure).Isp;
                return new PartEngine(m.GetValue("engineID"), Number(m.GetValue("maxThrust")), IspAt(0), IspAt(1),
                    m.GetNodes("PROPELLANT").Select(pr => new Propellant(pr.GetValue("name") ?? "", Number(pr.GetValue("ratio"))))
                        .Where(pr => pr.Name.Length > 0).ToList());
            }).ToList();
        var antennas = modules
            .Where(m => m.GetValue("name") == "ModuleDataTransmitter")
            .Select(m => new PartAntenna(m.GetValue("antennaType") ?? "DIRECT", Number(m.GetValue("antennaPower")),
                bool.TryParse(m.GetValue("antennaCombinable"), out var c) && c,
                m.GetValue("antennaCombinableExponent") is { } e ? Number(e) : 0.75))
            .ToList();
        var resources = p.GetNodes("RESOURCE")
            .Select(r => new PartResource(r.GetValue("name") ?? "", Number(r.GetValue("amount")), Number(r.GetValue("maxAmount"))))
            .Where(r => r.Name.Length > 0).ToList();

        return new KspPart(PartCatalog.NormalizeName(name), StripLocalization(p.GetValue("title")) ?? name, p.GetValue("category"),
            StripLocalization(p.GetValue("manufacturer")), Number(p.GetValue("mass")), Number(p.GetValue("cost")),
            (int)Number(p.GetValue("CrewCapacity")), resources, engines, antennas,
            FuelCrossfeed: !bool.TryParse(p.GetValue("fuelCrossFeed"), out var crossfeed) || crossfeed);
    }

    private static KspResource? ReadResource(ConfigNode r) => r.GetValue("name") is { Length: > 0 } name
        ? new KspResource(name, StripLocalization(r.GetValue("displayName")) ?? name, Number(r.GetValue("density")), Number(r.GetValue("unitCost")),
            r.GetValue("flowMode") ?? "STACK_PRIORITY_SEARCH")
        : null;

    /// <summary>
    /// ModuleManager's cache has English titles; raw configs have localization keys ("#autoLOC_500427"), which are
    /// dropped so the caller falls back to the part's name.
    /// </summary>
    private static string? StripLocalization(string? text) =>
        string.IsNullOrWhiteSpace(text) || text.StartsWith("#autoLOC", StringComparison.Ordinal) ? null : text.Trim();

    private static double Number(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
