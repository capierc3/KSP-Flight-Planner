using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core.Ksp;

/// <summary>How much a survey knows about a biome (docs/SURVEY.md).</summary>
public enum SurveyLevel
{
    /// <summary>Words only (Poor, Fair, Good…): the survey scanner's strips, once the body's orbital survey is done.</summary>
    Rough,

    /// <summary>The rounded figure the narrow-band scanner shows for a biome nobody has analysed.</summary>
    Estimate,

    /// <summary>True values: the biome has been analysed from the ground.</summary>
    Surveyed,
}

/// <summary>
/// A body's ore and biomes as the game generates them for one save, fetched once through the planner's add-on. The grid
/// is the surface bake's: rows from 90° S to 90° N, columns from 180° W eastward, one every <see cref="StepDeg"/>.
/// </summary>
/// <param name="Abundance">True ore abundance at each point, as a fraction (0.105 = 10.5 %), what a drill there would mine.</param>
/// <param name="Estimate">What the scanners show before a biome is analysed: one flat value per biome.</param>
/// <param name="Biomes">Biome index into <paramref name="BiomeNames"/> at each point; -1 where there's none.</param>
/// <param name="InitialRotationDeg">The body's rotation angle at UT 0 (θ₀), so ground tracks can be worked out from orbits.</param>
public sealed record SurveyMap(
    string BodyName, double StepDeg, double RadiusM, double RotationPeriodS, double InitialRotationDeg,
    float[] Abundance, float[] Estimate, short[] Biomes, IReadOnlyList<string> BiomeNames, DateTime FetchedUtc)
{
    public int Rows => SurfaceBake.GridRows(StepDeg);
    public int Columns => SurfaceBake.GridColumns(StepDeg);
    public int Count => Rows * Columns;

    public double Latitude(int index) => SurfaceBake.RowLatitude(StepDeg, index / Columns);
    public double Longitude(int index) => SurfaceBake.ColumnLongitude(StepDeg, index % Columns);

    public string? BiomeOf(int index) => Biomes[index] is var b and >= 0 && b < BiomeNames.Count ? BiomeNames[b] : null;
}

/// <summary>A station as the survey sees it: where it is, who is aboard, and which scanners it carries.</summary>
public sealed record SurveyStation(
    string VesselId, string Name, string BodyName, SavedOrbit Orbit, IReadOnlyList<string> Engineers, bool HasSurveyScanner, bool HasNarrowBand)
{
    /// <summary>An engineer and at least one scanner aboard: the station is surveying.</summary>
    public bool IsSurveying => Engineers.Count > 0 && (HasSurveyScanner || HasNarrowBand);

    /// <summary>
    /// The vessel as a survey station: orbiting a body on a closed orbit with a scanner aboard. Null for anything else.
    /// </summary>
    public static SurveyStation? From(SavedVessel vessel, KspSave save)
    {
        if (!vessel.IsOrbiting || vessel.Orbit is not { IsClosed: true, BodyName: { } body } orbit) return null;
        var survey = vessel.Parts.Any(IsSurveyScanner);
        var narrow = vessel.Parts.Any(IsNarrowBand);
        if (!survey && !narrow) return null;
        return new SurveyStation(vessel.Id, vessel.Name, body, orbit, vessel.Crew.Where(save.IsEngineer).ToList(), survey, narrow);
    }

    /// <summary>The M700 survey scanner, or any part with its module.</summary>
    public static bool IsSurveyScanner(SavedPart part) => part.ModuleNames.Contains("ModuleOrbitalSurveyor");

    /// <summary>
    /// The M4435 narrow-band scanner, or any part that reads abundance and has KerbNet. The Surface Scanning Module reads
    /// abundance too, but only landed, and has no KerbNet.
    /// </summary>
    public static bool IsNarrowBand(SavedPart part) =>
        part.ModuleNames.Contains("ModuleResourceScanner") && part.ModuleNames.Contains("ModuleKerbNetAccess");
}

/// <summary>One reading of a surveying station (from a save the game wrote, or the live game).</summary>
public sealed record SurveyReading(double Ut, string BodyName, OrbitalElements Orbit, IReadOnlyList<string> Engineers,
    bool HasSurveyScanner, bool HasNarrowBand, bool BodyScanned)
{
    public static SurveyReading Of(SurveyStation station, double ut, bool bodyScanned) =>
        new(ut, station.BodyName, station.Orbit.ToElements(), station.Engineers, station.HasSurveyScanner, station.HasNarrowBand, bodyScanned);
}

/// <summary>
/// Which parts of the surface each survey level has covered, and when: the game time each grid point was first under a
/// station's strip. docs/SURVEY.md, docs/MATH.md §10.
/// </summary>
public sealed class SurveyCoverage
{
    /// <summary>Each pass maps this far either side of the ground track (KerbNet's narrowest view).</summary>
    public const double StripHalfWidthDeg = 5;

    /// <summary>The narrow-band scanner reads abundance up to this height.</summary>
    public const double NarrowBandMaxAltitudeM = 500_000;

    /// <summary>A biome's report comes once this much of it has been covered.</summary>
    public const double ReportFraction = 0.5;

    /// <summary>Stop following a track that has covered nothing new for this many turns of the body: its pattern repeats.</summary>
    private const int IdleRotations = 20;

    private readonly double _stepDeg;
    private readonly int _rows, _columns;

    public SurveyCoverage(double stepDeg, float[]? rough = null, float[]? estimate = null)
    {
        _stepDeg = stepDeg;
        _rows = SurfaceBake.GridRows(stepDeg);
        _columns = SurfaceBake.GridColumns(stepDeg);
        Rough = rough ?? Empty(_rows * _columns);
        Estimate = estimate ?? Empty(_rows * _columns);
    }

    /// <summary>Game time each grid point was first covered by the survey scanner; NaN where it hasn't been.</summary>
    public float[] Rough { get; }

    /// <summary>The same for the narrow-band scanner.</summary>
    public float[] Estimate { get; }

    public double StepDeg => _stepDeg;

    public float[] Of(SurveyLevel level) => level == SurveyLevel.Rough ? Rough : Estimate;

    /// <summary>Takes away everything covered after <paramref name="ut"/> (the game went back: a quickload or revert).</summary>
    public void ForgetAfter(double ut)
    {
        foreach (var grid in new[] { Rough, Estimate })
            for (var i = 0; i < grid.Length; i++)
                if (grid[i] > ut) grid[i] = float.NaN;
    }

    /// <summary>
    /// Credits the time between two readings of a station that both show it surveying the same body: the levels both
    /// readings allow, along the first reading's orbit, or, if the orbit changed, the first orbit to halfway and the second
    /// after. Returns how many grid points were newly covered.
    /// </summary>
    public int Credit(SurveyReading from, SurveyReading to, SurveyMap map, double mu)
    {
        if (to.Ut <= from.Ut || from.BodyName != to.BodyName || from.BodyName != map.BodyName) return 0;
        if (from.Engineers.Count == 0 || to.Engineers.Count == 0) return 0;
        var rough = from.HasSurveyScanner && to.HasSurveyScanner && from.BodyScanned && to.BodyScanned;
        var estimate = from.HasNarrowBand && to.HasNarrowBand;
        if (!rough && !estimate) return 0;
        if (from.Orbit == to.Orbit) return Follow(from.Orbit, from.Ut, to.Ut, map, mu, rough, estimate);
        var middle = (from.Ut + to.Ut) / 2;
        return Follow(from.Orbit, from.Ut, middle, map, mu, rough, estimate) + Follow(to.Orbit, middle, to.Ut, map, mu, rough, estimate);
    }

    /// <summary>
    /// Follows the ground track from <paramref name="fromUt"/> to <paramref name="toUt"/> in steps of at most half a grid step
    /// over the ground, marking every point within <see cref="StripHalfWidthDeg"/> of it (narrow-band only while low enough).
    /// </summary>
    public int Follow(OrbitalElements orbit, double fromUt, double toUt, SurveyMap map, double mu, bool rough, bool estimate)
    {
        var a = orbit.SemiMajorAxisM;
        var e = orbit.Eccentricity;
        if (!(a > 0) || e is < 0 or >= 1 || !(toUt > fromUt)) return 0;
        var spin = 2 * Math.PI / map.RotationPeriodS;
        var rp = a * (1 - e);
        var fastest = Math.Sqrt(mu * a * (1 - e * e)) / (rp * rp) + Math.Abs(spin); // rad/s over the ground, at periapsis
        var dt = _stepDeg / 2 * Math.PI / 180 / fastest; // half a grid step, so the strip's edges have no gaps
        var period = Kepler.OrbitalPeriod(orbit, mu);
        var idleLimit = Math.Max(IdleRotations * Math.Abs(map.RotationPeriodS), IdleRotations * period);

        var added = 0;
        var lastNew = fromUt;
        int Left(float[] grid) => grid.Count(float.IsNaN);
        var left = (rough ? Left(Rough) : 0) + (estimate ? Left(Estimate) : 0);
        for (var t = fromUt; left > added; t = Math.Min(t + dt, toUt))
        {
            var r = Kepler.StateAt(orbit, mu, t).Position;
            var length = r.Length;
            var lat = Math.Asin(Math.Clamp(r.Z / length, -1, 1)) * 180 / Math.PI;
            var lon = Wrap180(Math.Atan2(r.Y, r.X) * 180 / Math.PI - map.InitialRotationDeg - spin * t * 180 / Math.PI);
            var low = length - map.RadiusM <= NarrowBandMaxAltitudeM;
            var marked = Mark(lat, lon, (float)t, rough, estimate && low);
            if (marked > 0) { added += marked; lastNew = t; }
            else if (t - lastNew > idleLimit) break;
            if (t >= toUt) break;
        }
        return added;
    }

    /// <summary>Marks the grid points within the strip of a spot under the station; returns how many were new.</summary>
    private int Mark(double latDeg, double lonDeg, float ut, bool rough, bool estimate)
    {
        if (!rough && !estimate) return 0;
        var radius = StripHalfWidthDeg * Math.PI / 180;
        var cosLimit = Math.Cos(radius);
        var (sinLat, cosLat) = Math.SinCos(latDeg * Math.PI / 180);
        var rowLow = Math.Max(0, (int)Math.Floor((latDeg - StripHalfWidthDeg + 90) / _stepDeg));
        var rowHigh = Math.Min(_rows - 1, (int)Math.Ceiling((latDeg + StripHalfWidthDeg + 90) / _stepDeg));
        var added = 0;
        for (var row = rowLow; row <= rowHigh; row++)
        {
            var rowLat = SurfaceBake.RowLatitude(_stepDeg, row);
            var (sinRow, cosRow) = Math.SinCos(rowLat * Math.PI / 180);
            // How far east and west of the spot this row is inside the strip: cos Δλ ≥ (cos ρ − sin φ sin φ_row) / (cos φ cos φ_row).
            var denominator = cosLat * cosRow;
            var cosSpan = denominator < 1e-12 ? -1 : (cosLimit - sinLat * sinRow) / denominator;
            if (cosSpan > 1) continue;
            var spanDeg = cosSpan <= -1 ? 180 : Math.Acos(cosSpan) * 180 / Math.PI + _stepDeg; // a step more; the distance check decides
            var columns = spanDeg >= 180 ? _columns : Math.Min(_columns, (int)Math.Ceiling(spanDeg / _stepDeg) * 2 + 1);
            var first = spanDeg >= 180 ? 0 : (int)Math.Floor((lonDeg - spanDeg + 180) / _stepDeg);
            for (var k = 0; k < columns; k++)
            {
                var column = ((first + k) % _columns + _columns) % _columns;
                var colLon = SurfaceBake.ColumnLongitude(_stepDeg, column);
                var cosDistance = sinLat * sinRow + cosLat * cosRow * Math.Cos((colLon - lonDeg) * Math.PI / 180);
                if (cosDistance < cosLimit) continue;
                var i = row * _columns + column;
                if (rough && float.IsNaN(Rough[i])) { Rough[i] = ut; added++; }
                if (estimate && float.IsNaN(Estimate[i])) { Estimate[i] = ut; added++; }
            }
        }
        return added;
    }

    private static float[] Empty(int count)
    {
        var grid = new float[count];
        Array.Fill(grid, float.NaN);
        return grid;
    }

    private static double Wrap180(double deg) => ((deg + 180) % 360 + 360) % 360 - 180;
}

/// <summary>A good spot to land and mine: true abundance there (a fraction).</summary>
public sealed record SurveySpot(double LatitudeDeg, double LongitudeDeg, double Abundance);

/// <summary>What the survey says about one biome.</summary>
/// <param name="Cells">How many grid points the biome has.</param>
/// <param name="RoughCovered">Fraction of it the survey scanner has covered; likewise <paramref name="EstimateCovered"/> for the narrow-band.</param>
/// <param name="RoughUt">When the survey scanner had covered <see cref="SurveyCoverage.ReportFraction"/> of it (its Rough report); null if not yet.</param>
/// <param name="Estimate">The scanners' figure before analysis (a fraction).</param>
/// <param name="TrueAverage">The true average over the biome once analysed.</param>
public sealed record BiomeSurvey(
    string Biome, int Cells, double RoughCovered, double EstimateCovered, double? RoughUt, double? EstimateUt,
    double Estimate, bool Analysed, double? TrueAverage, IReadOnlyList<SurveySpot> Spots)
{
    /// <summary>What the survey has to say, or null when it hasn't reported on the biome.</summary>
    public SurveyLevel? Level => Analysed ? SurveyLevel.Surveyed : EstimateUt is not null ? SurveyLevel.Estimate : RoughUt is not null ? SurveyLevel.Rough : null;

    /// <summary>The report's words: "Good", "≈ 7%", "10.5% at best"…</summary>
    public string Report => Level switch
    {
        SurveyLevel.Surveyed => Spots.Count > 0 ? $"{SurveyReports.Percent(Spots[0].Abundance)} at best, {SurveyReports.Percent(TrueAverage ?? 0)} on average"
            : $"{SurveyReports.Percent(TrueAverage ?? 0)} on average; fly the narrow-band over it to find the best spots",
        SurveyLevel.Estimate => $"≈ {Math.Round(Estimate * 100):0}%",
        SurveyLevel.Rough => SurveyReports.Word(Estimate),
        _ => "",
    };
}

/// <summary>
/// A place the engineer suggests trying. Before the biome is analysed it's a spot deep inside it, so a landing there is
/// in that biome and its analysis counts; <see cref="Abundance"/> is then the biome's estimate. After, it's one of the
/// biome's richest spots and the true value there.
/// </summary>
public sealed record SurveyCandidate(string Biome, double LatitudeDeg, double LongitudeDeg, double Abundance, bool Analysed)
{
    public string Value => Analysed ? SurveyReports.Percent(Abundance) : $"≈ {Math.Round(Abundance * 100):0}%";

    /// <summary>"23.9° S 23.1° E".</summary>
    public string Position => SurveyReports.LatLon(LatitudeDeg, LongitudeDeg);
}

/// <summary>Turns a body's map and coverage into reports, per biome. docs/SURVEY.md.</summary>
public static class SurveyReports
{
    /// <summary>How many spots the engineer suggests.</summary>
    public const int MaxCandidates = 5;

    public static string LatLon(double latitudeDeg, double longitudeDeg) =>
        $"{Math.Abs(latitudeDeg):0.0}° {(latitudeDeg < 0 ? "S" : "N")} {Math.Abs(longitudeDeg):0.0}° {(longitudeDeg < 0 ? "W" : "E")}";

    /// <summary>
    /// The engineer's top spots to try, best first: an analysed biome's richest spots (true values), and for each biome
    /// the narrow-band has reported on but nobody has analysed, the spot deepest inside it (ranked by its estimate). Only
    /// points the narrow-band has covered count.
    /// </summary>
    public static IReadOnlyList<SurveyCandidate> Candidates(SurveyMap map, SurveyCoverage coverage, IReadOnlyList<BiomeSurvey> biomes)
    {
        var depth = Depths(map);
        var list = new List<SurveyCandidate>();
        foreach (var b in biomes)
        {
            if (b.Analysed)
            {
                list.AddRange(b.Spots.Select(s => new SurveyCandidate(b.Biome, s.LatitudeDeg, s.LongitudeDeg, s.Abundance, true)));
                continue;
            }
            if (b.EstimateUt is null) continue;
            var index = Array.IndexOf(map.BiomeNames.ToArray(), b.Biome);
            // Deepest inside the biome, away from the poles where a degree of longitude is short and the ground is rough.
            var best = -1;
            var bestScore = double.MinValue;
            for (var i = 0; i < map.Count; i++)
            {
                if (map.Biomes[i] != index || float.IsNaN(coverage.Estimate[i])) continue;
                var score = depth[i] * Math.Cos(map.Latitude(i) * Math.PI / 180);
                if (score > bestScore) { bestScore = score; best = i; }
            }
            if (best >= 0) list.Add(new SurveyCandidate(b.Biome, map.Latitude(best), map.Longitude(best), b.Estimate, false));
        }
        return list.OrderByDescending(c => c.Abundance).ThenBy(c => c.Analysed ? 0 : 1).Take(MaxCandidates).ToList();
    }

    /// <summary>How many grid steps each point is from the nearest point of another biome (wrapping round in longitude).</summary>
    private static int[] Depths(SurveyMap map)
    {
        int rows = map.Rows, columns = map.Columns;
        var depth = new int[map.Count];
        Array.Fill(depth, -1);
        var queue = new Queue<int>();
        for (var i = 0; i < map.Count; i++)
        {
            int r = i / columns, c = i % columns;
            foreach (var j in Neighbours(r, c))
                if (map.Biomes[j] != map.Biomes[i]) { depth[i] = 0; queue.Enqueue(i); break; }
        }
        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            foreach (var j in Neighbours(i / columns, i % columns))
                if (depth[j] < 0) { depth[j] = depth[i] + 1; queue.Enqueue(j); }
        }
        for (var i = 0; i < depth.Length; i++) if (depth[i] < 0) depth[i] = rows; // a biome covering the whole body
        return depth;

        IEnumerable<int> Neighbours(int r, int c)
        {
            if (r > 0) yield return (r - 1) * columns + c;
            if (r < rows - 1) yield return (r + 1) * columns + c;
            yield return r * columns + (c + 1) % columns;
            yield return r * columns + (c + columns - 1) % columns;
        }
    }

    /// <summary>Best spots are at least this far apart.</summary>
    public const double SpotSpacingDeg = 3;

    public const int MaxSpots = 3;

    /// <summary>Rough's words for an estimate (a fraction): Trace, Poor, Fair, Good, Rich.</summary>
    public static string Word(double estimate) => (estimate * 100) switch
    {
        < 1 => "Trace",
        < 3 => "Poor",
        < 6 => "Fair",
        < 9 => "Good",
        _ => "Rich",
    };

    public static string Percent(double fraction) => $"{fraction * 100:0.0}%";

    /// <summary>
    /// Every biome of the map: how much each level has covered and when it reported, and for analysed biomes the true
    /// average and the best spots among the points the narrow-band has covered.
    /// </summary>
    public static IReadOnlyList<BiomeSurvey> Build(SurveyMap map, SurveyCoverage coverage, string bodyName, IReadOnlySet<(string Body, string Biome)> analysed)
    {
        var cells = new List<int>[map.BiomeNames.Count];
        for (var b = 0; b < cells.Length; b++) cells[b] = [];
        for (var i = 0; i < map.Count; i++)
            if (map.Biomes[i] is var b and >= 0 && b < cells.Length) cells[b].Add(i);

        var list = new List<BiomeSurvey>();
        for (var b = 0; b < cells.Length; b++)
        {
            var points = cells[b];
            if (points.Count == 0) continue;
            var name = map.BiomeNames[b];
            var isAnalysed = analysed.Contains((bodyName, name));
            var estimate = points.Average(i => (double)map.Estimate[i]);
            var spots = isAnalysed ? BestSpots(map, points.Where(i => !float.IsNaN(coverage.Estimate[i]))) : [];
            list.Add(new BiomeSurvey(name, points.Count,
                Fraction(points, coverage.Rough), Fraction(points, coverage.Estimate),
                ReportTime(points, coverage.Rough), ReportTime(points, coverage.Estimate),
                estimate, isAnalysed, isAnalysed ? points.Average(i => (double)map.Abundance[i]) : null, spots));
        }
        return list.OrderBy(s => s.Biome, StringComparer.Ordinal).ToList();
    }

    /// <summary>The richest points, at least <see cref="SpotSpacingDeg"/> apart, most first.</summary>
    public static IReadOnlyList<SurveySpot> BestSpots(SurveyMap map, IEnumerable<int> points)
    {
        var spots = new List<SurveySpot>();
        foreach (var i in points.OrderByDescending(i => map.Abundance[i]))
        {
            var spot = new SurveySpot(map.Latitude(i), map.Longitude(i), map.Abundance[i]);
            if (spots.Any(s => Landing.SurfaceDistance(1, s.LatitudeDeg, s.LongitudeDeg, spot.LatitudeDeg, spot.LongitudeDeg) < SpotSpacingDeg * Math.PI / 180)) continue;
            spots.Add(spot);
            if (spots.Count == MaxSpots) break;
        }
        return spots;
    }

    private static double Fraction(List<int> points, float[] covered) => points.Count(i => !float.IsNaN(covered[i])) / (double)points.Count;

    /// <summary>When <see cref="SurveyCoverage.ReportFraction"/> of the points had been covered, or null if they haven't yet.</summary>
    private static double? ReportTime(List<int> points, float[] covered)
    {
        var times = points.Select(i => covered[i]).Where(t => !float.IsNaN(t)).OrderBy(t => t).ToList();
        var needed = (int)Math.Ceiling(SurveyCoverage.ReportFraction * points.Count);
        return times.Count >= needed && needed > 0 ? times[needed - 1] : null;
    }
}

/// <summary>The game's rules for an orbital survey (ModuleOrbitalSurveyor.PerformSurvey). docs/SURVEY.md.</summary>
public static class OrbitalSurveyRules
{
    /// <summary>Lowest periapsis and highest apoapsis allowed above the surface, for a body of radius <paramref name="radiusM"/>.</summary>
    public static (double MinPeriapsisM, double MaxApoapsisM) Band(double radiusM) =>
        (Math.Max(radiusM / 10, 25_000), Math.Min(radiusM * 5, 1_500_000));

    public const double MinInclinationDeg = 80;

    /// <summary>
    /// Everything stopping this craft's orbital survey (empty when it can run, as far as the save tells). The game needs no
    /// crew for it, so a probe can do it.
    /// </summary>
    public static IReadOnlyList<string> Problems(SurveyStation station, CelestialBody body)
    {
        var problems = new List<string>();
        if (!station.HasSurveyScanner) problems.Add("The craft needs a survey scanner (M700).");
        var o = station.Orbit;
        var (minPe, maxAp) = Band(body.EquatorialRadiusM);
        var pe = o.SemiMajorAxisM * (1 - o.Eccentricity) - body.EquatorialRadiusM;
        var ap = o.SemiMajorAxisM * (1 + o.Eccentricity) - body.EquatorialRadiusM;
        // The game asks for at least 80°, so retrograde orbits up to 180° pass too.
        if (o.InclinationDeg < MinInclinationDeg)
            problems.Add($"The orbit must be polar: inclination at least {MinInclinationDeg:0}° (it's {o.InclinationDeg:0.#}°).");
        if (pe <= minPe || ap >= maxAp)
            problems.Add($"The orbit must stay between {minPe / 1000:0} km and {maxAp / 1000:0} km above {body.Name} (it's {pe / 1000:0.#} km × {ap / 1000:0.#} km).");
        return problems;
    }
}
