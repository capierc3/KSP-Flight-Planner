namespace KspFlightPlanner.Core.Ksp;

/// <summary>A vessel as the running game reports it (through kRPC). Same type and situation names as the save.</summary>
public sealed record LiveVessel(string Name, string Type, string Situation, SavedOrbit? Orbit)
{
    /// <summary>The vessel the player is flying.</summary>
    public bool IsActive { get; init; }

    /// <summary>The vessel the player has set as target.</summary>
    public bool IsTarget { get; init; }

    public bool IsOrbiting => Situation == "ORBITING";
}

/// <summary>A maneuver node on the vessel being flown.</summary>
public sealed record LiveNode(double Ut, double Prograde, double Normal, double Radial);

/// <summary>What the running game reported at one moment.</summary>
/// <param name="Ut">Game time, in seconds since Year 1, Day 1.</param>
/// <param name="Vessels">Every vessel with its current orbit; null outside the flight scene, where kRPC can't list them.</param>
/// <param name="TargetBody">The planet or moon set as target, if the target is a body.</param>
public sealed record LiveGame(double Ut, IReadOnlyList<LiveVessel>? Vessels, string? TargetBody = null)
{
    /// <summary>Maneuver nodes on the vessel being flown (empty outside flight).</summary>
    public IReadOnlyList<LiveNode> ActiveNodes { get; init; } = [];

    public LiveVessel? Active => Vessels?.FirstOrDefault(v => v.IsActive);
    public LiveVessel? Target => Vessels?.FirstOrDefault(v => v.IsTarget);
}

/// <summary>The save with the game's live time and orbits laid over it.</summary>
/// <param name="SaveIds">The save vessel each live vessel was matched to.</param>
/// <param name="Unmatched">Live vessels not in the save (launched or undocked since KSP last wrote it).</param>
public sealed record LiveMerge(KspSave Save, IReadOnlyDictionary<LiveVessel, string> SaveIds, IReadOnlyList<LiveVessel> Unmatched)
{
    /// <summary>
    /// Lays <paramref name="live"/> over the save read from disk: the live game time, and each matched vessel's live
    /// situation and orbit. kRPC has no vessel ids, so vessels are matched by name (and type), ties going to the closest
    /// orbit. Where <paramref name="previous"/> already holds an orbit that's the same path as the live one, that
    /// object is kept, so a coasting vessel doesn't look changed just because the game restated its epoch.
    /// </summary>
    /// <param name="mu">Gravitational parameter of a body by name (to compare orbits stated at different epochs).</param>
    public static LiveMerge Apply(KspSave file, LiveGame live, KspSave? previous, Func<string, double?> mu)
    {
        if (live.Vessels is null) return new LiveMerge(file with { Ut = live.Ut }, new Dictionary<LiveVessel, string>(ReferenceEqualityComparer.Instance), []);

        var saveIds = Match(file.Vessels, live.Vessels);
        var liveById = saveIds.ToDictionary(p => p.Value, p => p.Key);
        var before = previous?.Vessels.Where(v => v.Id.Length > 0).GroupBy(v => v.Id).ToDictionary(g => g.Key, g => g.First());

        var vessels = file.Vessels.Select(v =>
        {
            if (!liveById.TryGetValue(v.Id, out var lv)) return v;
            var orbit = lv.Orbit;
            if (orbit is not null && before?.GetValueOrDefault(v.Id)?.Orbit is { } kept && SamePath(kept, orbit, live.Ut, mu)) orbit = kept;
            return v with { Situation = lv.Situation, Orbit = orbit ?? v.Orbit };
        }).ToList();

        var unmatched = live.Vessels.Where(v => !saveIds.ContainsKey(v)).ToList();
        return new LiveMerge(file with { Ut = live.Ut, Vessels = vessels }, saveIds, unmatched);
    }

    /// <summary>Pairs live vessels with save vessels: same name, preferring the same type, then the closest orbit.</summary>
    private static Dictionary<LiveVessel, string> Match(IReadOnlyList<SavedVessel> saved, IReadOnlyList<LiveVessel> live)
    {
        var pairs = new List<(LiveVessel Live, SavedVessel Saved, double Score)>();
        foreach (var lv in live)
            foreach (var sv in saved)
                if (sv.Id.Length > 0 && sv.Name == lv.Name)
                    pairs.Add((lv, sv, (sv.Type == lv.Type ? 0 : 100) + OrbitDistance(sv.Orbit, lv.Orbit)));

        var result = new Dictionary<LiveVessel, string>(ReferenceEqualityComparer.Instance); // two live vessels can look alike
        var taken = new HashSet<string>();
        foreach (var (lv, sv, _) in pairs.OrderBy(p => p.Score))
            if (!result.ContainsKey(lv) && taken.Add(sv.Id)) result[lv] = sv.Id;
        return result;
    }

    /// <summary>A rough difference between two orbits for breaking name ties: other body, size, shape and tilt.</summary>
    private static double OrbitDistance(SavedOrbit? a, SavedOrbit? b)
    {
        if (a is null || b is null) return 50;
        if (a.BodyName != b.BodyName) return 10;
        var size = a.SemiMajorAxisM > 0 && b.SemiMajorAxisM > 0 ? Math.Abs(Math.Log(Math.Abs(a.SemiMajorAxisM / b.SemiMajorAxisM))) : 1;
        return size + Math.Abs(a.Eccentricity - b.Eccentricity) + Math.Abs(a.InclinationDeg - b.InclinationDeg) / 180;
    }

    /// <summary>
    /// Whether two statements of an orbit describe the same path with the craft in the same place at <paramref name="ut"/>
    /// (to within about 10 m per 1,000 km, above the jitter of a coasting craft under physics). Angles that are undefined for circular or equatorial orbits are compared as
    /// the combinations that are defined.
    /// </summary>
    public static bool SamePath(SavedOrbit a, SavedOrbit b, double ut, Func<string, double?> mu)
    {
        const double tolerance = 1e-5; // relative size, eccentricity, and radians
        if (a.BodyName != b.BodyName || a.BodyName is not { } body || mu(body) is not { } m) return false;
        if (Math.Abs(a.SemiMajorAxisM - b.SemiMajorAxisM) > tolerance * Math.Abs(a.SemiMajorAxisM)) return false;
        if (Math.Abs(a.Eccentricity - b.Eccentricity) > tolerance) return false;
        if (Angle(a.InclinationDeg * DegToRad, b.InclinationDeg * DegToRad) > tolerance) return false;
        if (a.Eccentricity >= 1) return false; // escaping: not worth the special cases, always treat as changed

        // Longitude of periapsis and mean longitude stay defined when the orbit is circular or equatorial.
        var equatorial = Math.Sin(a.InclinationDeg * DegToRad) < tolerance;
        var circular = a.Eccentricity < tolerance;
        if (!equatorial && Angle(a.LanDeg * DegToRad, b.LanDeg * DegToRad) > tolerance) return false;
        var pa = (a.LanDeg + a.ArgumentOfPeriapsisDeg) * DegToRad;
        var pb = (b.LanDeg + b.ArgumentOfPeriapsisDeg) * DegToRad;
        if (!circular && Angle(pa, pb) > tolerance) return false;
        return Angle(pa + MeanAnomalyAt(a, ut, m), pb + MeanAnomalyAt(b, ut, m)) <= tolerance;
    }

    private static double MeanAnomalyAt(SavedOrbit o, double ut, double mu) =>
        o.MeanAnomalyRad + Math.Sqrt(mu / Math.Pow(o.SemiMajorAxisM, 3)) * (ut - o.EpochUt);

    /// <summary>Smallest difference between two angles in radians.</summary>
    private static double Angle(double a, double b) => Math.Abs(Math.IEEERemainder(a - b, 2 * Math.PI));

    private const double DegToRad = Math.PI / 180;
}
