using System.Globalization;
using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core.Ksp;

/// <summary>A vessel's orbit exactly as KSP stores it (ORBIT node). Angles in degrees except the mean anomaly.</summary>
public sealed record SavedOrbit(
    double SemiMajorAxisM,
    double Eccentricity,
    double InclinationDeg,
    double LanDeg,
    double ArgumentOfPeriapsisDeg,
    double MeanAnomalyRad,
    double EpochUt,
    int ReferenceBodyIndex,
    string? BodyIdent)
{
    /// <summary>
    /// The body's name: from IDENT ("Squad/Kerbin" → "Kerbin"; KSP calls Kerbol "Sun"), or from the stock body
    /// index when the save has no IDENT.
    /// </summary>
    public string? BodyName
    {
        get
        {
            var name = BodyIdent is { Length: > 0 } ident ? ident[(ident.LastIndexOf('/') + 1)..]
                : ReferenceBodyIndex >= 0 && ReferenceBodyIndex < StockBodyOrder.Length ? StockBodyOrder[ReferenceBodyIndex] : null;
            return name == "Sun" ? "Kerbol" : name;
        }
    }

    /// <summary>The same orbit as the planner's elements (epoch = EPH).</summary>
    public OrbitalElements ToElements() =>
        new(SemiMajorAxisM, Eccentricity, InclinationDeg, LanDeg, ArgumentOfPeriapsisDeg, MeanAnomalyRad, EpochUt);

    /// <summary>A closed orbit the planner can fly from or to (escaping and NaN orbits aren't).</summary>
    public bool IsClosed => double.IsFinite(SemiMajorAxisM) && SemiMajorAxisM > 0 && Eccentricity is >= 0 and < 1;

    /// <summary>A path the planner can follow: a closed orbit, or a hyperbola (escaping, or passing a moon).</summary>
    public bool IsPath => IsClosed || double.IsFinite(SemiMajorAxisM) && SemiMajorAxisM < 0 && Eccentricity > 1 && double.IsFinite(Eccentricity);

    /// <summary>KSP's flightGlobalsIndex for the stock system (the REF value).</summary>
    public static readonly string[] StockBodyOrder =
        ["Kerbol", "Kerbin", "Mun", "Minmus", "Moho", "Eve", "Duna", "Ike", "Jool", "Laythe", "Vall", "Bop", "Tylo", "Gilly", "Pol", "Dres", "Eeloo"];
}

/// <summary>One VESSEL from a save: a ship, station, probe, debris, asteroid, flag…</summary>
/// <param name="Id">KSP's vessel GUID (pid); stays the same for the vessel's whole life.</param>
/// <param name="Type">Ship, Station, Probe, Lander, Relay, Debris, SpaceObject, Flag…</param>
/// <param name="Situation">ORBITING, LANDED, SPLASHED, PRELAUNCH, FLYING, SUB_ORBITAL, ESCAPING, DOCKED.</param>
public sealed record SavedVessel(string Id, string Name, string Type, string Situation, SavedOrbit? Orbit)
{
    public bool IsOrbiting => Situation == "ORBITING";

    /// <summary>Every part as it is now: what's left in its tanks, engine settings, who is aboard.</summary>
    public IReadOnlyList<SavedPart> Parts { get; init; } = [];

    /// <summary>Game time of launch (lct).</summary>
    public double LaunchUt { get; init; }

    /// <summary>Mission elapsed time in seconds (met).</summary>
    public double MissionTimeS { get; init; }

    /// <summary>Where a landed or splashed vessel stands (lat, lon in degrees; alt = height above the datum). NaN in flight.</summary>
    public double LatitudeDeg { get; init; } = double.NaN;
    public double LongitudeDeg { get; init; } = double.NaN;
    public double AltitudeM { get; init; } = double.NaN;

    public IEnumerable<string> Crew => Parts.SelectMany(p => p.Crew);

    /// <summary>
    /// The names of the vessels docked together into this one, as its docking ports remember them: each port keeps the name
    /// of the vessel it came with, and KSP gives that name back on undocking. Includes this vessel's own name from before
    /// it docked. Empty when nothing is docked.
    /// </summary>
    public IReadOnlySet<string> DockedNames { get; init; } = new HashSet<string>();

    /// <summary>Which of <see cref="Parts"/> is the root (VESSEL root); the others hang from it by <see cref="SavedPart.ParentIndex"/>.</summary>
    public int RootIndex { get; init; }

    /// <summary>
    /// For a craft split off a docked stack (<see cref="DockedCraft"/>): the save id of the stack it's docked in. Null for a
    /// vessel as the save has it.
    /// </summary>
    public string? DockedInId { get; init; }
}

/// <summary>
/// A vessel as its docking port remembers it from before it docked (DOCKEDVESSEL): KSP gives the name and type back on
/// undocking. <paramref name="RootUid"/> is that vessel's root part.
/// </summary>
public sealed record DockedVesselInfo(string Name, string Type, string RootUid);

/// <summary>A docking port on a part: its state ("Ready", "Docked (docker)", "Docked (dockee)"…), the port it's docked to, and the vessel it came with.</summary>
public sealed record SavedDockingPort(string State, string DockedToUid, DockedVesselInfo? CameWith)
{
    public bool IsDocked => State.StartsWith("Docked", StringComparison.Ordinal) && DockedToUid is { Length: > 0 } and not "0";
}

/// <summary>A part of a vessel in the save. <see cref="Name"/> matches <see cref="KspPart.Name"/> in the part catalog.</summary>
/// <param name="MassT">The part's dry mass in tonnes, as the save records it.</param>
public sealed record SavedPart(string Name, double MassT, IReadOnlyList<PartResource> Resources, IReadOnlyList<SavedEngineState> Engines,
    IReadOnlyList<string> Crew)
{
    /// <summary>Multi-mode engines (e.g. RAPIER): whether the first mode is running; null for other parts.</summary>
    public bool? RunningPrimaryMode { get; init; }

    /// <summary>KSP's id for the part (uid), unique in the save.</summary>
    public string Uid { get; init; } = "";

    /// <summary>The part it's attached to (an index into the vessel's parts); the root's is its own index.</summary>
    public int ParentIndex { get; init; }

    /// <summary>A command part: a crew pod or probe core (ModuleCommand).</summary>
    public bool IsCommand { get; init; }

    /// <summary>The part's docking ports (usually none, or one).</summary>
    public IReadOnlyList<SavedDockingPort> DockingPorts { get; init; } = [];
}

/// <summary>An engine module's settings in the save, in the same order as the part's engines in the catalog.</summary>
/// <param name="ThrustPercent">The thrust limiter (0–100).</param>
public sealed record SavedEngineState(double ThrustPercent, bool IsIgnited, bool IsShutdown, bool IsFlameout);

/// <summary>What the planner reads from a save file (persistent.sfs).</summary>
/// <param name="Ut">Game time when the file was written, in seconds since Year 1, Day 1.</param>
public sealed record KspSave(string Title, string Mode, double Ut, IReadOnlyList<SavedVessel> Vessels)
{
    /// <summary>Waypoints the player placed (KerbNet or the map), from the ScenarioCustomWaypoints scenario.</summary>
    public IReadOnlyList<SavedWaypoint> Waypoints { get; init; } = [];

    /// <summary>What's been researched; null in sandbox, where every part can be used.</summary>
    public SavedResearch? Research { get; init; }

    /// <summary>The game's CommNet settings (difficulty options); stock defaults when the save doesn't say.</summary>
    public SavedCommNet CommNet { get; init; } = SavedCommNet.Default;
}

/// <summary>
/// CommNet as the save has it: the tracking station's level and the difficulty modifiers on antenna range and on the
/// tracking station's power (docs/MATH.md §7.7).
/// </summary>
/// <param name="TrackingStationLevel">1 to 3. Career saves record it; sandbox and science saves have every building upgraded.</param>
public sealed record SavedCommNet(bool Enabled, int TrackingStationLevel, double RangeModifier, double DsnModifier)
{
    public static readonly SavedCommNet Default = new(true, 3, 1, 1);
}

/// <summary>A custom waypoint: a named spot on a body (degrees, north and east positive).</summary>
public sealed record SavedWaypoint(string Name, string BodyName, double LatitudeDeg, double LongitudeDeg);

/// <summary>A save folder under the KSP install (saves/&lt;name&gt;/persistent.sfs).</summary>
public sealed record KspSaveFolder(string Name, string Title, string PersistentPath, DateTime LastWriteTime)
{
    public override string ToString() => Title == Name ? Name : $"{Name} — {Title}";
}

public static class KspSaveReader
{
    public const string PersistentFileName = "persistent.sfs";

    /// <summary>Built-in folders under saves/ that aren't the player's games.</summary>
    private static readonly string[] NotGames = ["scenarios", "training"];

    /// <summary>The KSP folder is right if it has a saves folder; returns why not, or null.</summary>
    public static string? CheckInstallFolder(string? installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath)) return "Choose the folder Kerbal Space Program is installed in.";
        if (!Directory.Exists(installPath)) return "That folder doesn't exist.";
        if (!Directory.Exists(Path.Combine(installPath, "saves")))
            return "No \"saves\" folder here — choose the folder that contains KSP_x64.exe.";
        return null;
    }

    /// <summary>The player's saves (folders with a persistent.sfs), most recently played first.</summary>
    public static IReadOnlyList<KspSaveFolder> FindSaves(string installPath)
    {
        var savesDir = Path.Combine(installPath, "saves");
        if (!Directory.Exists(savesDir)) return [];
        var list = new List<KspSaveFolder>();
        foreach (var dir in Directory.EnumerateDirectories(savesDir))
        {
            var name = Path.GetFileName(dir);
            var file = Path.Combine(dir, PersistentFileName);
            if (NotGames.Contains(name, StringComparer.OrdinalIgnoreCase) || !File.Exists(file)) continue;
            list.Add(new KspSaveFolder(name, ReadTitle(file) ?? name, file, File.GetLastWriteTime(file)));
        }
        return list.OrderByDescending(s => s.LastWriteTime).ToList();
    }

    public static string PersistentPath(string installPath, string saveName) =>
        Path.Combine(installPath, "saves", saveName, PersistentFileName);

    /// <summary>
    /// Reads a save, sharing the file with KSP (which may be writing it). Retries briefly while it's locked;
    /// throws IOException if it stays locked.
    /// </summary>
    public static KspSave Read(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return Parse(reader.ReadToEnd());
            }
            catch (IOException) when (attempt < 5 && File.Exists(path))
            {
                Thread.Sleep(300);
            }
        }
    }

    public static KspSave Parse(string text)
    {
        var game = ConfigNode.Parse(text).GetNode("GAME") ?? throw new FormatException("Not a KSP save: no GAME section.");
        var flight = game.GetNode("FLIGHTSTATE");
        var title = game.GetValue("Title") ?? "";
        var vessels = flight?.GetNodes("VESSEL").Select(ReadVessel).ToList() ?? [];
        var waypoints = game.GetNodes("SCENARIO").Where(n => n.GetValue("name") == "ScenarioCustomWaypoints")
            .SelectMany(n => n.GetNodes("WAYPOINT"))
            .Select(w => new SavedWaypoint(w.GetValue("name") ?? "Waypoint", w.GetValue("celestialName") ?? "",
                Number(w.GetValue("latitude")), Number(w.GetValue("longitude"))))
            .Where(w => w.BodyName.Length > 0 && double.IsFinite(w.LatitudeDeg) && double.IsFinite(w.LongitudeDeg))
            .ToList();
        var mode = game.GetValue("Mode") ?? "";
        var research = mode == "SANDBOX" ? null
            : game.GetNodes("SCENARIO").FirstOrDefault(n => n.GetValue("name") == "ResearchAndDevelopment") is { } rd ? ReadResearch(rd) : null;
        return new KspSave(title, mode, Number(flight?.GetValue("UT")), vessels)
        {
            Waypoints = waypoints, Research = research, CommNet = ReadCommNet(game),
        };
    }

    private static SavedCommNet ReadCommNet(ConfigNode game)
    {
        var parameters = game.GetNode("PARAMETERS");
        var settings = parameters?.GetNode("CommNetParams");
        // The facility's level is stored from 0 to 1 (level 1, 2, 3 → 0, 0.5, 1); only career saves have it.
        var station = game.GetNodes("SCENARIO").FirstOrDefault(n => n.GetValue("name") == "ScenarioUpgradeableFacilities")
            ?.GetNode("SpaceCenter/TrackingStation")?.GetValue("lvl");
        var level = station is null ? 3 : Math.Clamp((int)Math.Round(Number(station) * 2) + 1, 1, 3);
        return new SavedCommNet(
            !string.Equals(parameters?.GetNode("DIFFICULTY")?.GetValue("EnableCommNet"), "False", StringComparison.OrdinalIgnoreCase),
            level,
            settings?.GetValue("rangeModifier") is { } range ? Number(range) : 1,
            settings?.GetValue("DSNModifier") is { } dsn ? Number(dsn) : 1);
    }

    /// <summary>Researched tech nodes (state Available) and the parts each lists as usable.</summary>
    private static SavedResearch ReadResearch(ConfigNode rd)
    {
        var researched = rd.GetNodes("Tech").Where(t => t.GetValue("state") == "Available").ToList();
        return new SavedResearch(Number(rd.GetValue("sci")),
            researched.Select(t => t.GetValue("id")).OfType<string>().ToHashSet(),
            researched.SelectMany(t => t.Values.Where(v => v.Key == "part").Select(v => PartCatalog.NormalizeName(v.Value))).ToHashSet());
    }

    private static SavedVessel ReadVessel(ConfigNode v)
    {
        var o = v.GetNode("ORBIT");
        var orbit = o is null ? null : new SavedOrbit(
            Number(o.GetValue("SMA")), Number(o.GetValue("ECC")), Number(o.GetValue("INC")), Number(o.GetValue("LAN")),
            Number(o.GetValue("LPE")), Number(o.GetValue("MNA")), Number(o.GetValue("EPH")),
            int.TryParse(o.GetValue("REF"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ? index : -1,
            o.GetValue("IDENT"));
        return new SavedVessel(v.GetValue("pid") ?? v.GetValue("persistentId") ?? "", v.GetValue("name") ?? "(unnamed)",
            v.GetValue("type") ?? "", v.GetValue("sit") ?? "", orbit)
        {
            Parts = v.GetNodes("PART").Select(ReadPart).ToList(),
            DockedNames = v.GetNodes("PART").SelectMany(p => p.GetNodes("MODULE"))
                .Where(m => m.GetValue("name") == "ModuleDockingNode" && (m.GetValue("state") ?? "").StartsWith("Docked", StringComparison.Ordinal))
                .Select(m => m.GetNode("DOCKEDVESSEL")?.GetValue("vesselName"))
                .OfType<string>().ToHashSet(),
            RootIndex = int.TryParse(v.GetValue("root"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var root) ? root : 0,
            LaunchUt = Number(v.GetValue("lct")),
            MissionTimeS = Number(v.GetValue("met")),
            LatitudeDeg = Number(v.GetValue("lat")),
            LongitudeDeg = Number(v.GetValue("lon")),
            AltitudeM = Number(v.GetValue("alt")),
        };
    }

    private static SavedPart ReadPart(ConfigNode p)
    {
        var modules = p.GetNodes("MODULE").ToList();
        var engines = modules.Where(m => m.GetValue("name") is "ModuleEngines" or "ModuleEnginesFX" or "ModuleEnginesRF")
            .Select(m => new SavedEngineState(
                m.GetValue("thrustPercentage") is { } limit ? Number(limit) : 100,
                Flag(m, "EngineIgnited"), Flag(m, "engineShutdown"), Flag(m, "flameout")))
            .ToList();
        var resources = p.GetNodes("RESOURCE")
            .Select(r => new PartResource(r.GetValue("name") ?? "", Number(r.GetValue("amount")), Number(r.GetValue("maxAmount"))))
            .Where(r => r.Name.Length > 0).ToList();
        var multiMode = modules.FirstOrDefault(m => m.GetValue("name") == "MultiModeEngine");
        return new SavedPart(p.GetValue("name") ?? "", Number(p.GetValue("mass")), resources, engines,
            p.Values.Where(v => v.Key == "crew").Select(v => v.Value).ToList())
        {
            RunningPrimaryMode = multiMode is null ? null : !bool.TryParse(multiMode.GetValue("runningPrimary"), out var primary) || primary,
            Uid = p.GetValue("uid") ?? "",
            ParentIndex = int.TryParse(p.GetValue("parent"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parent) ? parent : 0,
            IsCommand = modules.Any(m => m.GetValue("name") == "ModuleCommand"),
            DockingPorts = modules.Where(m => m.GetValue("name") == "ModuleDockingNode")
                .Select(m => new SavedDockingPort(m.GetValue("state") ?? "", m.GetValue("dockUId") ?? "",
                    m.GetNode("DOCKEDVESSEL") is { } d && d.GetValue("vesselName") is { } n
                        ? new DockedVesselInfo(n, d.GetValue("vesselType") ?? "", d.GetValue("rootUId") ?? "")
                        : null))
                .ToList(),
        };
    }

    private static bool Flag(ConfigNode node, string key) => bool.TryParse(node.GetValue(key), out var value) && value;

    /// <summary>KSP writes invariant-culture numbers; "NaN" for orbits of landed vessels.</summary>
    private static double Number(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;

    /// <summary>The save's title from the first lines of the file ("Title = Protogen (SCIENCE_SANDBOX)").</summary>
    private static string? ReadTitle(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            for (var i = 0; i < 20 && reader.ReadLine() is { } line; i++)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Title", StringComparison.Ordinal) && trimmed.IndexOf('=') is var eq and > 0)
                    return trimmed[(eq + 1)..].Trim();
            }
        }
        catch (IOException) { }
        return null;
    }
}
