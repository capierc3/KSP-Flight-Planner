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

public sealed record Atmosphere(
    double HeightM,
    double? SeaLevelPressureAtm = null,
    double? ScaleHeightM = null,
    double? MinTempC = null,
    double? MaxTempC = null,
    bool? HasOxygen = null)
{
    public const double KilopascalsPerAtm = 101.325;

    /// <summary>Pressure at an altitude using the exponential scale-height model (approximation of KSP's curves).</summary>
    public double? PressureAtmAt(double altitudeM) =>
        altitudeM >= HeightM ? 0
        : SeaLevelPressureAtm is { } p0 && ScaleHeightM is { } h ? p0 * Math.Exp(-altitudeM / h)
        : null;
}

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

    /// <summary>Approximate Δv from the surface to low orbit (community Δv map), when known.</summary>
    public double? SurfaceToLowOrbitDv { get; init; }

    /// <summary>Orbit around the parent body; null for the central star or bodies without data.</summary>
    public OrbitalElements? Orbit { get; init; }

    public Atmosphere? Atmosphere { get; init; }

    public string Color { get; init; } = "#666666";
    public bool IsDefault { get; init; }

    public bool HasAtmosphere => Atmosphere is { HeightM: > 0 };

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
