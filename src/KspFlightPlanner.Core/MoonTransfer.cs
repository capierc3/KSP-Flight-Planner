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
/// The burns between an orbit around a planet and one around its moon, either way: the transfer (or a course correction,
/// when the craft is already on its way) and the capture at the destination's periapsis. <see cref="Transfer"/> is null
/// when the path it's on already arrives at the asked height; <see cref="Capture"/> when that height is in the
/// destination's atmosphere, which does the braking.
/// </summary>
public sealed record MoonTransferPlan(FlightBurn? Transfer, FlightBurn? Capture, SoiArrival Arrival, IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<FlightBurn> Burns => new[] { Transfer, Capture }.OfType<FlightBurn>().ToList();

    /// <summary>The craft is already on its way, so the first burn only corrects the path.</summary>
    public bool IsCorrection { get; init; }
}

/// <summary>
/// Planning a trip between a planet and one of its moons, with patched conics as KSP flies it, ending in a circular orbit
/// at a chosen height (or an atmospheric pass). The first burn is tuned until the path, flown across the sphere of
/// influence, reaches the destination's periapsis at that height going round the way it spins; the capture burn
/// circularizes there. Going out, the burn starts as the cheapest Lambert intercept of the moon's centre; coming back, it's
/// a prograde escape burn placed where it drops the periapsis lowest for its Δv. docs/MATH.md §3.7.
/// </summary>
public static class MoonTransfer
{
    /// <summary>A path passing within this of the asked periapsis needs no correction.</summary>
    public const double OnCourseM = 2_000;

    /// <summary>A moon's orbit tilted more than this from the craft's is worth waiting for (see <c>soonest</c>).</summary>
    public const double TiltedDeg = 0.5;

    /// <summary>How many later departure windows are searched across half the moon's orbit.</summary>
    private const int WaitWindows = 12;

    /// <summary>How far either side of a starting burn its prograde part is searched, and in what steps (m/s).</summary>
    private const double TuneRange = 40, TuneStep = 0.25;

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

    // ---------------- Planet to moon ----------------

    /// <summary>
    /// The transfer (or correction) and capture burns from <paramref name="craft"/> (around <paramref name="parent"/>) to a
    /// circular orbit at <paramref name="periapsisAltitudeM"/> around <paramref name="moon"/>, none before
    /// <paramref name="earliestUt"/>. Throws <see cref="ArgumentException"/> when no burn reaches it.
    /// </summary>
    /// <param name="soonest">
    /// Leave at the first chance. Otherwise, for a moon whose orbit is tilted from the craft's, the transfer may wait up to
    /// half the moon's orbit for it to come near the craft's plane, where the transfer needs no plane change.
    /// </param>
    public static MoonTransferPlan Plan(CelestialBody parent, CelestialBody moon, OrbitalElements craft, double earliestUt, double periapsisAltitudeM,
        bool soonest = false)
    {
        if (moon.Orbit is not { } moonOrbit || moon.ParentId != parent.Id) throw new ArgumentException($"{moon.Name} isn't a moon of {parent.Name}.");
        var mu = parent.Mu;
        var moonPeriod = Kepler.OrbitalPeriod(moonOrbit, mu);
        SoiArrival? Follow(OrbitalElements path, double from) => EnterSoi(parent, moon, path, from, from + Horizon(path, mu, moonPeriod));

        // Already heading there: fix the path from here, or leave it if it's good.
        var coasting = Follow(craft, earliestUt);
        if (OnCourse(coasting, periapsisAltitudeM)) return Finish(null, coasting!, moon, isCorrection: true, InsideNote(moon));

        var notes = new List<string>();
        double departureUt;
        List<Vector3d> seeds;
        if (coasting is not null)
        {
            // Speed up or slow down along the path as it is; or aim the rest of the way at the moon's centre, arriving when
            // it would have (when the miss is out of the path's plane). Whichever is cheaper.
            departureUt = earliestUt;
            var at = Kepler.StateAt(craft, mu, departureUt);
            var arriveUt = coasting.PeriapsisUt;
            seeds = [Vector3d.Zero];
            if (Lambert.Solve(at.Position, Kepler.StateAt(moonOrbit, mu, arriveUt).Position, arriveUt - departureUt, mu,
                    Vector3d.Cross(at.Position, at.Velocity)) is { } s)
                seeds.Add(s.DepartureVelocity - at.Velocity);
        }
        else
        {
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
            if (first is { } f && pick.Transfer.Ut > f.Transfer.Ut + window)
                notes.Add($"Waits {GameCalendar.Kerbin.FormatDuration(pick.Transfer.Ut - earliestUt)} for {moon.Name} to come near your orbit's plane: " +
                          $"leaving at the first chance costs about {f.Transfer.DeltaV - pick.Transfer.DeltaV:0} m/s more.");
            departureUt = pick.Transfer.Ut;
            seeds = [ToVector(Kepler.StateAt(craft, mu, departureUt), pick.Transfer.Burn)];
        }

        var aim = new Aim(craft, mu, departureUt, moon, periapsisAltitudeM, Follow);
        var at0 = aim.At;
        var dv = seeds.Select(seed => aim.Tune(seed, at0.Velocity.Normalized)).OfType<Vector3d>().OrderBy(v => v.Length).Cast<Vector3d?>().FirstOrDefault()
                 ?? throw new ArgumentException($"Couldn't find a burn that passes {moon.Name} at {Km(periapsisAltitudeM)}: try another height, or plan again from a rounder orbit.");
        var title = coasting is not null ? $"Course correction to {moon.Name}" : $"Transfer to {moon.Name}";
        var plan = Finish(new FlightBurn(title, departureUt, NodeBurn.From(at0, dv)), aim.ArrivalAfter(dv)!, moon, coasting is not null, InsideNote(moon));
        return plan with { Warnings = [.. notes, .. plan.Warnings] };
    }

    // ---------------- Moon to planet ----------------

    /// <summary>
    /// The escape (or correction) and capture burns from <paramref name="craft"/> (around <paramref name="moon"/>) to a
    /// circular orbit at <paramref name="periapsisAltitudeM"/> around its planet <paramref name="parent"/>, none before
    /// <paramref name="earliestUt"/>. A height in the planet's atmosphere plans no capture: the air does the braking.
    /// The escape burn is prograde, tried at <see cref="EscapePlaces"/> places round the orbit; the place where the least
    /// Δv reaches the height wins, then that's narrowed down. Throws <see cref="ArgumentException"/> when no burn reaches it.
    /// </summary>
    public static MoonTransferPlan PlanReturn(CelestialBody moon, CelestialBody parent, OrbitalElements craft, double earliestUt, double periapsisAltitudeM)
    {
        if (moon.Orbit is not { } moonOrbit || moon.ParentId != parent.Id) throw new ArgumentException($"{moon.Name} isn't a moon of {parent.Name}.");
        var mu = moon.Mu;
        var horizon = Kepler.OrbitalPeriod(moonOrbit, parent.Mu) / 2;
        SoiArrival? Follow(OrbitalElements path, double from) => LeaveSoi(moon, parent, path, from, from + horizon);
        var note = $"Once the craft has left {moon.Name}'s sphere of influence, plan Circularize at periapsis again from here for the exact burn.";

        var coasting = Follow(craft, earliestUt);
        if (OnCourse(coasting, periapsisAltitudeM)) return Finish(null, coasting!, parent, isCorrection: true, note);

        if (coasting is not null)
        {
            // On the way out already: speed up or slow down, or push sideways (which swings where it leaves); the cheaper.
            var aim = new Aim(craft, mu, earliestUt, parent, periapsisAltitudeM, Follow);
            var at = aim.At;
            var sideways = Vector3d.Cross(Vector3d.Cross(at.Position, at.Velocity), at.Velocity).Normalized;
            var dv = new[] { at.Velocity.Normalized, sideways }.Select(d => aim.Tune(Vector3d.Zero, d)).OfType<Vector3d>()
                         .OrderBy(v => v.Length).Cast<Vector3d?>().FirstOrDefault()
                     ?? throw new ArgumentException($"Couldn't find a correction that brings you to {Km(periapsisAltitudeM)} over {parent.Name}.");
            return Finish(new FlightBurn($"Course correction to {parent.Name}", earliestUt, NodeBurn.From(at, dv)), aim.ArrivalAfter(dv)!, parent,
                isCorrection: true, note);
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
        return Finish(new FlightBurn($"Leave {moon.Name} for {parent.Name}", bestUt, NodeBurn.From(best.Aim.At, burnDv)), best.Aim.ArrivalAfter(burnDv)!,
            parent, isCorrection: false, note);
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
            return (a + b) / 2;
        }
    }

    private static bool OnCourse(SoiArrival? arrival, double altitudeM) =>
        arrival is { Prograde: true } a && Math.Abs(a.PeriapsisAltitudeM - altitudeM) < OnCourseM;

    private static string InsideNote(CelestialBody moon) =>
        $"Once the craft is inside {moon.Name}'s sphere of influence, plan Circularize at periapsis again from here for the exact burn.";

    /// <summary>The capture burn at the destination's periapsis (none in its atmosphere), and what to watch out for.</summary>
    private static MoonTransferPlan Finish(FlightBurn? transfer, SoiArrival arrival, CelestialBody destination, bool isCorrection, string captureNote)
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
        return new MoonTransferPlan(transfer, capture, arrival, warnings) { IsCorrection = isCorrection };
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
