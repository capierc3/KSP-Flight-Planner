using System.Collections.Concurrent;
using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core;

/// <summary>
/// The arrival leg tool on a stop in orbit of another body: arrive in an orbit inclined <paramref name="InclinationDeg"/>
/// to that body's equator (0° equatorial, 90° polar, 180° retrograde equatorial) instead of however the trip comes in.
/// </summary>
public sealed record ArrivalLeg(double InclinationDeg);

/// <summary>
/// What arriving inclined costs: <paramref name="AimDv"/> on top of the trip, flown on the way (§3.9), charged on leg
/// <paramref name="Leg"/> of the segment (the one that crosses into <paramref name="Body"/>'s sphere). Null Δv when it
/// couldn't be worked out. <paramref name="IsEstimate"/> when it comes from the rule of thumb rather than a planned burn.
/// </summary>
public sealed record ArrivalPlan(CelestialBody Body, double InclinationDeg, double? AimDv, int Leg, bool IsEstimate);

/// <summary>
/// The extra Δv to arrive at a body in a chosen inclination, for Plan mode (no real orbits or dates):
/// <list type="bullet">
/// <item>From a planet to its moon, or a moon to its planet: both trips planned with <see cref="Hops"/> from a round
/// equatorial orbit (the map's low orbit), with and without the inclination; the difference.</item>
/// <item>Between planets: the rule of thumb Δv ≈ 2·b·sin(Δi/2) / t, moving the aim point b round the line of approach
/// with the course correction t before arriving (docs/MATH.md §3.9).</item>
/// </list>
/// Results are kept, since planning a hop takes a moment.
/// </summary>
public static class ArrivalTilt
{
    private static readonly ConcurrentDictionary<(HopKind, string, string, long, long, long), (double? Dv, bool Estimate)> Kept = new();

    public static ArrivalPlan Plan(HopKind kind, CelestialBody from, CelestialBody to, CelestialBody? parent, double parkingAltitudeM,
        double altitudeM, double inclinationDeg, int leg)
    {
        var key = (kind, from.Name, to.Name, (long)Math.Round(parkingAltitudeM), (long)Math.Round(altitudeM), (long)Math.Round(inclinationDeg * 100));
        var (dv, estimate) = Kept.GetOrAdd(key, _ => Work(kind, from, to, parent, parkingAltitudeM, altitudeM, inclinationDeg));
        return new ArrivalPlan(to, inclinationDeg, dv, leg, estimate);
    }

    private static (double? Dv, bool Estimate) Work(HopKind kind, CelestialBody from, CelestialBody to, CelestialBody? parent, double parkingAltitudeM,
        double altitudeM, double inclinationDeg)
    {
        if (kind == HopKind.Across)
            return parent is null || from.Orbit is null || to.Orbit is null ? (null, true) : (RuleOfThumb(from, to, parent, altitudeM, inclinationDeg), true);
        var craft = new OrbitalElements(from.EquatorialRadiusM + parkingAltitudeM, 0, 0, 0, 0, 0, 0);
        try
        {
            HopPlan Hop(double? tilt) => kind == HopKind.Down
                ? Hops.Down(from, to, craft, 0, altitudeM, soonest: true, inclinationDeg: tilt)
                : Hops.Up(from, to, craft, 0, altitudeM, tilt);
            double Total(HopPlan p) => p.Burns.Sum(b => b.DeltaV);
            return (Math.Max(0, Total(Hop(inclinationDeg)) - Total(Hop(null))), false);
        }
        catch (ArgumentException)
        {
            return (null, false);
        }
    }

    /// <summary>
    /// Δv ≈ 2·b·sin(Δi/2) / t for a trip between planets round <paramref name="star"/>: the approach comes in at the Hohmann
    /// transfer's v∞ (§3.1) about in the destination's equator, so swinging the aim point round the line of approach by
    /// Δi tilts the arrival by Δi. The aim point is b = r_p·√(1 + 2μ/(r_p·v∞²)) from the line through the body's centre
    /// (gravity bends the path in, so it's wider than r_p), and a sideways burn t before arriving moves it about Δv·t:
    /// t is the coast after the course correction, (1 − <see cref="Hops.CorrectionAt"/>) of the transfer.
    /// </summary>
    public static double RuleOfThumb(CelestialBody from, CelestialBody to, CelestialBody star, double altitudeM, double inclinationDeg)
    {
        var (r1, r2) = (from.Orbit!.SemiMajorAxisM, to.Orbit!.SemiMajorAxisM);
        var transfer = (r1 + r2) / 2;
        var vInf = Math.Abs(OrbitMath.CircularVelocity(star.Mu, r2) - OrbitMath.VisViva(star.Mu, r2, transfer));
        var t = (1 - Hops.CorrectionAt) * OrbitMath.Period(star.Mu, transfer) / 2;
        var rp = to.EquatorialRadiusM + altitudeM;
        var b = rp * Math.Sqrt(1 + 2 * to.Mu / (rp * vInf * vInf));
        return 2 * b * Math.Sin(inclinationDeg * Math.PI / 360) / t;
    }
}
