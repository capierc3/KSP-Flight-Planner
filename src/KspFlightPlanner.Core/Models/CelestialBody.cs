namespace KspFlightPlanner.Core.Models;

public sealed record StarSystem(long Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Keplerian elements of a body's orbit around its parent. Epoch is UT 0 for stock KSP.</summary>
public sealed record OrbitalElements(
    double SemiMajorAxisM,
    double Eccentricity,
    double InclinationDeg,
    double LongitudeOfAscendingNodeDeg,
    double ArgumentOfPeriapsisDeg,
    double MeanAnomalyAtEpochRad,
    double EpochS = 0);

/// <summary>One row of a body's pressure-vs-altitude table (KSP wiki). Density is only known for Kerbin.</summary>
public sealed record AtmospherePoint(double AltitudeM, double PressurePa, double? DensityKgM3 = null);

public sealed record Atmosphere(
    double HeightM,
    double? SeaLevelPressureAtm = null,
    double? ScaleHeightM = null,
    double? MinTempC = null,
    double? MaxTempC = null,
    bool? HasOxygen = null)
{
    public const double KilopascalsPerAtm = 101.325;
    public const double PascalsPerAtm = 101_325;

    /// <summary>Average molar mass of the air (g/mol), when known.</summary>
    public double? MolarMassGPerMol { get; init; }

    /// <summary>Ratio of specific heats γ, used for the speed of sound, when known.</summary>
    public double? AdiabaticIndex { get; init; }

    /// <summary>Sea-level temperature at the equator: night low and day high (°C), when known.</summary>
    public (double Night, double Day)? EquatorTempC { get; init; }

    /// <summary>Sea-level temperature at the poles: low and high (°C), when known.</summary>
    public (double Low, double High)? PoleTempC { get; init; }

    /// <summary>Pressure at fixed altitudes, ascending. Empty when only the scale height is known.</summary>
    public IReadOnlyList<AtmospherePoint> PressureTable { get; init; } = [];

    /// <summary>
    /// Pressure at an altitude. With a pressure table: log-linear between rows (linear into a 0 Pa row);
    /// otherwise the exponential scale-height model. See docs/MATH.md, "Atmospheric pressure".
    /// </summary>
    public double? PressureAtmAt(double altitudeM)
    {
        if (altitudeM >= HeightM) return 0;
        if (PressureTable.Count >= 2) return TablePressurePa(Math.Max(0, altitudeM)) / PascalsPerAtm;
        return SeaLevelPressureAtm is { } p0 && ScaleHeightM is { } h ? p0 * Math.Exp(-altitudeM / h) : null;
    }

    private double TablePressurePa(double altitudeM)
    {
        var t = PressureTable;
        var i = 1;
        while (i < t.Count - 1 && t[i].AltitudeM < altitudeM) i++;
        var (lo, hi) = (t[i - 1], t[i]);
        var f = Math.Clamp((altitudeM - lo.AltitudeM) / (hi.AltitudeM - lo.AltitudeM), 0, 1);
        if (lo.PressurePa <= 0 || hi.PressurePa <= 0) return lo.PressurePa + f * (hi.PressurePa - lo.PressurePa);
        return lo.PressurePa * Math.Pow(hi.PressurePa / lo.PressurePa, f);
    }
}

/// <summary>A named spot on a body's surface (launch site, anomaly, feature). Latitude north and longitude east positive.</summary>
public sealed record Landmark(string Name, string Kind, double LatitudeDeg, double LongitudeDeg, string? Note = null, string? Source = null);

/// <summary>
/// The body's science multipliers per situation (KSP wiki), plus the altitudes where "flying high" and
/// "in space high" begin. Null entries don't apply to the body (e.g. splashed on a body without oceans).
/// </summary>
public sealed record ScienceMultipliers(
    double? Landed, double? Splashed, double? FlyingLow, double? FlyingHigh,
    double? SpaceLow, double? SpaceHigh, double? Recovery,
    double? FlyingHighFromM, double? SpaceHighFromM);

/// <summary>
/// Constant physical data for a body. Distances in metres, times in seconds.
/// </summary>
public sealed record CelestialBody
{
    public long Id { get; init; }
    public long SystemId { get; init; }
    public required string Name { get; init; }

    /// <summary>Star, Planet, Dwarf planet or Moon (null when unknown).</summary>
    public string? BodyClass { get; init; }
    public long? ParentId { get; init; }

    public double MassKg { get; init; }

    /// <summary>Standard gravitational parameter μ = GM (m³/s²).</summary>
    public double Mu { get; init; }

    public double EquatorialRadiusM { get; init; }

    /// <summary>Sidereal rotation period. Negative means retrograde rotation.</summary>
    public double RotationPeriodS { get; init; }

    /// <summary>Sphere-of-influence radius measured from the body's centre.</summary>
    public double SoiRadiusM { get; init; }

    public double? HighestElevationM { get; init; }

    /// <summary>Estimated Δv from the surface to <see cref="DefaultParkingAltitudeM"/>; null for stars (see <see cref="DeltaVMapGenerator"/>).</summary>
    public double? SurfaceToLowOrbitDv => DeltaVMapGenerator.SurfaceToLowOrbitDv(this);

    /// <summary>Orbit around the parent body; null for the central star or bodies without data.</summary>
    public OrbitalElements? Orbit { get; init; }

    public Atmosphere? Atmosphere { get; init; }

    /// <summary>Biome names (stock bodies; from the KSP wiki).</summary>
    public IReadOnlyList<string> Biomes { get; init; } = [];

    /// <summary>Known surface spots: launch sites, anomalies, highest point (stock bodies; from the KSP wiki).</summary>
    public IReadOnlyList<Landmark> Landmarks { get; init; } = [];

    public ScienceMultipliers? Science { get; init; }

    /// <summary>
    /// When the player's game has been sampled (Tools › Bake Surface Data): each value compared with the wiki and which
    /// one is used (see <see cref="Ksp.BakeMerge"/>). Empty without a bake.
    /// </summary>
    public IReadOnlyList<Ksp.DataCheck> DataChecks { get; init; } = [];

    /// <summary>When the bake the values above came from was taken; null without one.</summary>
    public DateTime? BakedUtc { get; init; }

    public string Color { get; init; } = "#666666";
    public bool IsDefault { get; init; }

    public bool HasAtmosphere => Atmosphere is { HeightM: > 0 };

    /// <summary>A stand-in for a user's custom location (no gravity; see CustomLocationOrbits.ToBody).</summary>
    public bool IsCustomLocation => BodyClass == CustomLocationOrbits.BodyClassName;

    /// <summary>Height of the top of the atmosphere above the surface; 0 if airless.</summary>
    public double AtmosphereHeightM => Atmosphere?.HeightM ?? 0;

    /// <summary>Surface gravity g = μ/R² (m/s²).</summary>
    public double SurfaceGravity => Mu / (EquatorialRadiusM * EquatorialRadiusM);

    public double SoiAltitudeM => SoiRadiusM - EquatorialRadiusM;

    /// <summary>Radius of the synchronous orbit, or null if the body doesn't rotate.</summary>
    public double? SynchronousRadiusM =>
        RotationPeriodS == 0 ? null : OrbitMath.SemiMajorAxisFromPeriod(Mu, Math.Abs(RotationPeriodS));

    /// <summary>Synchronous orbit altitude, or null when it lies below the surface or outside the SOI.</summary>
    public double? SynchronousAltitudeM
    {
        get
        {
            if (SynchronousRadiusM is not { } r) return null;
            var alt = r - EquatorialRadiusM;
            return alt > 0 && r < SoiRadiusM ? alt : null;
        }
    }

    /// <summary>
    /// A safe low parking-orbit altitude: 10 km above the atmosphere, or ~2 km above the highest
    /// terrain (minimum 10 km). When the terrain height is unknown, 10% of the radius is used.
    /// </summary>
    public double DefaultParkingAltitudeM
    {
        get
        {
            var alt = HasAtmosphere ? AtmosphereHeightM + 10_000
                : HighestElevationM is { } peak ? peak + 2_000
                : 0.1 * EquatorialRadiusM;
            return Math.Ceiling(Math.Max(10_000, alt) / 1_000) * 1_000;
        }
    }

    public override string ToString() => Name;
}
