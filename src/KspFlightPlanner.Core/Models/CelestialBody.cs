namespace KspFlightPlanner.Core.Models;

public sealed record StarSystem(long Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Constant physical data for a body. Distances in metres, times in seconds.
/// </summary>
public sealed record CelestialBody
{
    public long Id { get; init; }
    public long SystemId { get; init; }
    public required string Name { get; init; }
    public double MassKg { get; init; }

    /// <summary>Standard gravitational parameter μ = GM (m³/s²).</summary>
    public double Mu { get; init; }

    public double EquatorialRadiusM { get; init; }

    /// <summary>Sidereal rotation period. Negative means retrograde rotation.</summary>
    public double RotationPeriodS { get; init; }

    /// <summary>Height of the top of the atmosphere above the surface; 0 if airless.</summary>
    public double AtmosphereHeightM { get; init; }

    /// <summary>Sphere-of-influence radius measured from the body's centre.</summary>
    public double SoiRadiusM { get; init; }

    public string Color { get; init; } = "#666666";
    public bool IsDefault { get; init; }

    public bool HasAtmosphere => AtmosphereHeightM > 0;

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

    public override string ToString() => Name;
}
