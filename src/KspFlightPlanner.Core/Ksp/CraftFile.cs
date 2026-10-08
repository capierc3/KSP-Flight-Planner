using System.Globalization;

namespace KspFlightPlanner.Core.Ksp;

/// <summary>A part of a saved ship design.</summary>
/// <param name="Id">The craft file's id for this part instance ("liquidEngine3.v2_4294121630").</param>
/// <param name="Name">The part's catalog name ("liquidEngine3.v2").</param>
/// <param name="ActivationStage">The stage that fires this part (istg); higher stages fire first.</param>
/// <param name="SeparationStage">The stage whose decoupler drops this part (sepI); -1 if it never leaves.</param>
/// <param name="ExtraMassT">Mass added by the part's options, e.g. a variant (modMass).</param>
/// <param name="Children">Ids of the parts attached to this one (link).</param>
/// <param name="FuelLineTarget">For a fuel line: the id of the part it feeds; it draws from the part it's attached to.</param>
/// <param name="CrossfeedSwitch">A decoupler's crossfeed toggle (ModuleToggleCrossfeed); null when the part has none.</param>
public sealed record CraftPart(
    string Id,
    string Name,
    int ActivationStage,
    int SeparationStage,
    double ExtraMassT,
    IReadOnlyList<PartResource> Resources,
    IReadOnlyList<SavedEngineState> Engines,
    IReadOnlyList<string> Children,
    string? FuelLineTarget,
    bool? CrossfeedSwitch)
{
    public bool? RunningPrimaryMode { get; init; }
}

/// <summary>A ship saved in the VAB or SPH (Ships/VAB/*.craft, Ships/SPH/*.craft).</summary>
/// <param name="Facility">VAB or SPH.</param>
public sealed record CraftFile(string Name, string Facility, string? Description, IReadOnlyList<CraftPart> Parts)
{
    /// <summary>The highest stage number: the first one that fires.</summary>
    public int FirstStage => Parts.Count == 0 ? 0 : Math.Max(0, Parts.Max(p => p.ActivationStage));
}

/// <summary>A craft file found in a save's Ships folder.</summary>
/// <param name="IsAutoSave">KSP's "Auto-Saved Ship": whatever is open in the editor right now.</param>
public sealed record CraftFileInfo(string Path, string Facility, DateTime LastWriteTime, bool IsAutoSave);

public static class CraftReader
{
    public const string AutoSaveName = "Auto-Saved Ship";

    /// <summary>Every .craft file in saves/&lt;save&gt;/Ships/VAB and /SPH, newest first.</summary>
    public static IReadOnlyList<CraftFileInfo> FindCrafts(string installPath, string saveName)
    {
        var ships = ShipsFolder(installPath, saveName);
        var list = new List<CraftFileInfo>();
        foreach (var facility in new[] { "VAB", "SPH" })
        {
            var dir = Path.Combine(ships, facility);
            if (!Directory.Exists(dir)) continue;
            list.AddRange(Directory.EnumerateFiles(dir, "*.craft").Select(f => new CraftFileInfo(f, facility, File.GetLastWriteTime(f),
                Path.GetFileNameWithoutExtension(f) == AutoSaveName)));
        }
        return list.OrderByDescending(c => c.LastWriteTime).ToList();
    }

    public static string ShipsFolder(string installPath, string saveName) => Path.Combine(installPath, "saves", saveName, "Ships");

    /// <summary>Reads a craft file, sharing it with KSP; retries briefly while the editor is writing it.</summary>
    public static CraftFile Read(string path)
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

    public static CraftFile Parse(string text)
    {
        var root = ConfigNode.Parse(text);
        var parts = root.GetNodes("PART").Select(ReadPart).ToList();
        return new CraftFile(root.GetValue("ship") ?? "(unnamed)", root.GetValue("type") ?? "VAB",
            root.GetValue("description") is { Length: > 0 } d ? d.Replace("¨", "\n") : null, parts);
    }

    private static CraftPart ReadPart(ConfigNode p)
    {
        var id = p.GetValue("part") ?? "";
        var modules = p.GetNodes("MODULE").ToList();
        var engines = modules.Where(m => m.GetValue("name") is "ModuleEngines" or "ModuleEnginesFX" or "ModuleEnginesRF")
            .Select(m => new SavedEngineState(m.GetValue("thrustPercentage") is { } limit ? Number(limit, 100) : 100, false, false, false))
            .ToList();
        var resources = p.GetNodes("RESOURCE")
            .Select(r => new PartResource(r.GetValue("name") ?? "", Number(r.GetValue("amount")), Number(r.GetValue("maxAmount"))))
            .Where(r => r.Name.Length > 0).ToList();
        var crossfeed = modules.FirstOrDefault(m => m.GetValue("name") == "ModuleToggleCrossfeed")?.GetValue("crossfeedStatus");
        var multiMode = modules.FirstOrDefault(m => m.GetValue("name") == "MultiModeEngine");
        var target = modules.Any(m => m.GetValue("name") == "CModuleFuelLine") ? p.GetNode("PARTDATA")?.GetValue("tgt") : null;

        return new CraftPart(
            id,
            PartName(id),
            (int)Number(p.GetValue("istg")),
            p.GetValue("sepI") is { } sep ? (int)Number(sep, -1) : -1,
            Number(p.GetValue("modMass")),
            resources,
            engines,
            p.Values.Where(v => v.Key == "link").Select(v => v.Value).ToList(),
            target,
            bool.TryParse(crossfeed, out var on) ? on : null)
        {
            RunningPrimaryMode = multiMode is null ? null : !bool.TryParse(multiMode.GetValue("runningPrimary"), out var primary) || primary,
        };
    }

    /// <summary>"liquidEngine3.v2_4294121630" → "liquidEngine3.v2" (the id is the last '_' and digits).</summary>
    public static string PartName(string id)
    {
        var underscore = id.LastIndexOf('_');
        return underscore > 0 && id[(underscore + 1)..].All(char.IsDigit) ? id[..underscore] : id;
    }

    private static double Number(string? text, double fallback = 0) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
