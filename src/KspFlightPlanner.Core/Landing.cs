using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core;

/// <summary>
/// A spot to land at. <paramref name="Source"/> says where it came from ("Flag", "Landed craft", "KerbNet waypoint",
/// "Landmark", "Entered"). <paramref name="GroundHeightM"/> is the ground's height above the datum when known
/// (flags and landed craft record it; wiki landmarks mostly don't).
/// </summary>
public sealed record LandingTarget(string Name, string Source, double LatitudeDeg, double LongitudeDeg, double? GroundHeightM = null)
{
    public override string ToString() => $"{Name} ({Source})";
}

/// <summary>
/// The landing leg tool on a surface stop: land at <paramref name="Target"/> with a lander whose thrust-to-weight ratio
/// (at the body's surface) is <paramref name="LanderTwr"/>.
/// </summary>
public sealed record LandingLeg(LandingTarget Target, double LanderTwr = Landing.DefaultTwr);

/// <summary>
/// A landing at a chosen spot from a circular orbit, worked out as in docs/MATH.md §2.6. Angles in degrees, speeds in
/// m/s, times in seconds, distances in metres.
/// </summary>
/// <param name="MinInclinationDeg">The orbit must be inclined at least this much to pass over the site (|latitude|).</param>
/// <param name="InclinationDeg">The inclination the plan assumes: one degree above the minimum, so the track crosses the
/// site's latitude instead of grazing it.</param>
/// <param name="PlaneChangeDv">Turning an equatorial orbit to <paramref name="InclinationDeg"/> (§2.3); 0 when the
/// arrival already sets the inclination.</param>
/// <param name="ArgumentOfLatitudeDeg">Where on the orbit (from the ascending node) the northbound pass crosses the site's latitude.</param>
/// <param name="TrackShiftPerOrbitDeg">How far west the ground track moves each orbit.</param>
/// <param name="DriftDeg">How far the body turns between the deorbit burn and periapsis.</param>
/// <param name="IsRoughEstimate">The body has air: drag shortens the descent by an amount the planner doesn't model yet.</param>
/// <param name="GroundHeightM">The site's ground height the plan used (null: unknown, the datum was assumed).</param>
public sealed record LandingPlan(
    LandingTarget Target, CelestialBody Body, double OrbitAltitudeM, double? GroundHeightM, double PeriapsisAltitudeM,
    double MinInclinationDeg, double InclinationDeg, double PlaneChangeDv, double ArgumentOfLatitudeDeg,
    double OrbitPeriodS, double TrackShiftPerOrbitDeg,
    double DeorbitDv, double DescentTimeS, double DriftDeg, double DriftM,
    double PeriapsisSpeed, double RelativeSpeed, double BrakingAcceleration,
    double BrakingDv, double BrakingTimeS, double BrakingDistanceM,
    bool IsRoughEstimate, IReadOnlyList<string> Warnings)
{
    /// <summary>The ground height came from the surface bake rather than the spot itself.</summary>
    public bool GroundFromBake { get; init; }

    /// <summary>
    /// With a surface bake: the highest terrain under the braking burn on the better of the two passes (§2.6), and
    /// whether that is the northbound pass. Null without a bake.
    /// </summary>
    public double? ApproachPeakM { get; init; }
    public bool? NorthboundPass { get; init; }

    /// <summary>Deorbit plus braking, without the plane change.</summary>
    public double TotalDv => DeorbitDv + BrakingDv;
}

public static class Landing
{
    /// <summary>A typical lander: thrust twice its weight at the surface.</summary>
    public const double DefaultTwr = 2;

    /// <summary>The descent's lowest point is this far above the site's ground.</summary>
    public const double ClearanceM = 1_000;

    /// <summary>The planned orbit is inclined this much more than the site's latitude (§2.6 step 1).</summary>
    public const double InclinationMarginDeg = 1;

    /// <summary>
    /// The landing at <paramref name="leg"/>'s target from a circular orbit at <paramref name="orbitAltitudeM"/> (§2.6).
    /// <paramref name="inclinationDeg"/> overrides the planned inclination; <paramref name="clearanceM"/> the periapsis
    /// height above the site.
    /// </summary>
    /// <param name="bake">The body's surface bake, when there is one: it gives the ground height of a spot without one and
    /// the terrain along the approach.</param>
    public static LandingPlan Plan(CelestialBody body, double orbitAltitudeM, LandingLeg leg,
        double? inclinationDeg = null, double clearanceM = ClearanceM, SurfaceBake? bake = null)
    {
        var target = leg.Target;
        var mu = body.Mu;
        var radius = body.EquatorialRadiusM;
        var phi = Rad(target.LatitudeDeg);
        var warnings = new List<string>();

        // Step 1: an orbit that passes over the site.
        var minInc = Math.Abs(target.LatitudeDeg);
        var inc = inclinationDeg ?? Math.Min(90, minInc + InclinationMarginDeg);
        var r0 = radius + orbitAltitudeM;
        var v0 = OrbitMath.CircularVelocity(mu, r0);
        var planeChange = 2 * v0 * Math.Sin(Rad(inc) / 2);

        // Step 2: where on the orbit the site's latitude is.
        var sinU = Math.Sin(Rad(inc)) is var si && si > 1e-12 ? Math.Clamp(Math.Sin(phi) / si, -1, 1) : 0;
        var u = Deg(Math.Asin(sinU));

        // Step 3: how far the ground track moves each orbit.
        var omega = body.RotationPeriodS == 0 ? 0 : 2 * Math.PI / body.RotationPeriodS;
        var period = OrbitMath.Period(mu, r0);
        var shift = Deg(omega * period);

        // Step 4: the deorbit burn, half an orbit before the site.
        var bakedGround = target.GroundHeightM is null ? bake?.HeightAt(target.LatitudeDeg, target.LongitudeDeg) : null;
        var knownGround = target.GroundHeightM ?? bakedGround;
        if (knownGround is null)
            warnings.Add("The ground height here isn't known; the plan assumes the datum (0 m). Enter it if you know it.");
        var ground = Math.Max(0, knownGround ?? 0);
        var periapsisAlt = Math.Min(orbitAltitudeM, ground + clearanceM);
        if (periapsisAlt < ground + clearanceM)
            warnings.Add($"The orbit is only {orbitAltitudeM / 1000:0.#} km up, below the site plus {clearanceM:0} m clearance.");
        if (bake is null && body.HighestElevationM is { } peak && periapsisAlt < peak)
            warnings.Add($"The descent's low point ({periapsisAlt / 1000:0.#} km) is below {body.Name}'s highest peak " +
                         $"({peak / 1000:0.#} km): check the terrain along the approach.");
        var rp = radius + periapsisAlt;
        var a = (r0 + rp) / 2;
        var deorbit = v0 - OrbitMath.VisViva(mu, r0, a);
        var descent = Math.PI * Math.Sqrt(a * a * a / mu);
        var drift = omega * descent;

        // Step 5: braking at periapsis, holding altitude while slowing down.
        var vp = OrbitMath.VisViva(mu, rp, a);
        var vs = omega * rp * Math.Cos(phi);
        var cosH = Math.Cos(phi) > 1e-9 ? Math.Clamp(Math.Cos(Rad(inc)) / Math.Cos(phi), -1, 1) : 0;
        var vRel = Math.Sqrt(Math.Max(0, vp * vp - 2 * vp * vs * cosH + vs * vs));
        var twr = Math.Max(1.05, leg.LanderTwr);
        if (twr != leg.LanderTwr) warnings.Add("A lander needs more thrust than weight; using TWR 1.05.");
        var thrustAccel = twr * body.SurfaceGravity;
        var g = mu / (rp * rp);
        var aH = Math.Sqrt(Math.Max(1e-9, thrustAccel * thrustAccel - g * g));
        var brakingTime = vRel / aH;
        var brakingDistance = vRel * vRel / (2 * aH);

        // With a bake: the terrain under the braking burn, coming in on either pass (§2.6, "the approach").
        double? approachPeak = null;
        bool? northbound = null;
        if (bake is not null)
        {
            var heading = Deg(Math.Asin(cosH));    // northbound pass: degrees east of north
            var north = ApproachPeak(bake, target, heading, brakingDistance, radius);
            var south = ApproachPeak(bake, target, 180 - heading, brakingDistance, radius);
            if (north is not null || south is not null)
            {
                northbound = south is null || north is not null && north <= south;
                approachPeak = northbound == true ? north : south;
                if (approachPeak > periapsisAlt - 100)
                    warnings.Add($"Terrain up to {approachPeak:#,0} m lies under the braking burn " +
                                 $"({(northbound == true ? "northbound" : "southbound")} pass, the lower of the two), above or near " +
                                 $"the descent's low point ({periapsisAlt:#,0} m). Brake higher or pick the other pass.");
            }
        }

        var rough = body.HasAtmosphere;
        if (rough)
            warnings.Insert(0, $"{body.Name} has air: drag shortens the descent, so where you come down is a rough estimate. " +
                               "Aerobraking and parachutes also make the burns smaller than shown.");

        return new LandingPlan(target, body, orbitAltitudeM, knownGround, periapsisAlt, minInc, inc, planeChange, u, period, shift,
            deorbit, descent, Deg(drift), drift * radius * Math.Cos(phi), vp, vRel, aH,
            thrustAccel * brakingTime, brakingTime, brakingDistance, rough, warnings)
        {
            GroundFromBake = bakedGround is not null,
            ApproachPeakM = approachPeak,
            NorthboundPass = northbound,
        };
    }

    /// <summary>
    /// The highest baked terrain on the ground track over the last <paramref name="distanceM"/> before the site, for a
    /// craft arriving on <paramref name="headingDeg"/> (degrees east of north). Sampled every 500 m.
    /// </summary>
    public static double? ApproachPeak(SurfaceBake bake, LandingTarget target, double headingDeg, double distanceM, double radius)
    {
        double? peak = null;
        var steps = Math.Max(1, (int)Math.Ceiling(distanceM / 500));
        for (var i = 0; i <= steps; i++)
        {
            var (lat, lon) = Destination(target.LatitudeDeg, target.LongitudeDeg, headingDeg + 180, distanceM * i / steps, radius);
            if (bake.HeightAt(lat, lon) is { } h && (peak is null || h > peak)) peak = h;
        }
        return peak;
    }

    /// <summary>The spot <paramref name="distanceM"/> from a start point along a great circle leaving on <paramref name="bearingDeg"/>.</summary>
    public static (double LatitudeDeg, double LongitudeDeg) Destination(double latitudeDeg, double longitudeDeg, double bearingDeg,
        double distanceM, double radius)
    {
        var d = distanceM / radius;
        var phi1 = Rad(latitudeDeg);
        var theta = Rad(bearingDeg);
        var phi2 = Math.Asin(Math.Clamp(Math.Sin(phi1) * Math.Cos(d) + Math.Cos(phi1) * Math.Sin(d) * Math.Cos(theta), -1, 1));
        var lambda2 = Rad(longitudeDeg) + Math.Atan2(Math.Sin(theta) * Math.Sin(d) * Math.Cos(phi1),
            Math.Cos(d) - Math.Sin(phi1) * Math.Sin(phi2));
        var lon = Deg(lambda2);
        return (Deg(phi2), (lon + 540) % 360 - 180);
    }

    /// <summary>
    /// Spots to land at on <paramref name="body"/>: its wiki landmarks, then from the save its flags, landed craft and
    /// custom (KerbNet) waypoints.
    /// </summary>
    public static IReadOnlyList<LandingTarget> Targets(CelestialBody body, KspSave? save)
    {
        var list = body.Landmarks
            .Select(m => new LandingTarget(m.Name, "Landmark", m.LatitudeDeg, m.LongitudeDeg))
            .ToList();
        if (save is null) return list;
        foreach (var v in save.Vessels)
        {
            if (v.Situation is not ("LANDED" or "SPLASHED") || v.Orbit?.BodyName != body.Name
                || !double.IsFinite(v.LatitudeDeg) || !double.IsFinite(v.LongitudeDeg)) continue;
            list.Add(new LandingTarget(v.Name, v.Type == "Flag" ? "Flag" : "Landed craft", v.LatitudeDeg, v.LongitudeDeg,
                double.IsFinite(v.AltitudeM) ? v.AltitudeM : null));
        }
        list.AddRange(save.Waypoints.Where(w => w.BodyName == body.Name)
            .Select(w => new LandingTarget(w.Name, "KerbNet waypoint", w.LatitudeDeg, w.LongitudeDeg)));
        return list;
    }

    /// <summary>Distance along the surface between two spots (haversine, §1.9).</summary>
    public static double SurfaceDistance(double radius, double lat1Deg, double lon1Deg, double lat2Deg, double lon2Deg)
    {
        var dPhi = Rad(lat2Deg - lat1Deg);
        var dLambda = Rad(lon2Deg - lon1Deg);
        var h = Math.Pow(Math.Sin(dPhi / 2), 2) + Math.Cos(Rad(lat1Deg)) * Math.Cos(Rad(lat2Deg)) * Math.Pow(Math.Sin(dLambda / 2), 2);
        return 2 * radius * Math.Asin(Math.Sqrt(Math.Min(1, h)));
    }

    private static double Rad(double deg) => deg * Math.PI / 180;
    private static double Deg(double rad) => rad * 180 / Math.PI;
}
