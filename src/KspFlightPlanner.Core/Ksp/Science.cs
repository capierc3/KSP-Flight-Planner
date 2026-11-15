namespace KspFlightPlanner.Core.Ksp;

/// <summary>KSP's experiment situations, as the bits of an experiment's situationMask and biomeMask.</summary>
[Flags]
public enum ScienceSituation
{
    None = 0,
    SrfLanded = 1,
    SrfSplashed = 2,
    FlyingLow = 4,
    FlyingHigh = 8,
    InSpaceLow = 16,
    InSpaceHigh = 32,
}

/// <summary>An experiment as the game defines it (ScienceDefs.cfg, or a mod's EXPERIMENT_DEFINITION).</summary>
public sealed record ScienceExperimentDef(
    string Id, string Title, double BaseValue, double ScienceCap, double DataScale,
    int SituationMask, int BiomeMask, bool RequireAtmosphere, bool RequireNoAtmosphere)
{
    public bool WorksIn(ScienceSituation s) => (SituationMask & (int)s) != 0;
    public bool BiomeMatters(ScienceSituation s) => (BiomeMask & (int)s) != 0;

    /// <summary>Asteroid and comet samples are per object, not per place, so the gap table leaves them out.</summary>
    public bool IsPerObject => Id.StartsWith("asteroidSample", StringComparison.Ordinal) || Id.StartsWith("cometSample", StringComparison.Ordinal);
}

/// <summary>A body's science: its multiplier for each situation (CelestialBodyScienceParams) and its biomes.</summary>
/// <param name="Biomes">The biome map's names, as the game shows them ("Greater Flats").</param>
/// <param name="MiniBiomes">Small biomes on top of the map (the buildings around KSC), landed only.</param>
public sealed record ScienceBody(
    string Name, double Landed, double Splashed, double FlyingLow, double FlyingHigh, double InSpaceLow, double InSpaceHigh,
    bool HasAtmosphere, bool HasOcean, bool HasSurface, double FlyingHighAltitudeM, double SpaceHighAltitudeM,
    IReadOnlyList<string> Biomes, IReadOnlyList<string> MiniBiomes)
{
    public double Multiplier(ScienceSituation s) => s switch
    {
        ScienceSituation.SrfLanded => Landed,
        ScienceSituation.SrfSplashed => Splashed,
        ScienceSituation.FlyingLow => FlyingLow,
        ScienceSituation.FlyingHigh => FlyingHigh,
        ScienceSituation.InSpaceLow => InSpaceLow,
        ScienceSituation.InSpaceHigh => InSpaceHigh,
        _ => 0,
    };

    /// <summary>Whether a craft can be in this situation at the body at all: no flying without air, no splashing without sea.</summary>
    public bool Has(ScienceSituation s) => s switch
    {
        ScienceSituation.SrfLanded => HasSurface,
        ScienceSituation.SrfSplashed => HasOcean,
        ScienceSituation.FlyingLow or ScienceSituation.FlyingHigh => HasAtmosphere,
        ScienceSituation.InSpaceLow or ScienceSituation.InSpaceHigh => true,
        _ => false,
    };
}

/// <summary>
/// What the game says about science, fetched once per save through the planner's add-on ("PlannerScience"):
/// the experiments, which parts carry which, and each body's multipliers and biomes.
/// </summary>
/// <param name="PartExperiments">Part name (normalized like <see cref="PartCatalog.NormalizeName"/>) → experiment ids its modules run.</param>
public sealed record ScienceCatalog(
    IReadOnlyList<ScienceExperimentDef> Experiments,
    IReadOnlyDictionary<string, IReadOnlyList<string>> PartExperiments,
    IReadOnlyList<ScienceBody> Bodies,
    DateTime FetchedUtc)
{
    /// <summary>The kerbal's EVA part: its experiments (EVA report, surface sample) go with any crewed vessel.</summary>
    public const string EvaPart = "kerbalEVA";

    public ScienceExperimentDef? Experiment(string id) => Experiments.FirstOrDefault(e => e.Id == id);
    public ScienceBody? Body(string name) => Bodies.FirstOrDefault(b => b.Name == name);
}

/// <summary>A subject the save's R&amp;D records hold: science earned so far out of its cap.</summary>
/// <param name="BodyValue">The body's multiplier for the subject's situation (the save's sbv).</param>
public sealed record SavedScienceSubject(string Id, double Science, double Cap, double BodyValue);

/// <summary>Data a vessel carries that hasn't been recovered or sent home yet (a ScienceData node).</summary>
public sealed record CarriedScience(string SubjectId, double Data);

/// <summary>One experiment's share of a cell: what one run there pays, and what's left in the subject.</summary>
/// <param name="NextRun">Science one run would earn once recovered (after any copies already aboard come home).</param>
/// <param name="Left">Science left in the subject, out of <paramref name="Cap"/>.</param>
/// <param name="CarriedCopies">Copies of this subject already aboard some vessel.</param>
public sealed record ScienceGapEntry(string ExperimentId, string Title, string SubjectId, double NextRun, double Left, double Cap, int CarriedCopies);

/// <summary>One biome (or the whole body) in one situation.</summary>
public sealed record ScienceGapCell(ScienceSituation Situation, IReadOnlyList<ScienceGapEntry> Entries)
{
    public double NextRun => Entries.Sum(e => e.NextRun);
}

/// <summary>A row of the table: a biome, or <see cref="ScienceGapTable.AnywhereRow"/> for experiments where biome doesn't matter.</summary>
public sealed record ScienceGapRow(string Biome, bool IsMiniBiome, IReadOnlyList<ScienceGapCell> Cells)
{
    public ScienceGapCell? Cell(ScienceSituation s) => Cells.FirstOrDefault(c => c.Situation == s);
    public double NextRun => Cells.Sum(c => c.NextRun);
}

/// <summary>What's left to collect at a body for a set of experiments: rows of biomes, columns of situations (docs/SCIENCE.md).</summary>
public sealed record ScienceGapTable(string BodyName, IReadOnlyList<ScienceSituation> Situations, IReadOnlyList<ScienceGapRow> Rows)
{
    /// <summary>The row for subjects with no biome: the experiment gives the same subject anywhere at the body in that situation.</summary>
    public const string AnywhereRow = "Anywhere";

    public double NextRun => Rows.Sum(r => r.NextRun);
}

/// <summary>Science subjects and what's still left in them (docs/SCIENCE.md, docs/MATH.md §14.1).</summary>
public static class ScienceGaps
{
    /// <summary>Situations in the order KSP lists them, surface to high space.</summary>
    public static readonly ScienceSituation[] AllSituations =
    [
        ScienceSituation.SrfLanded, ScienceSituation.SrfSplashed, ScienceSituation.FlyingLow,
        ScienceSituation.FlyingHigh, ScienceSituation.InSpaceLow, ScienceSituation.InSpaceHigh,
    ];

    /// <summary>A subject's id as KSP builds it: experiment@Body + Situation + Biome, the biome without spaces.</summary>
    public static string SubjectId(string experimentId, string bodyName, ScienceSituation situation, string? biome) =>
        $"{experimentId}@{bodyName}{situation}{biome?.Replace(" ", "", StringComparison.Ordinal) ?? ""}";

    /// <summary>Short names for the situations, as column headings.</summary>
    public static string Label(ScienceSituation s) => s switch
    {
        ScienceSituation.SrfLanded => "Landed",
        ScienceSituation.SrfSplashed => "Splashed",
        ScienceSituation.FlyingLow => "Flying low",
        ScienceSituation.FlyingHigh => "Flying high",
        ScienceSituation.InSpaceLow => "Space low",
        ScienceSituation.InSpaceHigh => "Space high",
        _ => s.ToString(),
    };

    /// <summary>Whether the experiment can run in the situation at that body (its mask, the body's air, and whether the body has it).</summary>
    public static bool Available(ScienceExperimentDef e, ScienceBody body, ScienceSituation s) =>
        e.WorksIn(s) && body.Has(s) && (!e.RequireAtmosphere || body.HasAtmosphere) && (!e.RequireNoAtmosphere || !body.HasAtmosphere);

    /// <summary>
    /// The experiments a vessel can run: those of its parts, plus the kerbal's EVA experiments when it has crew.
    /// The crew report needs someone aboard, so an uncrewed probe core's pod doesn't count it.
    /// </summary>
    public static IReadOnlyList<ScienceExperimentDef> Aboard(SavedVessel vessel, ScienceCatalog catalog)
    {
        var crewed = vessel.Crew.Any();
        var ids = vessel.Parts.SelectMany(p => catalog.PartExperiments.GetValueOrDefault(PartCatalog.NormalizeName(p.Name)) ?? []).ToHashSet();
        if (crewed) ids.UnionWith(catalog.PartExperiments.GetValueOrDefault(ScienceCatalog.EvaPart) ?? []);
        else ids.Remove("crewReport");
        return catalog.Experiments.Where(e => ids.Contains(e.Id) && !e.IsPerObject).ToList();
    }

    /// <summary>Every subject's copies carried on vessels in the save, by subject id.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<CarriedScience>> Carried(KspSave save) =>
        save.Vessels.SelectMany(v => v.CarriedScience).GroupBy(c => c.SubjectId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CarriedScience>)g.ToList());

    /// <summary>
    /// What one run of the experiment would earn in the subject, once recovered, and what's left. KSP's value of data is
    /// data ÷ dataScale × the body's multiplier × (1 − earned ÷ cap); a run makes baseValue × dataScale of data.
    /// Copies already aboard come home first, each using up its share.
    /// </summary>
    public static ScienceGapEntry Entry(ScienceExperimentDef e, ScienceBody body, ScienceSituation s, string? biome,
        IReadOnlyDictionary<string, SavedScienceSubject> subjects, IReadOnlyDictionary<string, IReadOnlyList<CarriedScience>> carried)
    {
        var id = SubjectId(e.Id, body.Name, s, e.BiomeMatters(s) ? biome : null);
        var saved = subjects.GetValueOrDefault(id);
        var multiplier = saved?.BodyValue ?? body.Multiplier(s);
        var cap = saved?.Cap ?? e.ScienceCap * multiplier;
        var earned = saved?.Science ?? 0;
        var copies = carried.GetValueOrDefault(id) ?? [];
        foreach (var copy in copies)
            earned += Value(copy.Data, e.DataScale, multiplier, earned, cap);
        var next = Value(e.BaseValue * e.DataScale, e.DataScale, multiplier, earned, cap);
        return new ScienceGapEntry(e.Id, e.Title, id, next, Math.Max(0, cap - earned), cap, copies.Count);
    }

    /// <summary>KSP's GetScienceValue for recovered data: data ÷ dataScale × multiplier × (1 − earned ÷ cap), never below 0.</summary>
    public static double Value(double data, double dataScale, double multiplier, double earned, double cap) =>
        cap <= 0 || dataScale <= 0 ? 0 : Math.Max(0, data / dataScale * multiplier * (1 - earned / cap));

    /// <summary>
    /// The table for a body: a row per biome (mini-biomes after, landed only), then <see cref="ScienceGapTable.AnywhereRow"/>
    /// for experiments whose subject has no biome in that situation. Columns are the situations at least one of the
    /// experiments can use at the body.
    /// </summary>
    public static ScienceGapTable Table(ScienceBody body, IReadOnlyList<ScienceExperimentDef> experiments,
        IReadOnlyDictionary<string, SavedScienceSubject> subjects, IReadOnlyDictionary<string, IReadOnlyList<CarriedScience>> carried)
    {
        var situations = AllSituations.Where(s => experiments.Any(e => Available(e, body, s))).ToList();

        ScienceGapRow Row(string label, string? biome, bool mini, Func<ScienceExperimentDef, ScienceSituation, bool> belongs) =>
            new(label, mini, situations.Select(s => new ScienceGapCell(s,
                experiments.Where(e => Available(e, body, s) && belongs(e, s))
                    .Select(e => Entry(e, body, s, biome, subjects, carried)).ToList())).ToList());

        var rows = new List<ScienceGapRow>();
        rows.AddRange(body.Biomes.Select(b => Row(b, b, false, (e, s) => e.BiomeMatters(s))));
        rows.AddRange(body.MiniBiomes.Select(b => Row(b, b, true, (e, s) => s == ScienceSituation.SrfLanded && e.BiomeMatters(s))));
        // A body without a biome map (the Sun) gives every experiment one subject per situation, with no biome in its id.
        var anywhere = Row(ScienceGapTable.AnywhereRow, null, false, (e, s) => !e.BiomeMatters(s) || body.Biomes.Count == 0);
        if (anywhere.Cells.Any(c => c.Entries.Count > 0)) rows.Add(anywhere);
        return new ScienceGapTable(body.Name, situations, rows);
    }
}

/// <summary>Reads the add-on's answers (lines of tab-separated fields, see mod/…/Science/ScienceService.cs) into a <see cref="ScienceCatalog"/>.</summary>
public static class ScienceCatalogReader
{
    public static ScienceCatalog Parse(IEnumerable<string> experiments, IEnumerable<string> parts, IEnumerable<string> bodies, DateTime fetchedUtc)
    {
        var defs = experiments.Select(l => l.Split('\t')).Where(f => f.Length >= 9)
            .Select(f => new ScienceExperimentDef(f[0], f[1], Num(f[2]), Num(f[3]), Num(f[4]), Int(f[5]), Int(f[6]), f[7] == "1", f[8] == "1"))
            .ToList();
        var partMap = parts.Select(l => l.Split('\t')).Where(f => f.Length >= 2)
            .GroupBy(f => PartCatalog.NormalizeName(f[0]))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.First()[1].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList());
        var bodyList = bodies.Select(l => l.Split('\t')).Where(f => f.Length >= 14)
            .Select(f => new ScienceBody(f[0], Num(f[1]), Num(f[2]), Num(f[3]), Num(f[4]), Num(f[5]), Num(f[6]),
                f[7] == "1", f[8] == "1", f[9] == "1", Num(f[10]), Num(f[11]), Names(f[12]), Names(f[13])))
            .ToList();
        return new ScienceCatalog(defs, partMap, bodyList, fetchedUtc);
    }

    private static IReadOnlyList<string> Names(string field) => field.Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static double Num(string s) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static int Int(string s) =>
        int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
}
