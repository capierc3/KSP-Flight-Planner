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

/// <summary>What a part does with electric charge (docs/MATH.md §7.8).</summary>
public enum PowerKind
{
    /// <summary>A solar panel: its rate is at Kerbin's distance from the Sun, facing it.</summary>
    Solar,
    /// <summary>Makes charge on its own (an RTG).</summary>
    Generator,
    /// <summary>Makes charge from propellant (a fuel cell).</summary>
    FuelCell,
    /// <summary>A probe core or pod keeping control.</summary>
    Command,
    /// <summary>A reaction wheel at full torque.</summary>
    ReactionWheel,
    /// <summary>An antenna while it transmits.</summary>
    Antenna,
    Light,
    /// <summary>The science lab while researching.</summary>
    Lab,
    /// <summary>A converter running on charge (ISRU); a part with several modes has one per mode.</summary>
    Converter,
    Drill,
}

/// <summary>Electric charge a part makes or uses, per second (one entry per module).</summary>
/// <param name="Tracking">Solar panels: whether it turns to follow the Sun.</param>
/// <param name="Inputs">Fuel cells: the propellant they burn, units per second.</param>
public sealed record PartPower(PowerKind Kind, double EcPerSecond, bool Tracking = true, IReadOnlyList<Propellant>? Inputs = null)
{
    public bool Makes => Kind is PowerKind.Solar or PowerKind.Generator or PowerKind.FuelCell;
}

/// <summary>A part definition from GameData, as KSP uses it after mods have patched it.</summary>
/// <param name="Name">KSP's internal name with '_' written as '.', as saves and craft files write it (microEngine.v2).</param>
/// <param name="FuelCrossfeed">Whether fuel passes through the part (false for decouplers unless crossfeed is switched on).</param>
/// <param name="TechRequired">The tech node that unlocks it (<see cref="TechNode.Id"/>).</param>
/// <param name="BulkheadProfiles">The sizes it attaches at, as KSP lists them: size0 … size4, srf (radial), mk2, mk3.</param>
/// <param name="CrashToleranceMs">Impact speed it survives (m/s).</param>
/// <param name="MaxTempK">Temperature it can take before it burns up (K).</param>
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
    bool FuelCrossfeed = true,
    string? TechRequired = null,
    string? Description = null,
    IReadOnlyList<string>? BulkheadProfiles = null,
    double CrashToleranceMs = 0,
    double MaxTempK = 0,
    IReadOnlyList<PartPower>? Power = null)
{
    /// <summary>What it makes and uses of electric charge (none for most parts).</summary>
    public IReadOnlyList<PartPower> PowerModules => Power ?? [];
}

/// <summary>Every part, resource and tech node of a KSP install. Built by <see cref="PartCatalogReader"/>, stored in the planner database.</summary>
public sealed class PartCatalog(IEnumerable<KspPart> parts, IEnumerable<KspResource> resources, IEnumerable<TechNode>? techTree = null)
{
    public static readonly PartCatalog Empty = new([], []);

    /// <summary>The R&amp;D tech tree, in the order GameData lists it.</summary>
    public IReadOnlyList<TechNode> TechTree { get; } = techTree?.ToList() ?? [];

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
    public const int FormatVersion = 5;

    /// <summary>The tech tree as ModuleManager writes it after patches (what the game loads unless a save points elsewhere).</summary>
    public const string ModuleManagerTechTree = "ModuleManager.TechTree";

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
        var techTreeFile = Path.Combine(gameData, ModuleManagerTechTree);
        if (File.Exists(cache)) return Parse(ReadShared(cache), File.Exists(techTreeFile) ? ReadShared(techTreeFile) : null);

        // No ModuleManager: every .cfg as written (nodes starting with @, + or ! are patches and are skipped).
        var parts = new List<KspPart>();
        var resources = new List<KspResource>();
        var trees = new List<ConfigNode>();
        foreach (var file in Directory.EnumerateFiles(gameData, "*.cfg", SearchOption.AllDirectories))
        {
            var root = ConfigNode.Parse(ReadShared(file));
            parts.AddRange(root.GetNodes("PART").Select(ReadPart).OfType<KspPart>());
            resources.AddRange(root.GetNodes("RESOURCE_DEFINITION").Select(ReadResource).OfType<KspResource>());
            trees.AddRange(root.GetNodes("TechTree"));
        }
        return new PartCatalog(parts, resources, ReadTechTree(trees));
    }

    /// <summary>
    /// Parses ModuleManager's cache: UrlConfig { parentUrl = …  PART { … } } blocks. The tech tree comes from
    /// <paramref name="techTree"/> (ModuleManager.TechTree) when given, else from the cache's own TechTree.
    /// </summary>
    public static PartCatalog Parse(string configCache, string? techTree = null)
    {
        var configs = ConfigNode.Parse(configCache).GetNodes("UrlConfig").SelectMany(u => u.Nodes).ToList();
        var trees = techTree is not null ? ConfigNode.Parse(techTree).GetNodes("TechTree").ToList() : configs.Where(n => n.Name == "TechTree").ToList();
        return new PartCatalog(
            configs.Where(n => n.Name == "PART").Select(ReadPart).OfType<KspPart>(),
            configs.Where(n => n.Name == "RESOURCE_DEFINITION").Select(ReadResource).OfType<KspResource>(),
            ReadTechTree(trees));
    }

    /// <summary>The first TechTree with nodes (mods that replace the tree ship their own).</summary>
    private static List<TechNode> ReadTechTree(IEnumerable<ConfigNode> trees) =>
        trees.Select(t => t.GetNodes("RDNode").Select(ReadTechNode).OfType<TechNode>().ToList()).FirstOrDefault(t => t.Count > 0) ?? [];

    private static TechNode? ReadTechNode(ConfigNode n)
    {
        if (n.GetValue("id") is not { Length: > 0 } id) return null;
        var pos = (n.GetValue("pos") ?? "").Split(',');
        return new TechNode(id, StripLocalization(n.GetValue("title")) ?? id, StripLocalization(n.GetValue("description")),
            Number(n.GetValue("cost")), Number(pos.ElementAtOrDefault(0)), Number(pos.ElementAtOrDefault(1)),
            n.GetNodes("Parent").Select(p => p.GetValue("parentID")).OfType<string>().Where(p => p.Length > 0).ToList(),
            bool.TryParse(n.GetValue("anyToUnlock"), out var any) && any);
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
        var power = modules.SelectMany(ReadPower).Where(x => x.EcPerSecond > 0).ToList();

        return new KspPart(PartCatalog.NormalizeName(name), StripLocalization(p.GetValue("title")) ?? name, p.GetValue("category"),
            StripLocalization(p.GetValue("manufacturer")), Number(p.GetValue("mass")), Number(p.GetValue("cost")),
            (int)Number(p.GetValue("CrewCapacity")), resources, engines, antennas,
            FuelCrossfeed: !bool.TryParse(p.GetValue("fuelCrossFeed"), out var crossfeed) || crossfeed,
            TechRequired: p.GetValue("TechRequired") is { Length: > 0 } tech ? tech.Trim() : null,
            Description: StripLocalization(p.GetValue("description")),
            BulkheadProfiles: (p.GetValue("bulkheadProfiles") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            CrashToleranceMs: Number(p.GetValue("crashTolerance")),
            MaxTempK: Number(p.GetValue("maxTemp")),
            Power: power);
    }

    private const string Charge = "ElectricCharge";

    /// <summary>Electric charge a module makes or uses, per second.</summary>
    private static IEnumerable<PartPower> ReadPower(ConfigNode m)
    {
        // The rate a RESOURCE / INPUT_RESOURCE / OUTPUT_RESOURCE node gives for electric charge.
        static double Rate(IEnumerable<ConfigNode> nodes) => nodes
            .Where(n => (n.GetValue("name") ?? n.GetValue("ResourceName")) == Charge)
            .Sum(n => Number(n.GetValue("rate") ?? n.GetValue("Ratio")));
        static List<Propellant> Inputs(ConfigNode m) => m.GetNodes("INPUT_RESOURCE")
            .Select(n => new Propellant(n.GetValue("ResourceName") ?? n.GetValue("name") ?? "", Number(n.GetValue("Ratio") ?? n.GetValue("rate"))))
            .Where(i => i.Name.Length > 0 && i.Name != Charge).ToList();

        switch (m.GetValue("name"))
        {
            case "ModuleDeployableSolarPanel" when (m.GetValue("resourceName") ?? Charge) == Charge:
                yield return new PartPower(PowerKind.Solar, Number(m.GetValue("chargeRate")),
                    !string.Equals(m.GetValue("isTracking"), "false", StringComparison.OrdinalIgnoreCase));
                break;
            case "ModuleGenerator":
            {
                var inputs = Inputs(m);
                yield return new PartPower(inputs.Count > 0 ? PowerKind.FuelCell : PowerKind.Generator, Rate(m.GetNodes("OUTPUT_RESOURCE")),
                    Inputs: inputs.Count > 0 ? inputs : null);
                break;
            }
            case "ModuleResourceConverter":
                if (Rate(m.GetNodes("OUTPUT_RESOURCE")) is var made and > 0)
                    yield return new PartPower(PowerKind.FuelCell, made, Inputs: Inputs(m));
                else
                    yield return new PartPower(PowerKind.Converter, Rate(m.GetNodes("INPUT_RESOURCE")));
                break;
            case "ModuleResourceHarvester":
                yield return new PartPower(PowerKind.Drill, Rate(m.GetNodes("INPUT_RESOURCE")));
                break;
            case "ModuleCommand":
                yield return new PartPower(PowerKind.Command, Rate(m.GetNodes("RESOURCE")));
                break;
            case "ModuleReactionWheel":
                yield return new PartPower(PowerKind.ReactionWheel, Rate(m.GetNodes("RESOURCE")));
                break;
            case "ModuleDataTransmitter" when (m.GetValue("requiredResource") ?? Charge) == Charge && Number(m.GetValue("packetInterval")) > 0:
                // Charge per packet over seconds per packet.
                yield return new PartPower(PowerKind.Antenna, Number(m.GetValue("packetResourceCost")) / Number(m.GetValue("packetInterval")));
                break;
            case "ModuleLight" when !string.Equals(m.GetValue("useResources"), "false", StringComparison.OrdinalIgnoreCase):
                yield return new PartPower(PowerKind.Light, Number(m.GetValue("resourceAmount")));
                break;
            case "ModuleScienceConverter":
                yield return new PartPower(PowerKind.Lab, Number(m.GetValue("powerRequirement")));
                break;
        }
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
