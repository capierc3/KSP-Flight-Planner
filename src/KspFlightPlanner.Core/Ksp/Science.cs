using KspFlightPlanner.Core.Models;

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

/// <summary>How a result gets home: recovered with the vessel (all of it), or sent home over the antenna (a share).</summary>
public enum ScienceCounting
{
    /// <summary>Brought home and recovered: the full value.</summary>
    Recovered,

    /// <summary>Sent home: the value × the part's transmit fraction. A probe that never comes back only ever earns this.</summary>
    Transmitted,

    /// <summary>Show both; rank and colour by the recovered value.</summary>
    Both,
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

    /// <summary>Where the atmosphere ends (0 without one): below it a craft is flying, above it in space. From the add-on; older catalogs lack it.</summary>
    public double AtmosphereHeightM { get; init; }

    /// <summary>
    /// The situation of a craft in flight at an altitude, as KSP works it out: flying inside the atmosphere (low under the
    /// flying-high line), else in space (low under the space-high line).
    /// </summary>
    public ScienceSituation InFlightAt(double altitudeM) =>
        HasAtmosphere && altitudeM < AtmosphereHeightM
            ? altitudeM < FlyingHighAltitudeM ? ScienceSituation.FlyingLow : ScienceSituation.FlyingHigh
            : altitudeM < SpaceHighAltitudeM ? ScienceSituation.InSpaceLow : ScienceSituation.InSpaceHigh;
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

    /// <summary>
    /// Part name (normalized) → experiment id → the share of the value its result earns when sent home (the module's
    /// xmitDataScalar). From the add-on; empty in catalogs fetched before it was added.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> PartTransmit { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, double>>();

    public ScienceExperimentDef? Experiment(string id) => Experiments.FirstOrDefault(e => e.Id == id);
    public ScienceBody? Body(string name) => Bodies.FirstOrDefault(b => b.Name == name);
}

/// <summary>A subject the save's R&amp;D records hold: science earned so far out of its cap.</summary>
/// <param name="BodyValue">The body's multiplier for the subject's situation (the save's sbv).</param>
public sealed record SavedScienceSubject(string Id, double Science, double Cap, double BodyValue);

/// <summary>Data a vessel carries that hasn't been recovered or sent home yet (a ScienceData node).</summary>
/// <param name="Transmit">The share of its value it earns if sent home instead (the node's xmit).</param>
public sealed record CarriedScience(string SubjectId, double Data, double Transmit = 1);

/// <summary>One experiment's share of a cell: what one run there pays, and what's left in the subject.</summary>
/// <param name="NextRun">Science one run earns, counted the table's way (<see cref="ScienceCounting"/>; Both counts recovered).</param>
/// <param name="Left">Science left in the subject, out of <paramref name="Cap"/>.</param>
/// <param name="CarriedCopies">Copies of this subject already aboard some vessel.</param>
/// <param name="Recovered">What one run earns once recovered, after the copies aboard come home.</param>
/// <param name="Sent">What one run earns sent home, after the copies aboard are sent too.</param>
public sealed record ScienceGapEntry(string ExperimentId, string Title, string SubjectId, double NextRun, double Left, double Cap, int CarriedCopies,
    double Recovered, double Sent);

/// <summary>One biome (or the whole body) in one situation.</summary>
public sealed record ScienceGapCell(ScienceSituation Situation, IReadOnlyList<ScienceGapEntry> Entries)
{
    public double NextRun => Entries.Sum(e => e.NextRun);
    public double Recovered => Entries.Sum(e => e.Recovered);
    public double Sent => Entries.Sum(e => e.Sent);
}

/// <summary>A row of the table: a biome, or <see cref="ScienceGapTable.AnywhereRow"/> for experiments where biome doesn't matter.</summary>
public sealed record ScienceGapRow(string Biome, bool IsMiniBiome, IReadOnlyList<ScienceGapCell> Cells)
{
    public ScienceGapCell? Cell(ScienceSituation s) => Cells.FirstOrDefault(c => c.Situation == s);
    public double NextRun => Cells.Sum(c => c.NextRun);
    public double Recovered => Cells.Sum(c => c.Recovered);
    public double Sent => Cells.Sum(c => c.Sent);
}

/// <summary>What's left to collect at a body for a set of experiments: rows of biomes, columns of situations (docs/SCIENCE.md).</summary>
public sealed record ScienceGapTable(string BodyName, IReadOnlyList<ScienceSituation> Situations, IReadOnlyList<ScienceGapRow> Rows)
{
    /// <summary>The row for subjects with no biome: the experiment gives the same subject anywhere at the body in that situation.</summary>
    public const string AnywhereRow = "Anywhere";

    public double NextRun => Rows.Sum(r => r.NextRun);
    public double Recovered => Rows.Sum(r => r.Recovered);
    public double Sent => Rows.Sum(r => r.Sent);
}

/// <summary>Science subjects and what's still left in them (docs/SCIENCE.md).</summary>
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

    /// <summary>
    /// Each experiment's transmit fraction: the best of the parts that run it, on the vessel's parts (and the kerbal's
    /// EVA when crewed), or on every part the game has when <paramref name="vessel"/> is null. 1 where the catalog doesn't say.
    /// </summary>
    public static IReadOnlyDictionary<string, double> TransmitFractions(SavedVessel? vessel, ScienceCatalog catalog)
    {
        var parts = vessel is null ? catalog.PartTransmit.Keys.ToList()
            : vessel.Parts.Select(p => PartCatalog.NormalizeName(p.Name)).Concat(vessel.Crew.Any() ? [ScienceCatalog.EvaPart] : []).ToList();
        var best = new Dictionary<string, double>();
        foreach (var part in parts)
            foreach (var (id, xmit) in catalog.PartTransmit.GetValueOrDefault(part) ?? new Dictionary<string, double>())
                best[id] = Math.Max(best.GetValueOrDefault(id), xmit);
        return best;
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
    /// <remarks>
    /// Sent home, a result earns its value × the part's transmit fraction, and only that much fills the subject, so the
    /// sent figure assumes the copies aboard are sent too (each with its own fraction).
    /// </remarks>
    public static ScienceGapEntry Entry(ScienceExperimentDef e, ScienceBody body, ScienceSituation s, string? biome,
        IReadOnlyDictionary<string, SavedScienceSubject> subjects, IReadOnlyDictionary<string, IReadOnlyList<CarriedScience>> carried,
        double transmit = 1, ScienceCounting counting = ScienceCounting.Recovered)
    {
        var id = SubjectId(e.Id, body.Name, s, e.BiomeMatters(s) ? biome : null);
        var saved = subjects.GetValueOrDefault(id);
        var multiplier = saved?.BodyValue ?? body.Multiplier(s);
        var cap = saved?.Cap ?? e.ScienceCap * multiplier;
        var earned = saved?.Science ?? 0;
        var earnedSent = earned;
        var copies = carried.GetValueOrDefault(id) ?? [];
        foreach (var copy in copies)
        {
            earned += Value(copy.Data, e.DataScale, multiplier, earned, cap);
            earnedSent += Value(copy.Data, e.DataScale, multiplier, earnedSent, cap) * copy.Transmit;
        }
        var run = e.BaseValue * e.DataScale;
        var recovered = Value(run, e.DataScale, multiplier, earned, cap);
        var sent = Value(run, e.DataScale, multiplier, earnedSent, cap) * transmit;
        var transmitted = counting == ScienceCounting.Transmitted;
        return new ScienceGapEntry(e.Id, e.Title, id, transmitted ? sent : recovered,
            Math.Max(0, cap - (transmitted ? earnedSent : earned)), cap, copies.Count, recovered, sent);
    }

    /// <summary>KSP's GetScienceValue for recovered data: data ÷ dataScale × multiplier × (1 − earned ÷ cap), never below 0.</summary>
    public static double Value(double data, double dataScale, double multiplier, double earned, double cap) =>
        cap <= 0 || dataScale <= 0 ? 0 : Math.Max(0, data / dataScale * multiplier * (1 - earned / cap));

    /// <summary>
    /// The table for a body: a row per biome (mini-biomes after, landed only), then <see cref="ScienceGapTable.AnywhereRow"/>
    /// for experiments whose subject has no biome in that situation. Columns are the situations at least one of the
    /// experiments can use at the body.
    /// </summary>
    /// <param name="transmit">Each experiment's transmit fraction (<see cref="TransmitFractions"/>); 1 where missing.</param>
    public static ScienceGapTable Table(ScienceBody body, IReadOnlyList<ScienceExperimentDef> experiments,
        IReadOnlyDictionary<string, SavedScienceSubject> subjects, IReadOnlyDictionary<string, IReadOnlyList<CarriedScience>> carried,
        IReadOnlyDictionary<string, double>? transmit = null, ScienceCounting counting = ScienceCounting.Recovered)
    {
        var situations = AllSituations.Where(s => experiments.Any(e => Available(e, body, s))).ToList();

        ScienceGapRow Row(string label, string? biome, bool mini, Func<ScienceExperimentDef, ScienceSituation, bool> belongs) =>
            new(label, mini, situations.Select(s => new ScienceGapCell(s,
                experiments.Where(e => Available(e, body, s) && belongs(e, s))
                    .Select(e => Entry(e, body, s, biome, subjects, carried, transmit?.GetValueOrDefault(e.Id, 1) ?? 1, counting)).ToList())).ToList());

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
        var partLines = parts.Select(l => l.Split('\t')).Where(f => f.Length >= 2)
            .GroupBy(f => PartCatalog.NormalizeName(f[0])).ToDictionary(g => g.Key, g => g.First());
        var partMap = partLines.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value[1].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList());
        // Third field: each experiment's transmit fraction, in the same order.
        var partTransmit = partLines.Where(kv => kv.Value.Length >= 3).ToDictionary(kv => kv.Key, kv =>
        {
            var ids = kv.Value[1].Split(',', StringSplitOptions.RemoveEmptyEntries);
            var xmits = kv.Value[2].Split(',', StringSplitOptions.RemoveEmptyEntries);
            return (IReadOnlyDictionary<string, double>)ids.Zip(xmits).GroupBy(x => x.First).ToDictionary(g => g.Key, g => g.Max(x => Num(x.Second)));
        });
        var bodyList = bodies.Select(l => l.Split('\t')).Where(f => f.Length >= 14)
            .Select(f => new ScienceBody(f[0], Num(f[1]), Num(f[2]), Num(f[3]), Num(f[4]), Num(f[5]), Num(f[6]),
                f[7] == "1", f[8] == "1", f[9] == "1", Num(f[10]), Num(f[11]), Names(f[12]), Names(f[13]))
            {
                AtmosphereHeightM = f.Length >= 15 ? Num(f[14]) : 0,
            })
            .ToList();
        return new ScienceCatalog(defs, partMap, bodyList, fetchedUtc) { PartTransmit = partTransmit };
    }

    private static IReadOnlyList<string> Names(string field) => field.Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static double Num(string s) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static int Int(string s) =>
        int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
}

/// <summary>
/// A body's biomes on a latitude/longitude grid (row-major from 90° S / 180° W, like the surface bakes), with its
/// rotation so later stages can follow a ground track. Fetched once per save through the add-on's BiomeGrid.
/// </summary>
public sealed record ScienceBiomeMap(string BodyName, double StepDeg, double RadiusM, double RotationPeriodS, double InitialRotationDeg,
    short[] Biomes, IReadOnlyList<string> BiomeNames, DateTime FetchedUtc)
{
    public int Rows => SurfaceBake.GridRows(StepDeg);
    public int Columns => SurfaceBake.GridColumns(StepDeg);
    public int Count => Rows * Columns;

    public string? BiomeOf(int index) => index >= 0 && index < Biomes.Length && Biomes[index] is var b and >= 0 && b < BiomeNames.Count ? BiomeNames[b] : null;

    /// <summary>The biome at a spot: the nearest grid point.</summary>
    public string? BiomeAt(double latitudeDeg, double longitudeDeg)
    {
        var row = (int)Math.Round((Math.Clamp(latitudeDeg, -90, 90) + 90) / StepDeg);
        var lon = ((longitudeDeg + 180) % 360 + 360) % 360;
        var column = (int)Math.Round(lon / StepDeg) % Columns;
        return BiomeOf(Math.Min(row, Rows - 1) * Columns + column);
    }
}

public static class ScienceMapping
{
    /// <summary>What one run of the experiments earns in each biome, in one situation (biomes with nothing to run are left out).</summary>
    public static IReadOnlyDictionary<string, double> BiomeValues(ScienceGapTable table, ScienceSituation situation) =>
        table.Rows.Where(r => r.Biome != ScienceGapTable.AnywhereRow && !r.IsMiniBiome)
            .Select(r => (r.Biome, Cell: r.Cell(situation)))
            .Where(x => x.Cell is { Entries.Count: > 0 })
            .ToDictionary(x => x.Biome, x => x.Cell!.NextRun);

    /// <summary>The situation whose biomes pay most in all, for the map to start on.</summary>
    public static ScienceSituation? BestSituation(ScienceGapTable table) =>
        table.Situations.Count == 0 ? null
        : table.Situations.OrderByDescending(s => table.Rows.Where(r => r.Biome != ScienceGapTable.AnywhereRow).Sum(r => r.Cell(s)?.NextRun ?? 0)).First();
}

/// <summary>A stretch of an orbit over one biome (or anywhere) in one situation where one run of the experiments pays.</summary>
/// <param name="Biome">The biome under the vessel; null when no experiment's subject depends on it there.</param>
/// <param name="Entries">Each experiment's share: the biome's cell plus the <see cref="ScienceGapTable.AnywhereRow"/> cell.</param>
public sealed record SciencePass(double StartUt, double EndUt, ScienceSituation Situation, string? Biome, double NextRun, IReadOnlyList<ScienceGapEntry> Entries);

/// <summary>When a vessel on its orbit will be over somewhere that still pays (docs/SCIENCE.md).</summary>
public static class SciencePasses
{
    /// <summary>Stretches shorter than this are biome-edge flicker on the grid, too short to press a button in.</summary>
    public const double ShortestS = 5;

    /// <summary>Most steps to take along one look ahead; past that the steps get longer.</summary>
    private const int MostSteps = 200_000;

    /// <summary>
    /// Follows the orbit from <paramref name="fromUt"/> for <paramref name="orbits"/> turns, finding where the vessel is (the
    /// biome on the ground below and the situation from its altitude) and what one run of the table's experiments pays there.
    /// Returns the stretches that pay at least <paramref name="paysAtLeast"/>, in time order. Stops where the orbit meets
    /// the ground. <paramref name="map"/> may be null for a body without biomes.
    /// </summary>
    public static IReadOnlyList<SciencePass> Upcoming(OrbitalElements orbit, double mu, ScienceBody body, ScienceBiomeMap? map,
        ScienceGapTable table, double fromUt, int orbits = 3, double paysAtLeast = 1, int most = 20)
    {
        var a = orbit.SemiMajorAxisM;
        var e = orbit.Eccentricity;
        if (!(a > 0) || e is < 0 or >= 1 || !(mu > 0) || map is null && body.Biomes.Count > 0) return [];
        var radius = map?.RadiusM ?? 0;
        if (!(radius > 0)) return [];
        var spin = map is { RotationPeriodS: not 0 } ? 2 * Math.PI / map.RotationPeriodS : 0;
        var period = Kepler.OrbitalPeriod(orbit, mu);
        var toUt = fromUt + orbits * period;
        var h = Math.Sqrt(mu * a * (1 - e * e));
        var stepRad = (map?.StepDeg ?? 1) / 2 * Math.PI / 180; // half a grid step over the ground
        var longest = Math.Max(period / 720, (toUt - fromUt) / MostSteps); // the altitude lines are crossed in time too

        var anywhere = table.Rows.FirstOrDefault(r => r.Biome == ScienceGapTable.AnywhereRow);
        var rows = table.Rows.Where(r => !r.IsMiniBiome && r.Biome != ScienceGapTable.AnywhereRow).ToDictionary(r => r.Biome);

        var passes = new List<SciencePass>();
        (ScienceSituation Situation, string? Biome)? current = null;
        var start = fromUt;
        void Close(double end)
        {
            if (current is not { } key) return;
            var entries = (key.Biome is { } b && rows.TryGetValue(b, out var row) ? row.Cell(key.Situation)?.Entries ?? [] : [])
                .Concat(anywhere?.Cell(key.Situation)?.Entries ?? []).ToList();
            var value = entries.Sum(x => x.NextRun);
            if (value >= paysAtLeast && (end - start >= ShortestS || start == fromUt))
                passes.Add(new SciencePass(start, end, key.Situation, key.Biome, value, entries));
        }

        for (var t = fromUt; ; )
        {
            var r = Kepler.StateAt(orbit, mu, t).Position;
            var length = r.Length;
            var altitude = length - radius;
            if (altitude <= 0) { Close(t); return passes.Take(most).ToList(); }
            var situation = body.InFlightAt(altitude);
            string? biome = null;
            if (map is not null && rows.Count > 0)
            {
                var lat = Math.Asin(Math.Clamp(r.Z / length, -1, 1)) * 180 / Math.PI;
                var lon = Math.Atan2(r.Y, r.X) * 180 / Math.PI - map.InitialRotationDeg - spin * t * 180 / Math.PI;
                var under = map.BiomeAt(lat, lon);
                // Only a biome that changes the subject there splits a stretch.
                if (under is not null && rows.TryGetValue(under, out var row) && row.Cell(situation) is { Entries.Count: > 0 }) biome = under;
            }
            if (current != (situation, biome))
            {
                Close(t);
                if (passes.Count >= most) return passes;
                current = (situation, biome);
                start = t;
            }
            if (t >= toUt) { Close(t); break; }
            var rate = h / (length * length) + Math.Abs(spin);
            t = Math.Min(toUt, t + Math.Min(longest, stepRad / rate));
        }
        return passes.Take(most).ToList();
    }
}

