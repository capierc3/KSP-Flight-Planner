using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core;

/// <summary>Body-radius multipliers used when checking whether a body blocks line of sight.</summary>
public sealed record OcclusionSettings(bool Enabled, double AtmosphericBody = 0.75, double VacuumBody = 0.9)
{
    public static readonly OcclusionSettings Disabled = new(false);

    public double ModifierFor(CelestialBody body) =>
        !Enabled ? 1.0 : body.HasAtmosphere ? AtmosphericBody : VacuumBody;
}

public sealed record ResonantOrbitRequest(
    CelestialBody Body,
    int SatelliteCount,
    double AltitudeM,
    bool PreferDiveOrbit = false,
    OcclusionSettings? Occlusion = null);

public sealed record ResonantOrbitResult
{
    public double SatellitePeriodS { get; init; }
    public double SatelliteRadiusM { get; init; }

    /// <summary>True when the carrier dips below the target orbit (period (n−1)/n) instead of rising above it.</summary>
    public bool IsDiveOrbit { get; init; }
    public double CarrierPeriodS { get; init; }
    public double CarrierSemiMajorAxisM { get; init; }
    public double CarrierApoapsisAltitudeM { get; init; }
    public double CarrierPeriapsisAltitudeM { get; init; }
    public double CarrierEccentricity { get; init; }

    /// <summary>Δv to circularise each satellite from the carrier orbit at the shared apsis.</summary>
    public double InjectionDeltaV { get; init; }

    public double? MinimumLineOfSightAltitudeM { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Computes the carrier orbit that releases <c>n</c> satellites into evenly spaced slots of a
/// shared circular orbit, one per carrier revolution.
/// </summary>
public static class ResonantOrbitCalculator
{
    public static ResonantOrbitResult Calculate(ResonantOrbitRequest request)
    {
        var (body, n, altitude, preferDive, occlusion) = request;
        if (n < 1) throw new ArgumentOutOfRangeException(nameof(request), "At least one satellite is required.");
        if (altitude <= 0) throw new ArgumentOutOfRangeException(nameof(request), "Altitude must be above the surface.");
        if (altitude >= body.SoiAltitudeM) throw new ArgumentOutOfRangeException(nameof(request), "Altitude is outside the sphere of influence.");

        var mu = body.Mu;
        var r = body.EquatorialRadiusM + altitude;
        var satPeriod = OrbitMath.Period(mu, r);
        var warnings = new List<string>();

        // Carrier period chosen so each revolution the release point drifts by one slot (1/n of the orbit).
        // Resonant: T·(n+1)/n — periapsis at r, apoapsis raised. Dive: T·(n−1)/n — apoapsis at r, periapsis lowered.
        var dive = preferDive && n >= 2;
        var (carrierPeriod, a, otherApsisRadius) = Carrier(mu, r, satPeriod, n, dive);

        if (!dive && otherApsisRadius - body.EquatorialRadiusM > body.SoiAltitudeM)
        {
            if (n < 2) throw new InvalidOperationException("Carrier apoapsis would leave the sphere of influence.");
            warnings.Add("Resonant apoapsis would leave the sphere of influence; using a dive orbit instead.");
            dive = true;
            (carrierPeriod, a, otherApsisRadius) = Carrier(mu, r, satPeriod, n, dive);
        }

        var apR = dive ? r : otherApsisRadius;
        var peR = dive ? otherApsisRadius : r;
        var apAlt = apR - body.EquatorialRadiusM;
        var peAlt = peR - body.EquatorialRadiusM;

        var deltaV = Math.Abs(OrbitMath.CircularVelocity(mu, r) - OrbitMath.VisViva(mu, r, a));

        var minLos = OrbitMath.MinimumLineOfSightAltitude(
            body.EquatorialRadiusM, n, (occlusion ?? OcclusionSettings.Disabled).ModifierFor(body));

        if (altitude < body.AtmosphereHeightM)
            warnings.Add("Target orbit is inside the atmosphere.");
        if (peAlt <= 0)
            warnings.Add("Carrier periapsis is below the surface.");
        else if (peAlt < body.AtmosphereHeightM)
            warnings.Add("Carrier periapsis is inside the atmosphere.");
        if (minLos is { } los && altitude * 1.01 < los)
            warnings.Add("Satellites at this altitude will not have line of sight to their neighbours.");

        return new ResonantOrbitResult
        {
            SatellitePeriodS = satPeriod,
            SatelliteRadiusM = r,
            IsDiveOrbit = dive,
            CarrierPeriodS = carrierPeriod,
            CarrierSemiMajorAxisM = a,
            CarrierApoapsisAltitudeM = apAlt,
            CarrierPeriapsisAltitudeM = peAlt,
            CarrierEccentricity = OrbitMath.Eccentricity(peR, apR),
            InjectionDeltaV = deltaV,
            MinimumLineOfSightAltitudeM = minLos,
            Warnings = warnings,
        };
    }

    private static (double Period, double SemiMajorAxis, double OtherApsisRadius) Carrier(
        double mu, double r, double satPeriod, int n, bool dive)
    {
        var ratio = dive ? (n - 1.0) / n : (n + 1.0) / n;
        var period = satPeriod * ratio;
        var a = OrbitMath.SemiMajorAxisFromPeriod(mu, period);
        return (period, a, 2 * a - r);
    }
}
