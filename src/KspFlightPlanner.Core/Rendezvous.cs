using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core;

/// <summary>A burn in KSP's maneuver-node axes: prograde (along velocity), normal (along the orbit's angular momentum) and radial (outward).</summary>
public readonly record struct NodeBurn(double Prograde, double Normal, double Radial)
{
    public double Total => Math.Sqrt(Prograde * Prograde + Normal * Normal + Radial * Radial);

    /// <summary>The Δv vector <paramref name="dv"/> split into node axes for a craft at <paramref name="state"/>. See docs/MATH.md §17d.</summary>
    public static NodeBurn From(StateVector state, Vector3d dv)
    {
        var prograde = state.Velocity.Normalized;
        var normal = Vector3d.Cross(state.Position, state.Velocity).Normalized;
        var radial = Vector3d.Cross(prograde, normal); // in the orbit plane, perpendicular to prograde, pointing away from the body
        return new NodeBurn(Vector3d.Dot(dv, prograde), Vector3d.Dot(dv, normal), Vector3d.Dot(dv, radial));
    }
}

public enum ManeuverKind
{
    /// <summary>Tilt the orbit into the target's plane where the two planes cross.</summary>
    PlaneChange,
    /// <summary>Raise or lower the far side of the orbit so the target comes round to the right spot.</summary>
    Phasing,
    /// <summary>Leave for the intercept.</summary>
    Transfer,
    /// <summary>Kill the speed relative to the target at the meeting point.</summary>
    MatchVelocity,
    /// <summary>Lower the periapsis into the atmosphere (or the ground).</summary>
    Deorbit,
}

/// <summary>
/// One burn of a rendezvous plan, with its time and node values, plus what a pilot flying by eye needs:
/// where it happens (AN/DN, phase angle) and the orbit it leaves you in.
/// </summary>
public sealed record Maneuver(ManeuverKind Kind, double Ut, NodeBurn Burn, double PeriapsisAltitudeM, double ApoapsisAltitudeM)
{
    public double DeltaV => Burn.Total;

    /// <summary>Plane change: burning at the ascending node relative to the target (else the descending node).</summary>
    public bool AtAscendingNode { get; init; }

    /// <summary>Plane change: the angle between the two orbits' planes before the burn.</summary>
    public double RelativeInclinationDeg { get; init; }

    /// <summary>Phasing: the option flown.</summary>
    public PhasingOption? Phasing { get; init; }

    /// <summary>Transfer: how far the target is ahead of you, along your orbit, at the burn (0–360°).</summary>
    public double TargetAheadDeg { get; init; }

    /// <summary>Transfer: time from the burn to the meeting.</summary>
    public double FlightTimeS { get; init; }

    /// <summary>Match velocity: the distance to the target at the meeting (0 for an exact intercept).</summary>
    public double MissDistanceM { get; init; }
}

/// <summary>A craft or orbit to meet: its name and its orbit around the body everything happens at.</summary>
public sealed record RendezvousTarget(string Name, OrbitalElements Orbit);

/// <summary>Raise or lower the far side of your orbit for <paramref name="Orbits"/> laps. Orbits = 0 means "just wait".</summary>
public readonly record struct PhasingChoice(int Orbits, bool Higher)
{
    public static readonly PhasingChoice None = new(0, false);
}

/// <summary>
/// A phasing orbit: burn at the start, fly <see cref="Orbits"/> laps (each <see cref="PeriodS"/> long) and you're back at
/// the start point when the target is where the transfer needs it. See docs/MATH.md §17b.
/// </summary>
/// <param name="BurnDv">Prograde (+) or retrograde (−) Δv of the burn into the phasing orbit.</param>
/// <param name="ExtraDv">Rough extra cost: the burn in, and the same again to undo it (folded into the transfer burn).</param>
public sealed record PhasingOption(int Orbits, bool Higher, double PeriodS, double PeriapsisAltitudeM, double ApoapsisAltitudeM,
    double BurnDv, double ExtraDv, double DepartureUt)
{
    public PhasingChoice Choice => new(Orbits, Higher);
}

/// <summary>
/// The textbook view of a leg, for flying by feel: circular, coplanar orbits and a Hohmann transfer.
/// <see cref="RequiredLeadDeg"/> is where the target must be (ahead of you) when you burn.
/// </summary>
public sealed record RendezvousGuide(
    double ChaserPeriodS,
    double TargetPeriodS,
    double TransferTimeS,
    double RequiredLeadDeg,
    double CurrentLeadDeg,
    double WaitS,
    double SynodicPeriodS,
    double TransferDv,
    double MatchDv,
    bool SameOrbit);

/// <summary>A transfer picked on the porkchop plot.</summary>
public sealed record InterceptPoint(double DepartureUt, double FlightTimeS) : IPorkchopPoint;

/// <summary>Everything about one leg: the burns in order, the rule-of-thumb guide and the choices that shaped it.</summary>
/// <param name="StartOrbit">The orbit you fly at <see cref="StartUt"/>, before the leg's first burn.</param>
public sealed record LegPlan(
    RendezvousTarget Target,
    OrbitalElements StartOrbit,
    double StartUt,
    double ArrivalUt,
    IReadOnlyList<Maneuver> Maneuvers,
    RendezvousGuide Guide,
    IReadOnlyList<PhasingOption> PhasingOptions,
    PhasingOption? Phasing,
    PorkchopPlot? Porkchop,
    InterceptPoint? Intercept,
    InterceptPoint? BestIntercept)
{
    public double TotalDv => Maneuvers.Sum(m => m.DeltaV);
    public bool IsCustomIntercept => Intercept is not null && BestIntercept is not null && Intercept != BestIntercept;
}

/// <summary>A launch site on a rotating body. Stock KSP's Kerbin starts rotated 90° at UT 0.</summary>
public sealed record LaunchSite(string Name, string BodyName, double LatitudeDeg, double LongitudeDeg, double BodyInitialRotationDeg)
{
    public static readonly LaunchSite Ksc = new("Kerbal Space Center", "Kerbin", -0.0972, -74.5575, 90);

    /// <summary>
    /// Where the site is in the fixed (inertial) frame orbits are measured in, as a longitude comparable with an orbit's
    /// LAN: λ = longitude + initial rotation + 360°·UT / sidereal day. See docs/MATH.md §17e.
    /// </summary>
    public double InertialLongitudeDeg(CelestialBody body, double ut) =>
        Rendezvous.Wrap360(LongitudeDeg + BodyInitialRotationDeg + 360 * ut / body.RotationPeriodS);
}

/// <summary>
/// How the rocket gets to orbit: the circular parking orbit, how long after lift-off you're in it, and how far around
/// the planet (from the pad) that is. Tune the last two to your own ascents.
/// </summary>
public sealed record AscentProfile(double ParkingAltitudeM, double AscentTimeS = 360, double DownrangeDeg = 30);

public enum LaunchKind
{
    /// <summary>Lift off as the pad passes under the target's orbit going north: straight into its plane.</summary>
    AscendingPass,
    /// <summary>Lift off as the pad passes under the target's orbit going south: straight into its plane.</summary>
    DescendingPass,
    /// <summary>Lift off due east into an equatorial orbit at the best moment for phasing, and fix the plane in orbit.</summary>
    DueEast,
}

/// <summary>A lift-off time and heading, and the parking orbit it gives.</summary>
/// <param name="HeadingDeg">Compass heading to fly from the pad (Kerbin's spin already taken off).</param>
public sealed record LaunchOption(LaunchKind Kind, double LiftoffUt, double InsertionUt, double HeadingDeg, OrbitalElements ParkingOrbit);

public sealed record RendezvousRequest(
    CelestialBody Body,
    IReadOnlyList<RendezvousTarget> Targets,
    double EarliestUt,
    double StayS,
    RendezvousTarget? Chaser = null,
    LaunchSite? Site = null,
    AscentProfile? Ascent = null,
    double? DeorbitPeriapsisAltitudeM = null)
{
    public bool IsLaunch => Chaser is null;
}

/// <summary>The user's picks: which launch, which phasing orbit and which porkchop point per leg (null = the planner's choice).</summary>
public sealed record RendezvousChoices(int? Launch = null, IReadOnlyList<PhasingChoice?>? Phasing = null, IReadOnlyList<InterceptPoint?>? Intercepts = null)
{
    public PhasingChoice? PhasingFor(int leg) => Phasing is { } p && leg < p.Count ? p[leg] : null;
    public InterceptPoint? InterceptFor(int leg) => Intercepts is { } p && leg < p.Count ? p[leg] : null;
}

public sealed record RendezvousPlan(IReadOnlyList<LaunchOption> LaunchOptions, int LaunchIndex, IReadOnlyList<LegPlan> Legs, Maneuver? Deorbit)
{
    public LaunchOption? Launch => LaunchIndex >= 0 && LaunchIndex < LaunchOptions.Count ? LaunchOptions[LaunchIndex] : null;
    public double OrbitalDv => Legs.Sum(l => l.TotalDv) + (Deorbit?.DeltaV ?? 0);
}

/// <summary>
/// Plans meeting craft in orbit around one body: match planes, phase (or wait), transfer, match velocity — for each
/// target in turn — then optionally deorbit the last one. The transfer is a Lambert intercept searched around the
/// Hohmann window, so it works for real (slightly eccentric, slightly tilted) orbits. See docs/MATH.md §17.
/// </summary>
public static class Rendezvous
{
    /// <summary>Planes closer than this are treated as matched.</summary>
    public const double PlaneToleranceDeg = 0.02;

    /// <summary>Orbits whose sizes differ by less than this fraction count as the same orbit (no Hohmann window: phase by laps).</summary>
    public const double SameOrbitFraction = 0.005;

    /// <summary>Earliest a burn is planned after the previous step, so there is time to set up the node.</summary>
    public const double MinimumLeadS = 60;

    /// <summary>Waiting longer than this many of your orbits for a window makes the planner look at phasing orbits.</summary>
    public const double PhasingWaitOrbits = 3;

    /// <summary>Phasing orbits are offered for 1 to this many laps (more laps: a smaller change, so cheaper, but slower).</summary>
    public const int MaxPhasingLaps = 6;

    private const double DegToRad = Math.PI / 180;
    private const double RadToDeg = 180 / Math.PI;
    private const double TwoPi = 2 * Math.PI;

    // ---------------- Whole route ----------------

    public static RendezvousPlan Plan(RendezvousRequest request, RendezvousChoices? choices = null)
    {
        choices ??= new RendezvousChoices();
        Validate(request);
        var body = request.Body;
        var first = request.Targets[0];

        IReadOnlyList<LaunchOption> launches = [];
        var launchIndex = -1;
        OrbitalElements chaser;
        double start;
        if (request.IsLaunch)
        {
            launches = LaunchOptions(request.Site!, body, request.Ascent!, first.Orbit, request.EarliestUt);
            if (launches.Count == 0) throw new ArgumentException("No launch opportunity found.");
            launchIndex = choices.Launch is { } pick && pick >= 0 && pick < launches.Count ? pick : BestLaunch(body, launches, first, choices);
            chaser = launches[launchIndex].ParkingOrbit;
            start = launches[launchIndex].InsertionUt + MinimumLeadS;
        }
        else
        {
            chaser = request.Chaser!.Orbit;
            start = request.EarliestUt;
        }

        var legs = new List<LegPlan>();
        for (var i = 0; i < request.Targets.Count; i++)
        {
            var leg = PlanLeg(body, chaser, request.Targets[i], start, choices.PhasingFor(i), choices.InterceptFor(i));
            legs.Add(leg);
            chaser = request.Targets[i].Orbit; // docked: you now fly the target's orbit
            start = leg.ArrivalUt + Math.Max(request.StayS, MinimumLeadS);
        }

        var deorbit = request.DeorbitPeriapsisAltitudeM is { } pe ? Deorbit(body, chaser, start, pe) : null;
        return new RendezvousPlan(launches, launchIndex, legs, deorbit);
    }

    /// <summary>The cheapest launch in orbital Δv; within 10 m/s of it, the one that arrives first.</summary>
    private static int BestLaunch(CelestialBody body, IReadOnlyList<LaunchOption> launches, RendezvousTarget first, RendezvousChoices choices)
    {
        var plans = launches.Select(l =>
        {
            try { return PlanLeg(body, l.ParkingOrbit, first, l.InsertionUt + MinimumLeadS, choices.PhasingFor(0)); }
            catch (ArgumentException) { return null; }
        }).ToList();
        var cheapest = plans.Where(p => p is not null).Select(p => p!.TotalDv).DefaultIfEmpty(double.NaN).Min();
        if (double.IsNaN(cheapest)) return 0;
        return plans.Select((p, i) => (p, i))
            .Where(x => x.p is not null && x.p.TotalDv <= cheapest + 10)
            .OrderBy(x => x.p!.ArrivalUt).First().i;
    }

    private static void Validate(RendezvousRequest r)
    {
        if (r.Targets.Count == 0) throw new ArgumentException("Pick a target to meet.");
        if (r.IsLaunch)
        {
            if (r.Site is null || r.Ascent is null) throw new ArgumentException("A launch needs a launch site and an ascent profile.");
            if (r.Site.BodyName != r.Body.Name)
                throw new ArgumentException($"{r.Site.Name} is on {r.Site.BodyName}; the targets orbit {r.Body.Name}. Start from a craft in orbit there.");
            if (r.Ascent.ParkingAltitudeM <= Math.Max(r.Body.AtmosphereHeightM, r.Body.HighestElevationM ?? 0))
                throw new ArgumentException("The parking orbit must be above the atmosphere.");
            if (r.Body.RotationPeriodS == 0) throw new ArgumentException($"{r.Body.Name} has no rotation period.");
        }
        foreach (var t in r.Targets.Append(r.Chaser).OfType<RendezvousTarget>())
            if (t.Orbit.Eccentricity is < 0 or >= 1 || t.Orbit.SemiMajorAxisM <= 0)
                throw new ArgumentException($"{t.Name} isn't in a closed orbit.");
    }

    // ---------------- One leg ----------------

    /// <summary>
    /// From <paramref name="chaser"/>'s orbit at <paramref name="startUt"/> to a standstill next to <paramref name="target"/>.
    /// </summary>
    /// <param name="phasing">null: the planner decides; <see cref="PhasingChoice.None"/>: wait in the current orbit.</param>
    /// <param name="intercept">A transfer picked on the porkchop plot; null for the cheapest.</param>
    public static LegPlan PlanLeg(CelestialBody body, OrbitalElements chaser, RendezvousTarget target, double startUt,
        PhasingChoice? phasing = null, InterceptPoint? intercept = null)
    {
        var mu = body.Mu;
        var maneuvers = new List<Maneuver>();
        var orbit = chaser;
        var t = startUt;

        // 1. Match planes at the cheaper of the next two crossings.
        if (RelativeInclinationDeg(orbit, target.Orbit, mu) > PlaneToleranceDeg)
        {
            var (burn, after) = MatchPlane(body, orbit, target.Orbit, t);
            maneuvers.Add(burn);
            orbit = after;
            t = burn.Ut + MinimumLeadS;
        }

        // 2. Phase: wait for the window, or fly a phasing orbit to bring it sooner.
        var guide = Guide(orbit, target.Orbit, mu, t);
        var options = PhasingOptions(body, orbit, target.Orbit, guide, t);
        var chosen = phasing switch
        {
            null => AutoPhasing(guide, options, t),
            { Orbits: 0 } => null,
            { } c => options.FirstOrDefault(o => o.Choice == c) ?? AutoPhasing(guide, options, t),
        };
        if (chosen is null && !double.IsFinite(guide.WaitS))
            chosen = options.OrderBy(o => o.ExtraDv).FirstOrDefault()
                ?? throw new ArgumentException($"You and {target.Name} fly in step, and no phasing orbit fits. Change orbit first.");

        double predicted, halfWindow;
        if (chosen is not null)
        {
            var state = Kepler.StateAt(orbit, mu, t);
            var dv = state.Velocity.Normalized * chosen.BurnDv;
            var after = new StateVector(state.Position, state.Velocity + dv);
            var phasingOrbit = Kepler.ElementsFrom(after, mu, t);
            maneuvers.Add(Burn(ManeuverKind.Phasing, t, state, dv, phasingOrbit, body) with { Phasing = chosen });
            orbit = phasingOrbit;
            predicted = chosen.DepartureUt;
            halfWindow = 0.5 * chosen.PeriodS;
        }
        else
        {
            predicted = t + guide.WaitS;
            halfWindow = Math.Min(0.5 * guide.SynodicPeriodS, Math.Max(guide.ChaserPeriodS, guide.TargetPeriodS));
        }

        // 3. Transfer: Lambert intercepts around the predicted window; the cheapest (or the one picked) is flown. Sharing the
        // target's orbit, the phasing lap has already brought you to it: a short hop of a quarter to under half an orbit
        // closes what's left (a half-orbit hop would be a 180° transfer, which Lambert can't solve).
        var earliest = t + (chosen is null ? 0 : MinimumLeadS);
        var depStart = Math.Max(earliest, predicted - halfWindow);
        var depEnd = Math.Max(depStart + 60, predicted + halfWindow);
        var (tofMin, tofMax) = guide.SameOrbit
            ? (0.15 * guide.TargetPeriodS, 0.45 * guide.TargetPeriodS)
            : (0.5 * guide.TransferTimeS, 1.5 * guide.TransferTimeS);
        var plot = Porkchop(orbit, target.Orbit, mu, depStart, depEnd, tofMin, tofMax);
        var best = Refine(orbit, target.Orbit, mu, plot, earliest);
        var flown = intercept is not null && Cost(orbit, target.Orbit, mu, intercept.DepartureUt, intercept.FlightTimeS) is { } c2 && double.IsFinite(c2)
            ? intercept : best;

        var (transfer, arrival) = Intercept(body, orbit, target.Orbit, flown.DepartureUt, flown.FlightTimeS);
        if (transfer.DeltaV >= 0.05) maneuvers.Add(transfer); // a phasing orbit can already lead straight to the target
        maneuvers.Add(arrival);
        return new LegPlan(target, chaser, startUt, arrival.Ut, maneuvers, guide, options, chosen, plot, flown, best);
    }

    /// <summary>The orbit you're on after flying <paramref name="burn"/> from <paramref name="before"/> (node axes as in <see cref="NodeBurn.From"/>).</summary>
    public static OrbitalElements OrbitAfter(Maneuver burn, OrbitalElements before, double mu)
    {
        var s = Kepler.StateAt(before, mu, burn.Ut);
        var prograde = s.Velocity.Normalized;
        var normal = Vector3d.Cross(s.Position, s.Velocity).Normalized;
        var radial = Vector3d.Cross(prograde, normal);
        var v = s.Velocity + prograde * burn.Burn.Prograde + normal * burn.Burn.Normal + radial * burn.Burn.Radial;
        return Kepler.ElementsFrom(new StateVector(s.Position, v), mu, burn.Ut);
    }

    /// <summary>Angle between the two orbits' planes (angle between their angular-momentum vectors).</summary>
    public static double RelativeInclinationDeg(OrbitalElements a, OrbitalElements b, double mu) =>
        Vector3d.Angle(Normal(a, mu), Normal(b, mu)) * RadToDeg;

    private static Vector3d Normal(OrbitalElements orbit, double mu)
    {
        var s = Kepler.StateAt(orbit, mu, orbit.EpochS);
        return Vector3d.Cross(s.Position, s.Velocity).Normalized;
    }

    /// <summary>
    /// The burn at a crossing of the two planes that turns your velocity into the target's plane, keeping its speed and
    /// climb angle: Δv = 2·v_h·sin(Δi/2) for the horizontal part v_h. The cheaper of the next two crossings. docs/MATH.md §17a.
    /// </summary>
    public static (Maneuver Burn, OrbitalElements After) MatchPlane(CelestialBody body, OrbitalElements chaser, OrbitalElements target, double afterUt)
    {
        var mu = body.Mu;
        var targetNormal = Normal(target, mu);
        var relInc = RelativeInclinationDeg(chaser, target, mu);
        var period = Kepler.OrbitalPeriod(chaser, mu);

        double Side(double ut) => Vector3d.Dot(Kepler.StateAt(chaser, mu, ut).Position, targetNormal);

        (Maneuver Burn, OrbitalElements After)? best = null;
        const int samples = 360;
        var found = 0;
        for (var k = 0; k < samples && found < 2; k++)
        {
            double t0 = afterUt + period * k / samples, t1 = afterUt + period * (k + 1) / samples;
            double s0 = Side(t0), s1 = Side(t1);
            if (Math.Sign(s0) == Math.Sign(s1) || s1 == 0) continue;
            for (var i = 0; i < 60; i++)
            {
                var mid = (t0 + t1) / 2;
                if (Math.Sign(Side(mid)) == Math.Sign(s0)) t0 = mid; else t1 = mid;
            }
            found++;
            var ut = (t0 + t1) / 2;
            var s = Kepler.StateAt(chaser, mu, ut);
            var rHat = s.Position.Normalized;
            var vr = Vector3d.Dot(s.Velocity, rHat);
            var vh = (s.Velocity - rHat * vr).Length;
            var along = Vector3d.Cross(targetNormal, rHat);
            var newVelocity = rHat * vr + along * vh;
            var after = Kepler.ElementsFrom(new StateVector(s.Position, newVelocity), mu, ut);
            var burn = Burn(ManeuverKind.PlaneChange, ut, s, newVelocity - s.Velocity, after, body) with
            {
                AtAscendingNode = Vector3d.Dot(s.Velocity, targetNormal) > 0,
                RelativeInclinationDeg = relInc,
            };
            if (best is null || burn.DeltaV < best.Value.Burn.DeltaV - 0.01) best = (burn, after);
        }
        return best ?? throw new ArgumentException("Couldn't find where the orbits' planes cross.");
    }

    /// <summary>
    /// Hohmann rule of thumb from your orbit's size a₁ to the target's a₂: transfer time t_H = π√(((a₁+a₂)/2)³/μ),
    /// lead angle φ = 180° − n₂·t_H, and the wait until the target is φ ahead. See docs/MATH.md §17b.
    /// </summary>
    public static RendezvousGuide Guide(OrbitalElements chaser, OrbitalElements target, double mu, double ut)
    {
        var a1 = chaser.SemiMajorAxisM;
        var a2 = target.SemiMajorAxisM;
        var n1 = Math.Sqrt(mu / (a1 * a1 * a1));
        var n2 = Math.Sqrt(mu / (a2 * a2 * a2));
        var hohmann = OrbitMath.Hohmann(mu, a1, a2);
        var sameOrbit = Math.Abs(a2 - a1) < SameOrbitFraction * a2;

        var lead = sameOrbit ? 0 : WrapPi(Math.PI - n2 * hohmann.TransferTimeS);
        var current = LeadAngle(Kepler.StateAt(chaser, mu, ut), Kepler.StateAt(target, mu, ut).Position);
        var rate = n2 - n1; // how fast the lead angle changes
        var wait = Math.Abs(rate) < 1e-12 ? double.PositiveInfinity
            : rate < 0 ? Wrap2Pi(current - lead) / -rate
            : Wrap2Pi(lead - current) / rate;
        var synodic = Math.Abs(rate) < 1e-12 ? double.PositiveInfinity : TwoPi / Math.Abs(rate);
        if (synodic > 1e3 * TwoPi / Math.Min(n1, n2)) (wait, synodic) = (double.PositiveInfinity, double.PositiveInfinity);

        return new RendezvousGuide(TwoPi / n1, TwoPi / n2, hohmann.TransferTimeS, Wrap360(lead * RadToDeg), current * RadToDeg,
            wait, synodic, hohmann.Dv1, hohmann.Dv2, sameOrbit);
    }

    /// <summary>
    /// Phasing orbits that bring the target to the lead angle after k laps (k = 1…6), one a little lower (faster) and one
    /// a little higher (slower) than your orbit: T_p = (Δ + 2π·m)/(n₂·k) with Δ = (φ − θ₀) mod 2π. docs/MATH.md §17b.
    /// Options that dip into the atmosphere or leave the sphere of influence are left out.
    /// </summary>
    public static IReadOnlyList<PhasingOption> PhasingOptions(CelestialBody body, OrbitalElements chaser, OrbitalElements target,
        RendezvousGuide guide, double ut)
    {
        var mu = body.Mu;
        var state = Kepler.StateAt(chaser, mu, ut);
        var r = state.Position.Length;
        var speed = state.Velocity.Length;
        var n2 = TwoPi / guide.TargetPeriodS;
        var delta = Wrap2Pi((guide.RequiredLeadDeg - guide.CurrentLeadDeg) * DegToRad);
        var floor = Math.Max(body.AtmosphereHeightM, body.HighestElevationM ?? 0) + 1_000;

        var options = new List<PhasingOption>();
        for (var k = 1; k <= MaxPhasingLaps; k++)
        {
            var x = k * guide.ChaserPeriodS / guide.TargetPeriodS - delta / TwoPi;
            var m = Math.Floor(x);
            foreach (var (laps, higher) in new[] { (m, false), (m + 1, true) })
            {
                if (laps < 0) continue;
                var period = (delta + TwoPi * laps) / (n2 * k);
                if (period <= 0) continue;
                var a = OrbitMath.SemiMajorAxisFromPeriod(mu, period);
                if (2 / r - 1 / a <= 0) continue;
                var newSpeed = Math.Sqrt(mu * (2 / r - 1 / a));
                var after = Kepler.ElementsFrom(new StateVector(state.Position, state.Velocity.Normalized * newSpeed), mu, ut);
                var pe = after.SemiMajorAxisM * (1 - after.Eccentricity) - body.EquatorialRadiusM;
                var ap = after.SemiMajorAxisM * (1 + after.Eccentricity) - body.EquatorialRadiusM;
                if (pe < floor || ap + body.EquatorialRadiusM >= body.SoiRadiusM) continue;
                var burn = newSpeed - speed;
                options.Add(new PhasingOption(k, higher, period, pe, ap, burn, 2 * Math.Abs(burn), ut + k * period));
            }
        }
        return options;
    }

    /// <summary>
    /// Wait when the window comes within a few orbits; otherwise the cheapest phasing orbit that still gets there sooner
    /// than waiting (null if none does).
    /// </summary>
    private static PhasingOption? AutoPhasing(RendezvousGuide guide, IReadOnlyList<PhasingOption> options, double ut)
    {
        if (guide.WaitS <= PhasingWaitOrbits * guide.ChaserPeriodS) return null;
        return options.Where(o => o.DepartureUt < ut + guide.WaitS).OrderBy(o => o.ExtraDv).FirstOrDefault();
    }

    // ---------------- Lambert intercept ----------------

    /// <summary>Δv of leaving the chaser's orbit at <paramref name="dep"/> and matching the target after <paramref name="tof"/>; NaN when unsolvable.</summary>
    private static double Cost(OrbitalElements chaser, OrbitalElements target, double mu, double dep, double tof)
    {
        var c = Kepler.StateAt(chaser, mu, dep);
        var tgt = Kepler.StateAt(target, mu, dep + tof);
        if (Solve(c, tgt.Position, dep, tof, mu) is not { } s) return double.NaN;
        return (s.DepartureVelocity - c.Velocity).Length + (tgt.Velocity - s.ArrivalVelocity).Length;
    }

    /// <summary>
    /// Lambert's problem, prograde about the chaser's orbit, keeping only answers that really arrive: next to a 180° transfer
    /// the transfer plane is undefined and the formulas divide by almost zero, so the result is flown (propagated) and
    /// rejected if it ends more than 10 m from where it should.
    /// </summary>
    private static Lambert.Solution? Solve(StateVector chaser, Vector3d arrival, double dep, double tof, double mu)
    {
        var h = Vector3d.Cross(chaser.Position, chaser.Velocity);
        if (Lambert.Solve(chaser.Position, arrival, tof, mu, h) is not { } s) return null;
        var departing = new StateVector(chaser.Position, s.DepartureVelocity);
        if (!double.IsFinite(s.DepartureVelocity.Length) || 2 / chaser.Position.Length - s.DepartureVelocity.Length * s.DepartureVelocity.Length / mu <= 0)
            return null; // not a closed orbit
        var flown = Kepler.StateAt(Kepler.ElementsFrom(departing, mu, dep), mu, dep + tof).Position;
        return (flown - arrival).Length <= 10 ? s : null;
    }

    private static PorkchopPlot Porkchop(OrbitalElements chaser, OrbitalElements target, double mu, double depStart, double depEnd,
        double tofMin, double tofMax, int columns = 100, int rows = 60)
    {
        var grid = new double[columns * rows];
        Parallel.For(0, columns, col =>
        {
            var dep = depStart + (depEnd - depStart) * col / (columns - 1);
            for (var row = 0; row < rows; row++)
                grid[row * columns + col] = Cost(chaser, target, mu, dep, tofMin + (tofMax - tofMin) * row / (rows - 1));
        });
        return new PorkchopPlot(depStart, depEnd, tofMin, tofMax, columns, rows, grid);
    }

    /// <summary>The cheapest grid cell, then a few rounds of zooming in around it (as in the Transfer Planner).</summary>
    private static InterceptPoint Refine(OrbitalElements chaser, OrbitalElements target, double mu, PorkchopPlot plot, double earliest)
    {
        var bestCost = double.PositiveInfinity;
        double bestDep = 0, bestTof = 0;
        for (var r = 0; r < plot.Rows; r++)
        for (var c = 0; c < plot.Columns; c++)
            if (plot[c, r] < bestCost) (bestCost, bestDep, bestTof) = (plot[c, r], plot.DepartureAt(c), plot.FlightTimeAt(r));
        if (double.IsPositiveInfinity(bestCost)) throw new ArgumentException("No intercept found in this window.");

        double depStep = (plot.DepartureEndUt - plot.DepartureStartUt) / (plot.Columns - 1);
        double tofStep = (plot.FlightTimeMaxS - plot.FlightTimeMinS) / (plot.Rows - 1);
        for (var iteration = 0; iteration < 8; iteration++)
        {
            double centreDep = bestDep, centreTof = bestTof;
            for (var i = -5; i <= 5; i++)
            for (var j = -5; j <= 5; j++)
            {
                var dep = Math.Max(earliest, centreDep + i * depStep / 5);
                var tof = Math.Max(60, centreTof + j * tofStep / 5);
                var cost = Cost(chaser, target, mu, dep, tof);
                if (cost < bestCost) (bestCost, bestDep, bestTof) = (cost, dep, tof);
            }
            depStep /= 3;
            tofStep /= 3;
        }
        return new InterceptPoint(bestDep, bestTof);
    }

    /// <summary>The transfer burn and the match-velocity burn of a Lambert intercept. docs/MATH.md §17c.</summary>
    public static (Maneuver Transfer, Maneuver Match) Intercept(CelestialBody body, OrbitalElements chaser, OrbitalElements target,
        double departureUt, double flightTimeS)
    {
        var mu = body.Mu;
        var c = Kepler.StateAt(chaser, mu, departureUt);
        var arrivalUt = departureUt + flightTimeS;
        var tgt = Kepler.StateAt(target, mu, arrivalUt);
        var s = Solve(c, tgt.Position, departureUt, flightTimeS, mu)
                ?? throw new ArgumentException("No intercept for that departure and flight time.");

        var transferOrbit = Kepler.ElementsFrom(new StateVector(c.Position, s.DepartureVelocity), mu, departureUt);
        var lead = LeadAngle(c, Kepler.StateAt(target, mu, departureUt).Position) * RadToDeg;
        var transfer = Burn(ManeuverKind.Transfer, departureUt, c, s.DepartureVelocity - c.Velocity, transferOrbit, body) with
        {
            TargetAheadDeg = lead,
            FlightTimeS = flightTimeS,
        };
        var arriving = new StateVector(tgt.Position, s.ArrivalVelocity);
        var match = Burn(ManeuverKind.MatchVelocity, arrivalUt, arriving, tgt.Velocity - s.ArrivalVelocity, target, body);
        return (transfer, match);
    }

    // ---------------- Launch ----------------

    /// <summary>
    /// Lift-off opportunities in the next two sidereal days: each time the pad passes under the target's plane (straight into
    /// it — heading from sin β = cos i), plus a due-east launch timed for the shortest wait in orbit. docs/MATH.md §17e.
    /// </summary>
    public static IReadOnlyList<LaunchOption> LaunchOptions(LaunchSite site, CelestialBody body, AscentProfile ascent,
        OrbitalElements target, double earliestUt)
    {
        var mu = body.Mu;
        var radius = body.EquatorialRadiusM + ascent.ParkingAltitudeM;
        var orbitSpeed = Math.Sqrt(mu / radius);
        var day = body.RotationPeriodS;
        var spin = TwoPi * body.EquatorialRadiusM * Math.Cos(site.LatitudeDeg * DegToRad) / day; // surface speed, signed with rotation
        var degPerSecond = 360 / day;
        var options = new List<LaunchOption>();

        OrbitalElements Parking(double inclinationDeg, double lanDeg, double argumentOfLatitudeDeg, double insertionUt) =>
            new(radius, 0, inclinationDeg, Wrap360(lanDeg), 0, Wrap360(argumentOfLatitudeDeg) * DegToRad, insertionUt);

        double SurfaceHeading(double inertialAzimuthDeg)
        {
            var east = orbitSpeed * Math.Sin(inertialAzimuthDeg * DegToRad) - spin;
            var north = orbitSpeed * Math.Cos(inertialAzimuthDeg * DegToRad);
            return Wrap360(Math.Atan2(east, north) * RadToDeg);
        }

        var inclination = target.InclinationDeg;
        if (inclination is > 0.2 and < 179.8)
        {
            foreach (var ascending in new[] { true, false })
            {
                var nodeLongitude = target.LongitudeOfAscendingNodeDeg + (ascending ? 0 : 180);
                var gap = Wrap360((nodeLongitude - site.InertialLongitudeDeg(body, earliestUt)) * Math.Sign(degPerSecond));
                for (var n = 0; n < 2; n++)
                {
                    var liftoff = earliestUt + (gap + 360 * n) / Math.Abs(degPerSecond);
                    var insertion = liftoff + ascent.AscentTimeS;
                    var azimuth = ascending ? 90 - inclination : 90 + inclination;
                    options.Add(new LaunchOption(ascending ? LaunchKind.AscendingPass : LaunchKind.DescendingPass, liftoff, insertion,
                        SurfaceHeading(azimuth), Parking(inclination, target.LongitudeOfAscendingNodeDeg, (ascending ? 0 : 180) + ascent.DownrangeDeg, insertion)));
                }
            }
        }

        // Due east: any time works for the plane, so time it for the shortest wait once in orbit.
        double bestArrival = double.PositiveInfinity, bestLiftoff = earliestUt;
        for (var liftoff = earliestUt; liftoff <= earliestUt + Math.Abs(day); liftoff += 30)
        {
            var insertion = liftoff + ascent.AscentTimeS;
            var parking = Parking(0, 0, site.InertialLongitudeDeg(body, liftoff) + ascent.DownrangeDeg, insertion);
            var guide = Guide(parking, target, mu, insertion);
            var arrival = insertion + (double.IsFinite(guide.WaitS) ? guide.WaitS : 1e12) + guide.TransferTimeS;
            if (arrival < bestArrival) (bestArrival, bestLiftoff) = (arrival, liftoff);
        }
        var eastInsertion = bestLiftoff + ascent.AscentTimeS;
        options.Add(new LaunchOption(LaunchKind.DueEast, bestLiftoff, eastInsertion, SurfaceHeading(90),
            Parking(0, 0, site.InertialLongitudeDeg(body, bestLiftoff) + ascent.DownrangeDeg, eastInsertion)));

        return options.OrderBy(o => o.LiftoffUt).ToList();
    }

    // ---------------- Deorbit ----------------

    /// <summary>
    /// A retrograde burn at the next apoapsis (right away for a circular orbit) that drops the periapsis to
    /// <paramref name="periapsisAltitudeM"/>: Δv = v_a − √(2μ·r_p / (r_a·(r_a + r_p))). docs/MATH.md §17f.
    /// </summary>
    public static Maneuver Deorbit(CelestialBody body, OrbitalElements orbit, double afterUt, double periapsisAltitudeM)
    {
        var mu = body.Mu;
        var ut = orbit.Eccentricity < 0.002 ? afterUt : Kepler.NextTimeAtMeanAnomaly(orbit, mu, afterUt, Math.PI);
        var s = Kepler.StateAt(orbit, mu, ut);
        var ra = s.Position.Length;
        var rp = body.EquatorialRadiusM + periapsisAltitudeM;
        if (rp >= ra) throw new ArgumentException("The deorbit periapsis must be below the orbit.");
        var newSpeed = Math.Sqrt(2 * mu * rp / (ra * (ra + rp)));
        var dv = s.Velocity.Normalized * (newSpeed - s.Velocity.Length);
        var after = Kepler.ElementsFrom(new StateVector(s.Position, s.Velocity + dv), mu, ut);
        return Burn(ManeuverKind.Deorbit, ut, s, dv, after, body);
    }

    // ---------------- Helpers ----------------

    /// <summary>
    /// How far the target is ahead of the chaser along the chaser's orbit, radians in [0, 2π): the angle from the
    /// chaser's position to the target's, measured in the direction of motion.
    /// </summary>
    public static double LeadAngle(StateVector chaser, Vector3d targetPosition)
    {
        var radial = chaser.Position.Normalized;
        var normal = Vector3d.Cross(chaser.Position, chaser.Velocity).Normalized;
        var ahead = Vector3d.Cross(normal, radial);
        return Wrap2Pi(Math.Atan2(Vector3d.Dot(targetPosition, ahead), Vector3d.Dot(targetPosition, radial)));
    }

    private static Maneuver Burn(ManeuverKind kind, double ut, StateVector before, Vector3d dv, OrbitalElements after, CelestialBody body) =>
        new(kind, ut, NodeBurn.From(before, dv),
            after.SemiMajorAxisM * (1 - after.Eccentricity) - body.EquatorialRadiusM,
            after.SemiMajorAxisM * (1 + after.Eccentricity) - body.EquatorialRadiusM);

    internal static double Wrap360(double deg) => (deg % 360 + 360) % 360;
    private static double Wrap2Pi(double rad) => (rad % TwoPi + TwoPi) % TwoPi;
    private static double WrapPi(double rad) => Wrap2Pi(rad + Math.PI) - Math.PI;
}
