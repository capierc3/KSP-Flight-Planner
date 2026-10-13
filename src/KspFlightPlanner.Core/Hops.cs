using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core;

/// <summary>
/// Crossing into the destination's sphere of influence: entering a moon's from its planet's, or leaving a moon's for its
/// planet's. <paramref name="Path"/> is the path around the destination from there (a hyperbola past a moon, an ellipse
/// down to the planet), with its epoch at <paramref name="CrossingUt"/>.
/// </summary>
/// <param name="Prograde">The path goes round the destination the way it spins (anticlockwise seen from the north).</param>
public sealed record SoiArrival(double CrossingUt, OrbitalElements Path, double PeriapsisUt, double PeriapsisAltitudeM, bool Prograde);

/// <summary>
/// One hop's burns: those that get the craft there (<see cref="Departure"/>: the burn to leave, or a course correction
/// when it's already on its way, plus a mid-course correction on a long trip) and the capture at the destination's
/// periapsis. <see cref="Departure"/> is empty when the path it's on already arrives at the asked height;
/// <see cref="Capture"/> is null when that height is in the destination's atmosphere, which does the braking.
/// </summary>
public sealed record HopPlan(IReadOnlyList<FlightBurn> Departure, FlightBurn? Capture, SoiArrival Arrival, IReadOnlyList<string> Warnings)
{
    /// <summary>The first burn: the one that leaves (or corrects).</summary>
    public FlightBurn? Transfer => Departure.Count > 0 ? Departure[0] : null;

    public IReadOnlyList<FlightBurn> Burns => Capture is null ? Departure : [.. Departure, Capture];

    /// <summary>The craft is already on its way, so the first burn only corrects the path.</summary>
    public bool IsCorrection { get; init; }

    /// <summary>The hop waits for a later, cheaper window rather than leaving at the first chance (a tilted moon).</summary>
    public bool Waits { get; init; }
}

/// <summary>
/// The three kinds of hop between bodies, with patched conics as KSP flies them, each ending at a periapsis at a chosen
/// height around the destination and a capture there (or an atmospheric pass):
/// <list type="bullet">
/// <item><see cref="Down"/>: from a planet to one of its moons (or from the star to a planet).</item>
/// <item><see cref="Up"/>: from a moon back to its planet.</item>
/// <item><see cref="Across"/>: between two bodies with the same parent (Kerbin → Duna, Mun → Minmus), in a launch window from the Transfer Planner.</item>
/// </list>
/// Each tunes its burn until the path, followed across the spheres of influence, reaches the destination's periapsis at
/// that height going round the way it spins. docs/MATH.md §3.7.
/// </summary>
public static class Hops
{
    /// <summary>A path passing within this of the asked periapsis needs no correction.</summary>
    public const double OnCourseM = 2_000;

    /// <summary>A moon's orbit tilted more than this from the craft's is worth waiting for (see <c>soonest</c>).</summary>
    public const double TiltedDeg = 0.5;

    /// <summary>On a hop across, the mid-course correction comes this far (as a fraction of the coast) after leaving the origin's sphere.</summary>
    public const double CorrectionAt = 0.1;

    /// <summary>How many later departure windows are searched across half the moon's orbit.</summary>
    private const int WaitWindows = 12;

    /// <summary>How far either side of a starting burn its prograde part is searched, and in what steps (m/s).</summary>
    private const double TuneRange = 40, TuneStep = 0.25;

    /// <summary>A tuned burn counts as arriving at the height when it's within this.</summary>
    private const double ArrivedM = 1_000;

    /// <summary>How many places round the orbit an escape burn is tried, and how far above escape speed it's searched (m/s).</summary>
    private const int EscapePlaces = 36;
    private const double EscapeRange = 1_500, EscapeStep = 2;

    // ---------------- Following a path across a sphere of influence ----------------

    /// <summary>
    /// Where the craft on <paramref name="craft"/> (around <paramref name="parent"/>) first enters <paramref name="moon"/>'s
    /// SOI between <paramref name="fromUt"/> and <paramref name="untilUt"/>, and its path around the moon; null if it doesn't.
    /// </summary>
    public static SoiArrival? EnterSoi(CelestialBody parent, CelestialBody moon, OrbitalElements craft, double fromUt, double untilUt)
    {
        var moonOrbit = moon.Orbit ?? throw new ArgumentException($"{moon.Name} has no orbit.");
        var mu = parent.Mu;
        var soi = moon.SoiRadiusM;
        // Never more than this far between looks, so the closing speed used for the step stays about right.
        var maxStep = Math.Min(Kepler.OrbitalPeriod(moonOrbit, mu), craft.Eccentricity < 1 ? Kepler.OrbitalPeriod(craft, mu) : double.PositiveInfinity) / 72;

        double Distance(double t, out double closing)
        {
            var c = Kepler.StateAt(craft, mu, t);
            var m = Kepler.StateAt(moonOrbit, mu, t);
            closing = (c.Velocity - m.Velocity).Length;
            return (c.Position - m.Position).Length;
        }

        if (Distance(fromUt, out _) < soi) return null; // already inside: its orbit is around the moon, not the planet
        var ut = Crossing(fromUt, untilUt, t => (Distance(t, out var closing) - soi, closing), maxStep);
        return ut is { } u ? Relative(Offset(Kepler.StateAt(craft, mu, u), Kepler.StateAt(moonOrbit, mu, u), -1), moon, u) : null;
    }

    /// <summary>
    /// Where the craft on <paramref name="craft"/> (around <paramref name="moon"/>) first leaves its SOI between
    /// <paramref name="fromUt"/> and <paramref name="untilUt"/>, and its path around <paramref name="parent"/>; null if it doesn't.
    /// </summary>
    public static SoiArrival? LeaveSoi(CelestialBody moon, CelestialBody parent, OrbitalElements craft, double fromUt, double untilUt)
    {
        var moonOrbit = moon.Orbit ?? throw new ArgumentException($"{moon.Name} has no orbit.");
        var soi = moon.SoiRadiusM;
        if (craft.Eccentricity < 1 && craft.SemiMajorAxisM * (1 + craft.Eccentricity) < soi) return null; // never gets that far
        var maxStep = craft.Eccentricity < 1 ? Kepler.OrbitalPeriod(craft, moon.Mu) / 72 : double.PositiveInfinity;

        (double Gap, double Speed) Gap(double t)
        {
            var s = Kepler.StateAt(craft, moon.Mu, t);
            return (soi - s.Position.Length, s.Velocity.Length);
        }

        if (Gap(fromUt).Gap < 0) return null;
        var ut = Crossing(fromUt, untilUt, Gap, maxStep);
        return ut is { } u ? Relative(Offset(Kepler.StateAt(craft, moon.Mu, u), Kepler.StateAt(moonOrbit, parent.Mu, u), +1), parent, u) : null;
    }

    /// <summary>
    /// The first time from <paramref name="fromUt"/> the gap closes to zero, or null by <paramref name="untilUt"/>. Each step
    /// is half the time the gap would take to close at the speed it's closing, so the crossing can't be skipped; then it's
    /// halved back to within 0.01 s.
    /// </summary>
    private static double? Crossing(double fromUt, double untilUt, Func<double, (double Gap, double Speed)> gap, double maxStep)
    {
        var before = fromUt;
        for (var t = fromUt; t <= untilUt;)
        {
            var (g, speed) = gap(t);
            if (g < 0)
            {
                double lo = before, hi = t;
                while (hi - lo > 0.01)
                {
                    var mid = (lo + hi) / 2;
                    if (gap(mid).Gap < 0) hi = mid; else lo = mid;
                }
                return hi;
            }
            before = t;
            t += Math.Clamp(0.5 * g / Math.Max(speed, 1), 1, maxStep);
        }
        return null;
    }

    /// <summary>A state moved to another body's frame: plus (or minus) that body's own state.</summary>
    private static StateVector Offset(StateVector state, StateVector by, int sign) =>
        new(state.Position + by.Position * sign, state.Velocity + by.Velocity * sign);

    /// <summary>The path around <paramref name="body"/> through <paramref name="state"/> (relative to it) at <paramref name="ut"/>.</summary>
    private static SoiArrival Relative(StateVector state, CelestialBody body, double ut)
    {
        var path = Kepler.ElementsFrom(state, body.Mu, ut);
        var periapsisUt = Kepler.NextPeriapsisUt(path, body.Mu, ut) ?? ut;
        var prograde = Vector3d.Cross(state.Position, state.Velocity).Z >= 0;
        return new SoiArrival(ut, path, periapsisUt, Kepler.PeriapsisRadius(path) - body.EquatorialRadiusM, prograde);
    }

    /// <summary>The orbit around the destination once <paramref name="plan"/>'s capture is flown; null without one.</summary>
    public static OrbitalElements? Captured(HopPlan plan, CelestialBody destination) =>
        plan.Capture is { } c ? Rendezvous.OrbitAfter(c.Ut, c.Burn, plan.Arrival.Path, destination.Mu) : null;

    // ---------------- Down: planet to moon ----------------

    /// <summary>
    /// The transfer (or correction) and capture burns from <paramref name="craft"/> (around <paramref name="parent"/>) to a
    /// circular orbit at <paramref name="periapsisAltitudeM"/> around <paramref name="moon"/>, none before
    /// <paramref name="earliestUt"/>. The transfer starts as the cheapest Lambert intercept of the moon's centre (as a
    /// rendezvous, §4.4). Throws <see cref="ArgumentException"/> when no burn reaches it.
    /// </summary>
    /// <param name="soonest">
    /// Leave at the first chance. Otherwise, for a moon whose orbit is tilted from the craft's, the transfer may wait up to
    /// half the moon's orbit for it to come near the craft's plane, where the transfer needs no plane change.
    /// </param>
    public static HopPlan Down(CelestialBody parent, CelestialBody moon, OrbitalElements craft, double earliestUt, double periapsisAltitudeM,
        bool soonest = false)
    {
        if (moon.Orbit is not { } moonOrbit || moon.ParentId != parent.Id) throw new ArgumentException($"{moon.Name} doesn't orbit {parent.Name}.");
        var mu = parent.Mu;
        var moonPeriod = Kepler.OrbitalPeriod(moonOrbit, mu);
        var note = InsideNote(moon);

        // Already heading there (into its sphere, or near it on a path that crosses its orbit): fix the path from here, or
        // leave it if it's good.
        var meetUt = FollowDown(parent, moon, craft, earliestUt)?.PeriapsisUt ?? NearMiss(craft, moonOrbit, moon.SoiRadiusM, mu, earliestUt, moonPeriod);
        if (meetUt is { } meet)
        {
            // Now, or a quarter or half of the way there (a plane fix is cheaper later), whichever costs least.
            var fixes = new[] { 0, 0.25, 0.5 }.Select(f => earliestUt + f * Math.Max(0, meet - earliestUt)).Distinct().Select(ut =>
            {
                try { return (Ut: ut, Result: Correct(parent, moon, craft, ut, meet, periapsisAltitudeM)); }
                catch (ArgumentException) { return (Ut: ut, Result: ((NodeBurn?, SoiArrival)?)null); }
            }).Where(c => c.Result is not null).ToList();
            if (fixes.Count > 0)
            {
                var (ut, (fix, arrival)) = (fixes.MinBy(c => c.Result!.Value.Item1?.Total ?? 0) is var c ? (c.Ut, c.Result!.Value) : default);
                return Finish(fix is { } f ? [new FlightBurn($"Course correction to {moon.Name}", ut, f)] : [], arrival, moon, note) with { IsCorrection = true };
            }
            if (craft.Eccentricity >= 1) throw new ArgumentException($"Couldn't find a course correction that passes {moon.Name} at {Km(periapsisAltitudeM)}.");
            // No correction reaches it: plan a transfer instead.
        }

        if (craft.Eccentricity >= 1) throw new ArgumentException($"The craft is escaping {parent.Name}, so there's no orbit to leave {moon.Name}-bound from.");
        var craftPeriod = Kepler.OrbitalPeriod(craft, mu);
        var synodic = 1 / Math.Abs(1 / craftPeriod - 1 / moonPeriod);
        // About a Hohmann transfer's flight time, out to the moon's distance.
        var halfway = (craft.SemiMajorAxisM + moonOrbit.SemiMajorAxisM) / 2;
        var hohmannS = Math.PI * Math.Sqrt(halfway * halfway * halfway / mu);
        var window = Math.Min(synodic, moonPeriod) * 1.05;

        // The cheapest intercept (transfer plus arrival speed) leaving in the window starting at each time.
        (Maneuver Transfer, double Cost)? Window(double start)
        {
            try
            {
                var best = Rendezvous.CheapestIntercept(craft, moonOrbit, mu, start, start + window, 0.6 * hohmannS, 1.4 * hohmannS);
                var (transfer, match) = Rendezvous.Intercept(parent, craft, moonOrbit, best.DepartureUt, best.FlightTimeS);
                return (transfer, transfer.DeltaV + match.DeltaV);
            }
            catch (ArgumentException) { return null; }
        }

        var first = Window(earliestUt);
        var chosen = first;
        if (!soonest && Rendezvous.RelativeInclinationDeg(craft, moonOrbit, mu) > TiltedDeg)
        {
            var starts = Enumerable.Range(1, WaitWindows).Select(i => earliestUt + i * moonPeriod / 2 / WaitWindows);
            var later = starts.AsParallel().Select(Window).Where(w => w is not null).OrderBy(w => w!.Value.Cost).FirstOrDefault();
            if (later is { } l && (chosen is null || l.Cost < chosen.Value.Cost - 1)) chosen = later;
        }
        if (chosen is not { } pick) throw new ArgumentException($"No transfer to {moon.Name} found from this orbit.");
        var waits = first is { } f0 && pick.Transfer.Ut > f0.Transfer.Ut + window;
        var notes = waits
            ? new List<string> { $"Waits {GameCalendar.Kerbin.FormatDuration(pick.Transfer.Ut - earliestUt)} for {moon.Name} to come near your orbit's plane: " +
                                 $"leaving at the first chance costs about {first!.Value.Transfer.DeltaV - pick.Transfer.DeltaV:0} m/s more." }
            : [];

        var departureUt = pick.Transfer.Ut;
        var aim = new Aim(craft, mu, departureUt, moon, periapsisAltitudeM, (p, from) => FollowDown(parent, moon, p, from));
        var dv = aim.Tune(ToVector(aim.At, pick.Transfer.Burn), aim.At.Velocity.Normalized)
                 ?? throw new ArgumentException($"Couldn't find a burn that passes {moon.Name} at {Km(periapsisAltitudeM)}: try another height, or plan again from a rounder orbit.");
        var plan = Finish([new FlightBurn($"Transfer to {moon.Name}", departureUt, NodeBurn.From(aim.At, dv))], aim.ArrivalAfter(dv)!, moon, note);
        return plan with { Warnings = [.. notes, .. plan.Warnings], Waits = waits };
    }

    /// <summary>
    /// When a path that crosses the moon's orbit (give or take its sphere) comes closest to it, if that's near: within five
    /// spheres, or a tenth of the moon's distance. That's a trip that's missing, worth a correction rather than a new
    /// transfer. Null otherwise.
    /// </summary>
    private static double? NearMiss(OrbitalElements craft, OrbitalElements moonOrbit, double soi, double mu, double fromUt, double moonPeriod)
    {
        var pe = Kepler.PeriapsisRadius(craft);
        var ap = craft.Eccentricity < 1 ? craft.SemiMajorAxisM * (1 + craft.Eccentricity) : double.PositiveInfinity;
        var (moonPe, moonAp) = (Kepler.PeriapsisRadius(moonOrbit), moonOrbit.SemiMajorAxisM * (1 + moonOrbit.Eccentricity));
        if (ap < moonPe - soi || pe > moonAp + soi) return null;
        var ut = ClosestApproachUt(craft, moonOrbit, mu, fromUt, fromUt + Horizon(craft, mu, moonPeriod));
        var gap = (Kepler.StateAt(craft, mu, ut).Position - Kepler.StateAt(moonOrbit, mu, ut).Position).Length;
        return gap < Math.Max(5 * soi, 0.1 * moonOrbit.SemiMajorAxisM) ? ut : null;
    }

    private static SoiArrival? FollowDown(CelestialBody parent, CelestialBody moon, OrbitalElements path, double from) =>
        EnterSoi(parent, moon, path, from, from + Horizon(path, parent.Mu, Kepler.OrbitalPeriod(moon.Orbit!, parent.Mu)));

    /// <summary>
    /// A correction at <paramref name="ut"/> to a path around <paramref name="parent"/> so it passes <paramref name="moon"/> at
    /// the asked height: speed up or slow down along the path as it is, or aim the rest of the way at the moon's centre (for a
    /// miss out of the path's plane), arriving when the path comes closest to it or at <paramref name="arriveUt"/>; whichever
    /// is cheapest. No burn when the path already passes within <see cref="OnCourseM"/>.
    /// </summary>
    private static (NodeBurn? Burn, SoiArrival Arrival) Correct(CelestialBody parent, CelestialBody moon, OrbitalElements path, double ut, double arriveUt,
        double periapsisAltitudeM)
    {
        var aim = new Aim(path, parent.Mu, ut, moon, periapsisAltitudeM, (p, from) => FollowDown(parent, moon, p, from));
        if (aim.ArrivalAfter(Vector3d.Zero) is { } now && OnCourse(now, periapsisAltitudeM)) return (null, now);

        var at = aim.At;
        var seeds = new List<Vector3d> { Vector3d.Zero };
        var closest = ClosestApproachUt(path, moon.Orbit!, parent.Mu, ut, ut + 2 * Math.Max(arriveUt - ut, 0) + 1);
        foreach (var t in new[] { closest, arriveUt }.Where(t => t > ut + 60))
            if (Lambert.Solve(at.Position, Kepler.StateAt(moon.Orbit!, parent.Mu, t).Position, t - ut, parent.Mu, Vector3d.Cross(at.Position, at.Velocity)) is { } s)
                seeds.Add(s.DepartureVelocity - at.Velocity);
        var dv = seeds.Select(seed => aim.Tune(seed, at.Velocity.Normalized)).OfType<Vector3d>().OrderBy(v => v.Length).Cast<Vector3d?>().FirstOrDefault()
                 ?? throw new ArgumentException($"Couldn't find a course correction that passes {moon.Name} at {Km(periapsisAltitudeM)}.");
        return (NodeBurn.From(at, dv), aim.ArrivalAfter(dv)!);
    }

    /// <summary>When a craft on <paramref name="path"/> comes closest to a body on <paramref name="target"/> between the two times.</summary>
    private static double ClosestApproachUt(OrbitalElements path, OrbitalElements target, double mu, double from, double to)
    {
        double Gap(double t) => (Kepler.StateAt(path, mu, t).Position - Kepler.StateAt(target, mu, t).Position).Length;
        const int samples = 200;
        var step = (to - from) / samples;
        var best = Enumerable.Range(0, samples + 1).Select(i => from + i * step).MinBy(Gap);
        double lo = Math.Max(from, best - step), hi = Math.Min(to, best + step);
        const double g = 0.6180339887;
        for (var i = 0; i < 50; i++)
        {
            double a = hi - g * (hi - lo), b = lo + g * (hi - lo);
            if (Gap(a) < Gap(b)) hi = b; else lo = a;
        }
        return (lo + hi) / 2;
    }

    // ---------------- Up: moon to planet ----------------

    /// <summary>
    /// The escape (or correction) and capture burns from <paramref name="craft"/> (around <paramref name="moon"/>) to a
    /// circular orbit at <paramref name="periapsisAltitudeM"/> around its planet <paramref name="parent"/>, none before
    /// <paramref name="earliestUt"/>. A height in the planet's atmosphere plans no capture: the air does the braking.
    /// The escape burn is prograde, tried at <see cref="EscapePlaces"/> places round the orbit; the place where the least
    /// Δv reaches the height wins, then that's narrowed down. Throws <see cref="ArgumentException"/> when no burn reaches it.
    /// </summary>
    public static HopPlan Up(CelestialBody moon, CelestialBody parent, OrbitalElements craft, double earliestUt, double periapsisAltitudeM)
    {
        if (moon.Orbit is not { } moonOrbit || moon.ParentId != parent.Id) throw new ArgumentException($"{moon.Name} doesn't orbit {parent.Name}.");
        var mu = moon.Mu;
        var horizon = Kepler.OrbitalPeriod(moonOrbit, parent.Mu) / 2;
        SoiArrival? Follow(OrbitalElements path, double from) => LeaveSoi(moon, parent, path, from, from + horizon);
        var note = $"Once the craft has left {moon.Name}'s sphere of influence, plan Circularize at periapsis again from here for the exact burn.";

        var coasting = Follow(craft, earliestUt);
        if (coasting is not null)
        {
            if (OnCourse(coasting, periapsisAltitudeM)) return Finish([], coasting, parent, note) with { IsCorrection = true };
            // On the way out already: speed up or slow down, or push sideways (which swings where it leaves); the cheaper.
            var aim = new Aim(craft, mu, earliestUt, parent, periapsisAltitudeM, Follow);
            var at = aim.At;
            var sideways = Vector3d.Cross(Vector3d.Cross(at.Position, at.Velocity), at.Velocity).Normalized;
            var dv = new[] { at.Velocity.Normalized, sideways }.Select(d => aim.Tune(Vector3d.Zero, d)).OfType<Vector3d>()
                         .OrderBy(v => v.Length).Cast<Vector3d?>().FirstOrDefault()
                     ?? throw new ArgumentException($"Couldn't find a correction that brings you to {Km(periapsisAltitudeM)} over {parent.Name}.");
            return Finish([new FlightBurn($"Course correction to {parent.Name}", earliestUt, NodeBurn.From(at, dv))], aim.ArrivalAfter(dv)!, parent, note)
                with { IsCorrection = true };
        }

        if (craft.Eccentricity >= 1) throw new ArgumentException($"The craft is escaping {moon.Name} but won't leave its sphere of influence.");
        var period = Kepler.OrbitalPeriod(craft, mu);

        // The least prograde Δv that brings the periapsis down to the height, burning at ut; null if none does.
        (double Dv, Aim Aim)? Escape(double ut)
        {
            var aim = new Aim(craft, mu, ut, parent, periapsisAltitudeM, Follow);
            var s = aim.At;
            var start = Math.Max(0, Math.Sqrt(2 * mu / s.Position.Length) - s.Velocity.Length);
            return aim.FirstRoot(s.Velocity.Normalized, start, start + EscapeRange, EscapeStep) is { } x ? (x, aim) : null;
        }

        var step = period / EscapePlaces;
        var tried = Enumerable.Range(0, EscapePlaces).AsParallel().Select(i => earliestUt + i * step)
            .Select(ut => (Ut: ut, Result: Escape(ut))).Where(t => t.Result is not null).OrderBy(t => t.Result!.Value.Dv).ToList();
        if (tried.Count == 0)
            throw new ArgumentException($"Couldn't find a burn that brings you to {Km(periapsisAltitudeM)} over {parent.Name}.");

        // Narrow down around the best place.
        var (bestUt, best) = (tried[0].Ut, tried[0].Result!.Value);
        for (var round = 0; round < 3; round++)
        {
            var around = bestUt;
            foreach (var r in Enumerable.Range(-4, 9).AsParallel().Select(i => around + i * step / 4)
                         .Where(ut => ut >= earliestUt).Select(ut => (Ut: ut, Result: Escape(ut))).Where(t => t.Result is not null).ToList())
                if (r.Result!.Value.Dv < best.Dv) (bestUt, best) = (r.Ut, r.Result.Value);
            step /= 4;
        }

        var burnDv = best.Aim.At.Velocity.Normalized * best.Dv;
        return Finish([new FlightBurn($"Leave {moon.Name} for {parent.Name}", bestUt, NodeBurn.From(best.Aim.At, burnDv))], best.Aim.ArrivalAfter(burnDv)!,
            parent, note);
    }

    // ---------------- Across: between two bodies with the same parent ----------------

    /// <summary>
    /// The burns from <paramref name="craft"/> (around <paramref name="origin"/>) to a circular orbit at
    /// <paramref name="periapsisAltitudeM"/> around <paramref name="destination"/>, in the launch window leaving
    /// <paramref name="origin"/> at <paramref name="departureUt"/> and arriving <paramref name="flightTimeS"/> later (from
    /// the Transfer Planner, §3.4):
    /// <list type="number">
    /// <item>The ejection burn: prograde, where the escape path's outgoing direction best matches the window's v∞, and as
    /// hard as that v∞ needs (§3.3).</item>
    /// <item>A course correction <see cref="CorrectionAt"/> of the way along the coast, tuned so the pass is at the height
    /// (it takes up what the ejection's patched conics and out-of-plane angle leave over).</item>
    /// <item>The capture at the destination's periapsis.</item>
    /// </list>
    /// A craft already leaving the origin's sphere skips the ejection.
    /// </summary>
    public static HopPlan Across(CelestialBody origin, CelestialBody destination, CelestialBody parent, OrbitalElements craft, double earliestUt,
        double periapsisAltitudeM, double departureUt, double flightTimeS)
    {
        if (origin.Orbit is null || destination.Orbit is null || origin.ParentId != parent.Id || destination.ParentId != parent.Id)
            throw new ArgumentException($"{origin.Name} and {destination.Name} don't both orbit {parent.Name}.");
        var arriveUt = departureUt + flightTimeS;
        var horizon = Kepler.OrbitalPeriod(origin.Orbit, parent.Mu) / 4;
        var burns = new List<FlightBurn>();
        var notes = new List<string>();

        var correctAt = (SoiArrival exit) => Math.Max(earliestUt, exit.CrossingUt + CorrectionAt * Math.Max(0, arriveUt - exit.CrossingUt));
        var coasting = LeaveSoi(origin, parent, craft, earliestUt, earliestUt + horizon);
        SoiArrival arrival;
        if (coasting is not null)
        {
            var (fix, a) = Correct(parent, destination, coasting.Path, correctAt(coasting), arriveUt, periapsisAltitudeM);
            if (fix is { } f) burns.Add(new FlightBurn($"Course correction to {destination.Name}", correctAt(coasting), f));
            arrival = a;
        }
        else
        {
            if (craft.Eccentricity >= 1) throw new ArgumentException($"The craft is escaping {origin.Name} but won't leave its sphere of influence.");
            var o = Kepler.StateAt(origin.Orbit, parent.Mu, departureUt);
            var d = Kepler.StateAt(destination.Orbit, parent.Mu, arriveUt);
            var transfer = Lambert.Solve(o.Position, d.Position, flightTimeS, parent.Mu, Vector3d.Cross(o.Position, o.Velocity))
                           ?? throw new ArgumentException("No transfer for that departure and flight time.");
            var vInf = transfer.DepartureVelocity - o.Velocity;
            // Near a 180° transfer the transfer's plane is ill-defined and v∞ can carry a tilt that isn't needed: also try
            // leaving flat in the origin's own orbital plane, with the plane fixed on the way.
            var plane = Vector3d.Cross(o.Position, o.Velocity).Normalized;
            var flat = (vInf - plane * Vector3d.Dot(vInf, plane)).Normalized * vInf.Length;

            // Each way to leave: the ejection, tuned through both crossings (out of the origin's sphere, into the
            // destination's) for the height, with or without first taking out the miss across the path's plane. What
            // the tuning can't reach is left to the course correction. The cheapest in all wins.
            var follow = (OrbitalElements p, double from) =>
                LeaveSoi(origin, parent, p, from, from + horizon) is { } x ? FollowDown(parent, destination, x.Path, x.CrossingUt) : null;
            var tries = new List<(double Ut, Vector3d Dv, StateVector At, SoiArrival Exit, NodeBurn? Fix, SoiArrival Arrival)>();
            var offDeg = 0.0;
            foreach (var (want, isFlat) in new[] { (vInf, false), (flat, true) })
            {
                var (ut, dv, off) = Eject(craft, origin.Mu, origin.SoiRadiusM, want, departureUt, earliestUt);
                if (isFlat) offDeg = off;
                var aim = new Aim(craft, origin.Mu, ut, destination, periapsisAltitudeM, follow);
                foreach (var start in new[] { dv, Level(origin, destination, parent, aim.At, ut, dv, arriveUt, horizon) }.Distinct())
                {
                    var burn = aim.Tune(start, aim.At.Velocity.Normalized) ?? start;
                    var path = Kepler.ElementsFrom(new StateVector(aim.At.Position, aim.At.Velocity + burn), origin.Mu, ut);
                    if (LeaveSoi(origin, parent, path, ut, ut + horizon) is not { } exit) continue;
                    try
                    {
                        var (fix, a) = Correct(parent, destination, exit.Path, correctAt(exit), arriveUt, periapsisAltitudeM);
                        tries.Add((ut, burn, aim.At, exit, fix, a));
                    }
                    catch (ArgumentException) { }
                }
            }
            if (tries.Count == 0) throw new ArgumentException($"Couldn't find an ejection burn from {origin.Name} that reaches {destination.Name} in this window.");
            var best = tries.MinBy(t => t.Dv.Length + (t.Fix?.Total ?? 0));

            burns.Add(new FlightBurn($"Leave {origin.Name} for {destination.Name}", best.Ut, NodeBurn.From(best.At, best.Dv))
            {
                Note = $"After it, plan Go to {destination.Name} again from here (once out of {origin.Name}'s sphere) to correct the course from the real path.",
            });
            if (best.Fix is { } bestFix) burns.Add(new FlightBurn($"Course correction to {destination.Name}", correctAt(best.Exit), bestFix));
            arrival = best.Arrival;
            if (offDeg > 5)
                notes.Add($"Your orbit is tilted {offDeg:0}° from the way you need to leave {origin.Name}: the burns make up the difference. " +
                          "An orbit in the plane of the transfer would be cheaper.");
        }

        var plan = Finish(burns, arrival, destination, InsideNote(destination));
        return plan with { Warnings = [.. notes, .. plan.Warnings], IsCorrection = coasting is not null };
    }

    /// <summary>
    /// The prograde ejection burn from <paramref name="craft"/> (around a body of gravity <paramref name="mu"/>) that leaves
    /// its sphere of influence (radius <paramref name="soi"/>) with the velocity closest to <paramref name="vInf"/>. With
    /// patched conics the body stops pulling at the sphere's edge, so that's where the speed has to be |v∞|: the burn is
    /// √(|v∞|² + 2μ/r − 2μ/r_SOI) − v. It's placed where the path's direction at the edge is nearest v∞'s. Searched over
    /// the orbit before <paramref name="departureUt"/> (or after <paramref name="earliestUt"/>, when that's sooner), then
    /// narrowed down. Returns the time, the Δv vector and how far (degrees) the direction is still off.
    /// </summary>
    private static (double Ut, Vector3d Dv, double OffDeg) Eject(OrbitalElements craft, double mu, double soi, Vector3d vInf, double departureUt,
        double earliestUt)
    {
        var period = Kepler.OrbitalPeriod(craft, mu);
        var from = Math.Max(earliestUt, departureUt - period);
        var want = vInf.Normalized;

        (Vector3d Dv, double Off) At(double ut)
        {
            var s = Kepler.StateAt(craft, mu, ut);
            var speed = Math.Sqrt(vInf.Length * vInf.Length + 2 * mu / s.Position.Length - 2 * mu / soi);
            var dv = s.Velocity.Normalized * (speed - s.Velocity.Length);
            var outgoing = DirectionAt(new StateVector(s.Position, s.Velocity + dv), mu, soi);
            return (dv, outgoing is { } u ? Vector3d.Angle(u, want) : Math.PI);
        }

        const int places = 72;
        var best = Enumerable.Range(0, places).Select(i => from + i * period / places).MinBy(ut => At(ut).Off);
        // Golden-section search between the neighbours.
        double lo = Math.Max(from, best - period / places), hi = best + period / places;
        const double g = 0.6180339887;
        for (var i = 0; i < 40; i++)
        {
            double a = hi - g * (hi - lo), b = lo + g * (hi - lo);
            if (At(a).Off < At(b).Off) hi = b; else lo = a;
        }
        var ut = (lo + hi) / 2;
        var (burn, off) = At(ut);
        return (ut, burn, off * 180 / Math.PI);
    }

    /// <summary>
    /// The ejection burn <paramref name="dv"/> at <paramref name="ut"/> (craft at <paramref name="at"/> around
    /// <paramref name="origin"/>) with a normal part added so that, where the path comes closest to
    /// <paramref name="destination"/>, it's level with it: no miss across the path's plane. The miss is close to linear in
    /// the normal Δv, so a few secant steps find it. Returns <paramref name="dv"/> unchanged if the path doesn't leave.
    /// </summary>
    private static Vector3d Level(CelestialBody origin, CelestialBody destination, CelestialBody parent, StateVector at, double ut, Vector3d dv,
        double arriveUt, double horizon)
    {
        var normal = Vector3d.Cross(at.Position, at.Velocity).Normalized;
        double Across(double x)
        {
            var path = Kepler.ElementsFrom(new StateVector(at.Position, at.Velocity + dv + normal * x), origin.Mu, ut);
            if (LeaveSoi(origin, parent, path, ut, ut + horizon) is not { } exit) return double.NaN;
            var t = ClosestApproachUt(exit.Path, destination.Orbit!, parent.Mu, exit.CrossingUt, exit.CrossingUt + 2 * Math.Max(arriveUt - exit.CrossingUt, 0) + 1);
            var c = Kepler.StateAt(exit.Path, parent.Mu, t);
            var d = Kepler.StateAt(destination.Orbit!, parent.Mu, t);
            return Vector3d.Dot(c.Position - d.Position, Vector3d.Cross(c.Position, c.Velocity).Normalized);
        }

        double x0 = 0, x1 = 2, f0 = Across(x0), f1 = Across(x1);
        for (var i = 0; i < 8 && double.IsFinite(f0) && double.IsFinite(f1) && f1 != f0 && Math.Abs(f1) > 1; i++)
        {
            var x2 = x1 - f1 * (x1 - x0) / (f1 - f0);
            (x0, f0, x1, f1) = (x1, f1, x2, Across(x2));
        }
        return double.IsFinite(f1) && Math.Abs(f1) < Math.Abs(Across(0)) ? dv + normal * x1 : dv;
    }

    /// <summary>
    /// The direction a craft on the path through <paramref name="state"/> is heading when it's <paramref name="radius"/>
    /// from the body, on the way out; null if it never gets that far. From r = p / (1 + e·cos ν), and the velocity's
    /// direction (−sin ν, e + cos ν) in the orbit's plane.
    /// </summary>
    private static Vector3d? DirectionAt(StateVector state, double mu, double radius)
    {
        var (r, v) = (state.Position, state.Velocity);
        var h = Vector3d.Cross(r, v);
        var eVec = (r * (Vector3d.Dot(v, v) - mu / r.Length) - v * Vector3d.Dot(r, v)) / mu;
        var e = eVec.Length;
        var p = Vector3d.Dot(h, h) / mu;
        var cosNu = e < 1e-9 ? double.NaN : (p / radius - 1) / e;
        if (cosNu is < -1 or > 1 || double.IsNaN(cosNu)) return null;
        var nu = Math.Acos(cosNu);
        var x = eVec / e;
        var y = Vector3d.Cross(h.Normalized, x);
        return (x * -Math.Sin(nu) + y * (e + Math.Cos(nu))).Normalized;
    }

    // ---------------- Shared ----------------

    /// <summary>
    /// Tuning a burn at <paramref name="ut"/> from <paramref name="craft"/> (around a body of gravity <paramref name="mu"/>)
    /// until the path, followed by <paramref name="follow"/> across the sphere of influence, reaches
    /// <paramref name="destination"/>'s periapsis at <paramref name="altitudeM"/> going round the way it spins. The quantity
    /// solved is the signed periapsis ±r_p − r_target, which runs smoothly through zero as the path sweeps across the body.
    /// </summary>
    private sealed class Aim(OrbitalElements craft, double mu, double ut, CelestialBody destination, double altitudeM,
        Func<OrbitalElements, double, SoiArrival?> follow)
    {
        public StateVector At { get; } = Kepler.StateAt(craft, mu, ut);

        public SoiArrival? ArrivalAfter(Vector3d dv) =>
            follow(Kepler.ElementsFrom(new StateVector(At.Position, At.Velocity + dv), mu, ut), ut);

        private double Miss(Vector3d dv)
        {
            if (ArrivalAfter(dv) is not { } arrival) return double.NaN;
            var signed = destination.EquatorialRadiusM + arrival.PeriapsisAltitudeM;
            return (arrival.Prograde ? signed : -signed) - (destination.EquatorialRadiusM + altitudeM);
        }

        /// <summary>The root closest to <paramref name="seed"/> along <paramref name="direction"/>: look outward both ways for a change of sign, then halve.</summary>
        public Vector3d? Tune(Vector3d seed, Vector3d direction)
        {
            var seen = new Dictionary<int, double>();
            double At(int i) => seen.TryGetValue(i, out var v) ? v : seen[i] = Miss(seed + direction * (i * TuneStep));
            for (var k = 0; k < TuneRange / TuneStep; k++)
                foreach (var (a, b) in new[] { (k, k + 1), (-k, -k - 1) })
                    if (Halve(x => Miss(seed + direction * x), a * TuneStep, b * TuneStep, At(a), At(b)) is { } x)
                        return seed + direction * x;
            return null;
        }

        /// <summary>The first root of a burn of x along <paramref name="direction"/> for x from <paramref name="from"/> to <paramref name="to"/>.</summary>
        public double? FirstRoot(Vector3d direction, double from, double to, double step)
        {
            var previous = Miss(direction * from);
            for (var x = from + step; x <= to; x += step)
            {
                var f = Miss(direction * x);
                if (Halve(y => Miss(direction * y), x - step, x, previous, f) is { } root) return root;
                previous = f;
            }
            return null;
        }

        /// <summary>Halves [a, b] down to the root when f changes sign across it; null otherwise.</summary>
        private static double? Halve(Func<double, double> f, double a, double b, double fa, double fb)
        {
            if (!double.IsFinite(fa) || !double.IsFinite(fb) || Math.Sign(fa) == Math.Sign(fb)) return null;
            for (var i = 0; i < 60 && Math.Abs(b - a) > 1e-6; i++)
            {
                var mid = (a + b) / 2;
                if (Math.Sign(f(mid)) == Math.Sign(fa)) a = mid; else b = mid;
            }
            // A pass over the poles can flip from one way round to the other without going through the middle, which
            // changes the sign with a jump rather than a root: keep what's found only if it really arrives at the height.
            var root = (a + b) / 2;
            return Math.Abs(f(root)) < ArrivedM ? root : null;
        }
    }

    private static bool OnCourse(SoiArrival? arrival, double altitudeM) =>
        arrival is { Prograde: true } a && Math.Abs(a.PeriapsisAltitudeM - altitudeM) < OnCourseM;

    private static string InsideNote(CelestialBody body) =>
        $"Once the craft is inside {body.Name}'s sphere of influence, plan Circularize at periapsis again from here for the exact burn.";

    /// <summary>The capture burn at the destination's periapsis (none in its atmosphere), and what to watch out for.</summary>
    private static HopPlan Finish(IReadOnlyList<FlightBurn> departure, SoiArrival arrival, CelestialBody destination, string captureNote)
    {
        var warnings = new List<string>();
        FlightBurn? capture = null;
        if (arrival.PeriapsisAltitudeM < destination.AtmosphereHeightM)
        {
            warnings.Add($"The periapsis is in {destination.Name}'s atmosphere (it starts at {Km(destination.AtmosphereHeightM)}), so there's no capture " +
                         "burn: the air slows you down. Low enough, it brings you down; just inside, it's an aerobraking pass you may need to repeat.");
        }
        else
        {
            var mu = destination.Mu;
            var rp = destination.EquatorialRadiusM + arrival.PeriapsisAltitudeM;
            capture = new FlightBurn($"Capture at {destination.Name} ({Km(arrival.PeriapsisAltitudeM)})", arrival.PeriapsisUt,
                new NodeBurn(OrbitMath.CircularVelocity(mu, rp) - OrbitMath.VisViva(mu, rp, arrival.Path.SemiMajorAxisM), 0, 0)) { Note = captureNote };
            if (arrival.PeriapsisAltitudeM < (destination.HighestElevationM ?? 0))
                warnings.Add($"The periapsis is below {destination.Name}'s highest peaks ({Km(destination.HighestElevationM!.Value)}): check the path in KSP before you fly it.");
        }
        return new HopPlan(departure, capture, arrival, warnings);
    }

    /// <summary>How long to follow a path looking for the moon: one orbit of it (or of the moon, if longer), up to two of the moon's.</summary>
    private static double Horizon(OrbitalElements craft, double mu, double moonPeriod) =>
        craft.Eccentricity < 1 ? Math.Min(Math.Max(Kepler.OrbitalPeriod(craft, mu), moonPeriod / 4), 2 * moonPeriod) : 2 * moonPeriod;

    /// <summary>A burn in node axes back as a vector, for a craft at <paramref name="state"/> (the inverse of <see cref="NodeBurn.From"/>).</summary>
    private static Vector3d ToVector(StateVector state, NodeBurn burn)
    {
        var prograde = state.Velocity.Normalized;
        var normal = Vector3d.Cross(state.Position, state.Velocity).Normalized;
        var radial = Vector3d.Cross(prograde, normal);
        return prograde * burn.Prograde + normal * burn.Normal + radial * burn.Radial;
    }

    private static string Km(double metres) => $"{metres / 1000:#,0.#} km";
}
