using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core;

/// <summary>
/// One way of landing at a spot from the craft's real orbit: its burns, when it touches down, and anything to know.
/// </summary>
/// <param name="Label">What it's best at ("Least Δv", "Sooner").</param>
/// <param name="LandingUt">When the braking burn ends over the spot.</param>
/// <param name="MissM">How far to the side of the spot the ground track passes before the plane fix (§2.9 step 4).</param>
/// <param name="Northbound">The craft comes in heading north (else south).</param>
public sealed record LandingOption(string Label, IReadOnlyList<FlightBurn> Burns, double LandingUt, double MissM, bool Northbound,
    LandingPlan Site, IReadOnlyList<string> Warnings)
{
    public double DeltaV => Burns.Sum(b => b.DeltaV);
}

/// <summary>
/// Landing at a chosen spot from wherever the craft is in orbit (Fly mode): tilt the orbit if it can't reach the spot's
/// latitude, wait for a pass that goes close to the spot, deorbit half an orbit before, fix the plane a quarter orbit
/// before, and brake at the low point. The body's rotation angle at UT 0 (θ₀) times it. docs/MATH.md §2.9.
/// </summary>
public static class LandingFlights
{
    /// <summary>A sideways miss smaller than this isn't worth a plane-fix burn.</summary>
    public const double CorrectionMinM = 200;

    /// <summary>The orbit counts as reaching the spot when its highest latitude is at least this far beyond it.</summary>
    public const double ReachMarginDeg = 0.5;

    /// <summary>"Sooner" may cost this much more than the cheapest pass (or <see cref="SoonerExtraFraction"/> of it, if more).</summary>
    public const double SoonerExtraDv = 50;

    public const double SoonerExtraFraction = 0.1;

    /// <summary>A sooner pass costing less than this more than the cheapest is simply the best one: only it is offered.</summary>
    public const double TieDv = 5;

    /// <summary>The most passes looked at: a full turn of the body, so the spot passes under both sides of the orbit.</summary>
    public const int MaxPasses = 400;

    /// <summary>
    /// The ways of landing at <paramref name="leg"/>'s spot from <paramref name="orbit"/> around <paramref name="body"/>,
    /// with no burn sooner than <paramref name="leadS"/> after <paramref name="now"/>. The cheapest comes first.
    /// Throws <see cref="ArgumentException"/> when it can't be planned from this orbit.
    /// </summary>
    /// <param name="initialRotationDeg">The body's rotation angle at UT 0 (kRPC InitialRotation, or the surface bake).</param>
    public static IReadOnlyList<LandingOption> Options(CelestialBody body, LandingLeg leg, OrbitalElements orbit, double now, double leadS,
        double initialRotationDeg, SurfaceBake? bake = null)
    {
        var mu = body.Mu;
        var radius = body.EquatorialRadiusM;
        if (orbit.Eccentricity >= 1)
            throw new ArgumentException($"The craft is on a path that escapes {body.Name}: capture into orbit first.");
        if (Kepler.PeriapsisRadius(orbit) < radius + body.AtmosphereHeightM)
            throw new ArgumentException(body.AtmosphereHeightM > 0
                ? "The periapsis is already in the atmosphere: the craft is coming down. Plan the landing from a stable orbit."
                : "The periapsis is already below the ground: the craft is coming down. Plan the landing from a stable orbit.");

        // Restate the orbit now, so its node line and periapsis are measured the way this planner measures them.
        orbit = Kepler.ElementsFrom(Kepler.StateAt(orbit, mu, now), mu, now);
        var site = Landing.Plan(body, orbit.SemiMajorAxisM - radius, leg, orbit.InclinationDeg, bake: bake);
        var warnings = site.Warnings.Where(w => !w.StartsWith("Terrain up to", StringComparison.Ordinal)).ToList();
        var burns = new List<FlightBurn>();
        var earliest = now + leadS;

        // Step 1: an orbit that reaches the spot's latitude.
        var lat = Math.Abs(leg.Target.LatitudeDeg);
        var retrograde = orbit.InclinationDeg > 90;
        var reach = retrograde ? 180 - orbit.InclinationDeg : orbit.InclinationDeg;
        if (reach < lat + ReachMarginDeg && lat > ReachMarginDeg)
        {
            var newInc = Math.Min(90, lat + Landing.InclinationMarginDeg);
            if (retrograde) newInc = 180 - newInc;
            var tilt = Tilt(body, orbit, earliest, newInc);
            burns.Add(tilt);
            orbit = Rendezvous.OrbitAfter(tilt.Ut, tilt.Burn, orbit, mu);
            earliest = tilt.Ut + Rendezvous.BurnGapS;
        }

        // Steps 2–5 for each pass in a turn of the body; keep the ones that work.
        var period = Kepler.OrbitalPeriod(orbit, mu);
        var passes = body.RotationPeriodS == 0 ? 2 : (int)Math.Clamp(Math.Ceiling(Math.Abs(body.RotationPeriodS) / period) + 1, 2, MaxPasses);
        var candidates = new List<Pass>();
        for (var k = 0; k < passes; k++)
            if (PlanPass(body, leg, site, orbit, earliest, earliest + (k + 1) * period, initialRotationDeg) is { } pass)
                candidates.Add(pass);
        if (candidates.Count == 0)
            throw new ArgumentException($"No pass over {leg.Target.Name} could be planned from this orbit: is it below the descent's low point?");

        var cheapest = candidates.MinBy(c => c.DeltaV)!;
        var allowance = cheapest.DeltaV + Math.Max(SoonerExtraDv, SoonerExtraFraction * cheapest.DeltaV);
        var sooner = candidates.Where(c => c.DeltaV <= allowance).MinBy(c => c.LandingUt)!;

        LandingOption Option(string label, Pass p)
        {
            var notes = warnings.ToList();
            if (bake is not null)
            {
                var peak = Landing.ApproachPeak(bake, leg.Target, p.HeadingDeg, p.BrakingDistanceM, radius);
                if (peak > site.PeriapsisAltitudeM - 100)
                    notes.Add($"Terrain up to {peak:#,0} m lies under the braking burn, above or near the descent's low point " +
                              $"({site.PeriapsisAltitudeM:#,0} m). Brake a little early, or pick the other way to land.");
            }
            if (burns.Count > 0)
                notes.Insert(0, $"Your orbit doesn't reach {leg.Target.Name}'s latitude, so the first burn tilts it to {orbit.InclinationDeg:0.#}°.");
            if (site.IsRoughEstimate)
                notes.Add($"No braking burn on {body.Name}: the air and parachutes do most of the braking. Burn off what's left just before touchdown.");
            return new LandingOption(label, [.. burns, .. p.Burns], p.LandingUt, p.MissM, p.Northbound, site, notes);
        }

        return sooner.DeltaV - cheapest.DeltaV < TieDv
            ? [Option("Least Δv", sooner)]
            : [Option("Least Δv", cheapest), Option("Sooner", sooner)];
    }

    /// <summary>
    /// <paramref name="flight"/> (a <see cref="FlightKind.Landing"/>) with its burns planned from <paramref name="orbit"/>:
    /// the cheapest way, or the sooner one when the flight says so (and there is one).
    /// </summary>
    public static Flight Plan(Flight flight, CelestialBody body, OrbitalElements orbit, double now, double initialRotationDeg, SurfaceBake? bake = null)
    {
        if (flight.Landing is not { } leg) throw new ArgumentException("This flight has no landing spot.");
        var options = Options(body, leg, orbit, now, flight.LeadS, initialRotationDeg, bake);
        var option = flight.LandSooner ? options[^1] : options[0];
        return flight with
        {
            BodyName = body.Name,
            StartOrbit = orbit,
            Burns = option.Burns,
            PlannedUt = now,
            Note = option.Warnings.Count > 0 ? string.Join(" ", option.Warnings) : null,
            Approach = null,
        };
    }

    /// <summary>Where a spot on the surface is, as a unit vector in the fixed frame orbits are measured in, at <paramref name="ut"/>.</summary>
    public static Vector3d SiteDirection(CelestialBody body, LandingTarget target, double initialRotationDeg, double ut)
    {
        var lon = Rad(target.LongitudeDeg + initialRotationDeg) + (body.RotationPeriodS == 0 ? 0 : 2 * Math.PI * ut / body.RotationPeriodS);
        var lat = Rad(target.LatitudeDeg);
        return new Vector3d(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));
    }

    /// <summary>A pass's burns (deorbit, plane fix, braking) and what it costs.</summary>
    private sealed record Pass(IReadOnlyList<FlightBurn> Burns, double LandingUt, double MissM, bool Northbound, double HeadingDeg, double BrakingDistanceM)
    {
        public double DeltaV => Burns.Sum(b => b.DeltaV);
    }

    /// <summary>
    /// Steps 2–5 for the pass that lands near <paramref name="guessUt"/>: aim the low point a braking distance short of
    /// where the spot will be, deorbit half an orbit before it, and repeat until the landing time settles. Null when the
    /// deorbit burn would come before <paramref name="earliest"/>, or the orbit is too low there.
    /// </summary>
    private static Pass? PlanPass(CelestialBody body, LandingLeg leg, LandingPlan site, OrbitalElements orbit, double earliest, double guessUt,
        double initialRotationDeg)
    {
        var mu = body.Mu;
        var omega = body.RotationPeriodS == 0 ? 0 : 2 * Math.PI / body.RotationPeriodS;
        var spin = new Vector3d(0, 0, omega);
        var ground = Math.Max(0, site.GroundHeightM ?? 0);
        var rp = body.EquatorialRadiusM + site.PeriapsisAltitudeM;
        var thrust = Math.Max(1.05, leg.LanderTwr) * body.SurfaceGravity;
        var holdUp = Math.Sqrt(Math.Max(1e-9, thrust * thrust - mu * mu / (rp * rp * rp * rp)));
        var (node, ahead, normal) = Axes(orbit);
        var period = Kepler.OrbitalPeriod(orbit, mu);

        var landing = guessUt;
        var brakeDistance = site.BrakingDistanceM;
        var brakeTime = site.BrakingTimeS;
        var descentHalf = period / 2;
        double burnUt = 0;
        StateVector burnState = default;
        Vector3d newVelocity = default;
        OrbitalElements descent = orbit;
        double periapsisUt = 0, brakingDv = 0;
        StateVector low = default;
        for (var i = 0; i < 8; i++)
        {
            var s = SiteDirection(body, leg.Target, initialRotationDeg, landing);
            var siteAngle = Math.Atan2(Vector3d.Dot(s, ahead), Vector3d.Dot(s, node));
            var aim = siteAngle - brakeDistance / (body.EquatorialRadiusM + ground);
            burnUt = TimeAtArgumentOfLatitude(orbit, mu, aim - Math.PI, landing - brakeTime - descentHalf - period / 2);
            burnState = Kepler.StateAt(orbit, mu, burnUt);
            var rb = burnState.Position.Length;
            if (rb <= rp + 1) return null;
            var a = (rb + rp) / 2;
            newVelocity = QuickBurns.HorizontalVelocity(burnState, OrbitMath.VisViva(mu, rb, a));
            descent = Kepler.ElementsFrom(new StateVector(burnState.Position, newVelocity), mu, burnUt);
            descentHalf = Math.PI * Math.Sqrt(a * a * a / mu);
            periapsisUt = burnUt + descentHalf;
            low = Kepler.StateAt(descent, mu, periapsisUt);
            var relative = (low.Velocity - Vector3d.Cross(spin, low.Position)).Length;
            brakeTime = relative / holdUp;
            brakeDistance = relative * relative / (2 * holdUp);
            brakingDv = thrust * brakeTime;
            var next = periapsisUt + brakeTime;
            var settled = Math.Abs(next - landing) < 0.5;
            landing = next;
            if (settled) break;
        }
        if (burnUt < earliest) return null;

        var burns = new List<FlightBurn>();
        var name = leg.Target.Name;
        var deorbit = NodeBurn.From(burnState, newVelocity - burnState.Velocity);
        burns.Add(new FlightBurn($"Deorbit for {name}", burnUt, deorbit)
        {
            Note = $"Lowers the far side to {site.PeriapsisAltitudeM / 1000:#,0.#} km, a little short of {name}.",
        });

        // Step 4: the spot is off to the side of the track by the angle χ; a burn a quarter orbit before swings the plane over it.
        var siteAtLanding = SiteDirection(body, leg.Target, initialRotationDeg, landing);
        var chi = Math.Asin(Math.Clamp(Vector3d.Dot(siteAtLanding, normal), -1, 1));
        var miss = Math.Abs(chi) * (body.EquatorialRadiusM + ground);
        if (miss > CorrectionMinM)
        {
            var (dNode, dAhead, _) = Axes(descent);
            var siteAngle = Math.Atan2(Vector3d.Dot(siteAtLanding, dAhead), Vector3d.Dot(siteAtLanding, dNode));
            var fixUt = TimeAtArgumentOfLatitude(descent, mu, siteAngle - Math.PI / 2, burnUt + 1);
            if (fixUt - burnUt >= Rendezvous.BurnGapS / 2 && fixUt < periapsisUt)
            {
                var c = Kepler.StateAt(descent, mu, fixUt);
                var up = c.Position.Normalized;
                var plane = Vector3d.Cross(c.Position, siteAtLanding).Normalized;
                if (Vector3d.Dot(plane, normal) < 0) plane = -plane;
                var radial = Vector3d.Dot(c.Velocity, up);
                var horizontal = (c.Velocity - up * radial).Length;
                var fixedVelocity = up * radial + Vector3d.Cross(plane, up) * horizontal;
                var fix = NodeBurn.From(c, fixedVelocity - c.Velocity);
                burns.Add(new FlightBurn($"Swing the track over {name}", fixUt, fix)
                {
                    Note = $"Without it you'd come down {Distance(miss)} to the side of {name}.",
                });
                descent = Rendezvous.OrbitAfter(fixUt, fix, descent, mu);
                low = Kepler.StateAt(descent, mu, periapsisUt);
            }
        }

        // Step 5: braking from the low point, holding height. The node sits half the burn after it, as KSP pilots start half early.
        if (!site.IsRoughEstimate)
            burns.Add(new FlightBurn($"Brake to land at {name}", periapsisUt + brakeTime / 2, new NodeBurn(-brakingDv, 0, 0))
            {
                Note = $"Start at the low point ({site.PeriapsisAltitudeM / 1000:#,0.#} km, half the burn before this node) and hold surface " +
                       $"retrograde, keeping the vertical speed near zero: about {Units(brakeTime)} over {Distance(brakeDistance)}, " +
                       "stopping over the spot. Includes the gravity loss.",
            });

        var site0 = SiteDirection(body, leg.Target, initialRotationDeg, periapsisUt);
        var east = Vector3d.Cross(new Vector3d(0, 0, 1), site0).Normalized;
        var north = Vector3d.Cross(site0, east);
        var heading = Math.Atan2(Vector3d.Dot(low.Velocity, east), Vector3d.Dot(low.Velocity, north)) * 180 / Math.PI;
        return new Pass(burns, landing, miss, Vector3d.Dot(low.Velocity, north) >= 0, (heading + 360) % 360, brakeDistance);
    }

    /// <summary>A burn at the next equator crossing that turns the orbit to <paramref name="inclinationDeg"/>, keeping its speed (§2.3).</summary>
    internal static FlightBurn Tilt(CelestialBody body, OrbitalElements orbit, double after, double inclinationDeg)
    {
        var mu = body.Mu;
        var ascending = TimeAtArgumentOfLatitude(orbit, mu, 0, after);
        var descending = TimeAtArgumentOfLatitude(orbit, mu, Math.PI, after);
        var ut = Math.Min(ascending, descending);
        var s = Kepler.StateAt(orbit, mu, ut);
        var turn = Rad(inclinationDeg - orbit.InclinationDeg);
        var best = new[] { turn, -turn }
            .Select(t => Rotate(s.Velocity, s.Position.Normalized, t))
            .MinBy(v => Math.Abs(Inclination(Vector3d.Cross(s.Position, v)) - inclinationDeg));
        return new FlightBurn($"Tilt the orbit to {inclinationDeg:0.#}°", ut, NodeBurn.From(s, best - s.Velocity))
        {
            Note = "At the equator, so the ground track reaches the spot's latitude.",
        };
    }

    /// <summary>The orbit's node line, the in-plane direction 90° ahead of it, and its normal (as <see cref="Kepler.ElementsFrom"/> measures them).</summary>
    private static (Vector3d Node, Vector3d Ahead, Vector3d Normal) Axes(OrbitalElements orbit)
    {
        var lan = Rad(orbit.LongitudeOfAscendingNodeDeg);
        var inc = Rad(orbit.InclinationDeg);
        var node = new Vector3d(Math.Cos(lan), Math.Sin(lan), 0);
        var normal = new Vector3d(Math.Sin(inc) * Math.Sin(lan), -Math.Sin(inc) * Math.Cos(lan), Math.Cos(inc));
        return (node, Vector3d.Cross(normal, node), normal);
    }

    /// <summary>The first time at or after <paramref name="after"/> the craft is <paramref name="u"/> radians past the ascending node.</summary>
    private static double TimeAtArgumentOfLatitude(OrbitalElements orbit, double mu, double u, double after)
    {
        var e = orbit.Eccentricity;
        var nu = u - Rad(orbit.ArgumentOfPeriapsisDeg);
        var E = 2 * Math.Atan2(Math.Sqrt(1 - e) * Math.Sin(nu / 2), Math.Sqrt(1 + e) * Math.Cos(nu / 2));
        var m = E - e * Math.Sin(E);
        return Kepler.NextTimeAtMeanAnomaly(orbit, mu, after, (m % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI));
    }

    /// <summary>Rodrigues' rotation of <paramref name="v"/> about the unit axis <paramref name="k"/>.</summary>
    private static Vector3d Rotate(Vector3d v, Vector3d k, double angle) =>
        v * Math.Cos(angle) + Vector3d.Cross(k, v) * Math.Sin(angle) + k * (Vector3d.Dot(k, v) * (1 - Math.Cos(angle)));

    private static double Inclination(Vector3d h) => Math.Acos(Math.Clamp(h.Z / h.Length, -1, 1)) * 180 / Math.PI;

    private static string Distance(double metres) => metres < 1000 ? $"{metres:0} m" : $"{metres / 1000:#,0.#} km";

    private static string Units(double seconds) => seconds < 90 ? $"{seconds:0} s" : $"{seconds / 60:0.#} min";

    private static double Rad(double deg) => deg * Math.PI / 180;
}
