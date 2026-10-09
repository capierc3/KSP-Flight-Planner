using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core;

/// <summary>
/// The launch leg tool on the stop a launch arrives at: launch into an orbit inclined <paramref name="InclinationDeg"/>
/// (0° equatorial, 90° polar, 180° retrograde equatorial) instead of due east.
/// </summary>
public sealed record LaunchLeg(double InclinationDeg);

/// <summary>
/// A launch into an inclined circular orbit, worked out as in docs/MATH.md §2.7. Angles in degrees, speeds in m/s.
/// </summary>
/// <param name="SiteLatitudeDeg">The launch site's latitude φ (north positive).</param>
/// <param name="FlownInclinationDeg">The inclination the launch itself reaches: <paramref name="InclinationDeg"/> held to
/// |φ| … 180° − |φ|, the ones a launch from this latitude can reach.</param>
/// <param name="OrbitSpeed">Circular speed v at the orbit the launch goes into.</param>
/// <param name="SurfaceSpeed">How fast the site moves east with the body's spin, v_rot.</param>
/// <param name="AzimuthDeg">Direction of the orbit's velocity at the site on the northbound pass, from north (east 90°,
/// retrograde orbits head west of north); the southbound pass is 180° minus it.</param>
/// <param name="RelativeSpeed">The speed the rocket has to add to what the spin gives it, v_rel.</param>
/// <param name="LaunchDv">How much more that is than a launch due east, v_rel − (v − v_rot).</param>
/// <param name="TrimDv">The plane change in orbit for the part of the inclination a launch can't reach (§2.3); 0 when it can.</param>
/// <param name="NorthHeadingDeg">Navball heading to fly on a northbound launch (the spin already taken off).</param>
/// <param name="SouthHeadingDeg">Navball heading to fly on a southbound launch.</param>
public sealed record LaunchPlan(
    CelestialBody Body, string SiteName, double SiteLatitudeDeg, double OrbitAltitudeM,
    double InclinationDeg, double FlownInclinationDeg,
    double OrbitSpeed, double SurfaceSpeed, double AzimuthDeg, double RelativeSpeed,
    double LaunchDv, double TrimDv, double NorthHeadingDeg, double SouthHeadingDeg)
{
    /// <summary>What the launch costs on top of the Δv map's (due-east) value.</summary>
    public double ExtraDv => LaunchDv + TrimDv;

    /// <summary>A launch from the site goes straight into the inclination, with no plane change after.</summary>
    public bool IsReachable => TrimDv == 0;

    /// <summary>
    /// Northbound and southbound are the same launch: due east (or due west) at the lowest (or highest) inclination
    /// the site reaches, where the orbit just touches the site's latitude.
    /// </summary>
    public bool HasOneHeading => Math.Abs(NorthHeadingDeg - SouthHeadingDeg) < 0.05;

    /// <summary>"355.6° north or 184.4° south", or "270° (due west)" when both are the same.</summary>
    public string HeadingText => HasOneHeading
        ? $"{NorthHeadingDeg:0.#}° (due {(NorthHeadingDeg > 180 ? "west" : "east")})"
        : $"{NorthHeadingDeg:0.#}° north or {SouthHeadingDeg:0.#}° south";

    /// <summary>The lowest inclination a launch from the site reaches directly (|φ|).</summary>
    public double MinInclinationDeg => Math.Abs(SiteLatitudeDeg);
}

public static class LaunchInclination
{
    /// <summary>The launch site a launch from <see cref="LaunchSite.Ksc"/>'s body flies from (a wiki landmark).</summary>
    public const string DefaultSiteName = "KSC Launch Pad";

    /// <summary>
    /// Where launches from <paramref name="body"/> lift off: its "KSC Launch Pad" landmark, or its first launch site;
    /// null for a body with none (only Kerbin has them in the stock game).
    /// </summary>
    public static Landmark? Site(CelestialBody body) =>
        body.Landmarks.FirstOrDefault(m => m.Name == DefaultSiteName)
        ?? body.Landmarks.FirstOrDefault(m => m.Kind == "launch-site");

    /// <summary>A launch from <paramref name="body"/>'s launch site (<see cref="Site"/>), or null when it has none.</summary>
    public static LaunchPlan? Plan(CelestialBody body, double orbitAltitudeM, double inclinationDeg) =>
        Site(body) is { } site ? Plan(body, site.Name, site.LatitudeDeg, orbitAltitudeM, inclinationDeg) : null;

    /// <summary>
    /// A launch from latitude <paramref name="siteLatitudeDeg"/> into a circular orbit <paramref name="orbitAltitudeM"/>
    /// up, inclined <paramref name="inclinationDeg"/> (0–180°), §2.7.
    /// </summary>
    public static LaunchPlan Plan(CelestialBody body, string siteName, double siteLatitudeDeg, double orbitAltitudeM, double inclinationDeg)
    {
        if (inclinationDeg is < 0 or > 180) throw new ArgumentOutOfRangeException(nameof(inclinationDeg), "Inclination must be between 0° and 180°.");
        var phi = Rad(siteLatitudeDeg);
        var v = OrbitMath.CircularVelocity(body.Mu, body.EquatorialRadiusM + orbitAltitudeM);
        var vRot = body.RotationPeriodS == 0 ? 0 : 2 * Math.PI * body.EquatorialRadiusM * Math.Cos(phi) / body.RotationPeriodS;

        // A launch reaches |φ| … 180° − |φ|; the rest is a plane change once in orbit.
        var minInc = Math.Abs(siteLatitudeDeg);
        var flown = Math.Clamp(inclinationDeg, minInc, 180 - minInc);
        var trim = OrbitMath.PlaneChangeDv(v, Math.Abs(inclinationDeg - flown));

        // sin(az) = cos i / cos φ: how much of the orbit's velocity points east at the site (cos β, β from east).
        var cosBeta = Math.Clamp(Math.Cos(Rad(flown)) / Math.Cos(phi), -1, 1);
        var azimuth = Deg(Math.Asin(cosBeta));
        var vRel = Math.Sqrt(Math.Max(0, v * v + vRot * vRot - 2 * v * vRot * cosBeta));
        var launch = vRel - (v - vRot);

        double Heading(double az) => Rendezvous.Wrap360(Deg(Math.Atan2(v * Math.Sin(Rad(az)) - vRot, v * Math.Cos(Rad(az)))));
        return new LaunchPlan(body, siteName, siteLatitudeDeg, orbitAltitudeM, inclinationDeg, flown, v, vRot,
            Rendezvous.Wrap360(azimuth), vRel, launch, trim, Heading(azimuth), Heading(180 - azimuth));
    }

    private static double Rad(double deg) => deg * Math.PI / 180;
    private static double Deg(double rad) => rad * 180 / Math.PI;
}
