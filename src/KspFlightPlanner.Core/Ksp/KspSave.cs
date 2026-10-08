using System.Globalization;

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

    public IEnumerable<string> Crew => Parts.SelectMany(p => p.Crew);
}

/// <summary>A part of a vessel in the save. <see cref="Name"/> matches <see cref="KspPart.Name"/> in the part catalog.</summary>
/// <param name="MassT">The part's dry mass in tonnes, as the save records it.</param>
public sealed record SavedPart(string Name, double MassT, IReadOnlyList<PartResource> Resources, IReadOnlyList<SavedEngineState> Engines,
    IReadOnlyList<string> Crew)
{
    /// <summary>Multi-mode engines (e.g. RAPIER): whether the first mode is running; null for other parts.</summary>
    public bool? RunningPrimaryMode { get; init; }
}

/// <summary>An engine module's settings in the save, in the same order as the part's engines in the catalog.</summary>
/// <param name="ThrustPercent">The thrust limiter (0–100).</param>
public sealed record SavedEngineState(double ThrustPercent, bool IsIgnited, bool IsShutdown, bool IsFlameout);

/// <summary>What the planner reads from a save file (persistent.sfs).</summary>
/// <param name="Ut">Game time when the file was written, in seconds since Year 1, Day 1.</param>
public sealed record KspSave(string Title, string Mode, double Ut, IReadOnlyList<SavedVessel> Vessels);

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
        return new KspSave(title, game.GetValue("Mode") ?? "", Number(flight?.GetValue("UT")), vessels);
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
            LaunchUt = Number(v.GetValue("lct")),
            MissionTimeS = Number(v.GetValue("met")),
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
