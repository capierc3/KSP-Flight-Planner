using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core;

public enum HopKind
{
    /// <summary>From a body to one of its moons (or from the star to a planet).</summary>
    Down,
    /// <summary>From a moon to its planet.</summary>
    Up,
    /// <summary>Between two bodies with the same parent, in a launch window.</summary>
    Across,
}

/// <summary>One step of a trip: from <paramref name="From"/>'s orbit to <paramref name="To"/>'s, through <paramref name="Parent"/>'s sphere.</summary>
public sealed record Hop(HopKind Kind, CelestialBody From, CelestialBody To, CelestialBody Parent)
{
    public override string ToString() => $"{Kind} {From.Name} → {To.Name}";
}

/// <summary>One hop of a trip, planned, ending in an orbit at <see cref="AltitudeM"/> around its destination.</summary>
public sealed record TripLeg(Hop Hop, HopPlan Plan, double AltitudeM);

/// <summary>
/// One way of making a trip: every leg's burns, in order. Only the first leg is planned from where the craft really is;
/// the later ones from where the earlier ones leave it, so they're planned again from here on arrival.
/// </summary>
/// <param name="Label">"Cheapest", "Faster", "Leave at the first chance", or "" when there's only one way.</param>
public sealed record TripOption(string Label, IReadOnlyList<TripLeg> Legs)
{
    public IReadOnlyList<FlightBurn> Burns => Legs.SelectMany(l => l.Plan.Burns).ToList();

    public double DeltaV => Burns.Sum(b => b.DeltaV);

    /// <summary>The first burn's time; NaN when there's nothing to burn.</summary>
    public double DepartureUt => Burns.Count > 0 ? Burns[0].Ut : double.NaN;

    /// <summary>When the trip reaches its destination: the last leg's periapsis there.</summary>
    public double ArrivalUt => Legs[^1].Plan.Arrival.PeriapsisUt;

    public SoiArrival Arrival => Legs[^1].Plan.Arrival;

    public IReadOnlyList<string> Warnings => Legs.SelectMany(l => l.Plan.Warnings).Distinct().ToList();

    public bool IsCorrection => Legs[0].Plan.IsCorrection;
}

/// <summary>
/// Trips from where the craft is to any body: the chain of hops through the tree of bodies (up to the common parent,
/// across in a launch window, down to the destination), each planned from where the last one leaves the craft, and the
/// ways of making it worth choosing between. Intermediate stops are a circular orbit at the body's usual parking height.
/// docs/MATH.md §3.8.
/// </summary>
public static class TripPlanner
{
    /// <summary>A "faster" window may cost up to this much more than the cheapest (porkchop Δv).</summary>
    public const double FasterCostRatio = 1.25;

    /// <summary>The hops from <paramref name="from"/> to <paramref name="to"/>. Throws when there's no way (the star, the same body).</summary>
    public static IReadOnlyList<Hop> Route(CelestialBody from, CelestialBody to, IReadOnlyCollection<CelestialBody> bodies)
    {
        if (from.Id == to.Id) throw new ArgumentException($"You're already around {to.Name}.");
        if (to.ParentId is null) throw new ArgumentException($"Going to {to.Name} itself isn't a trip the planner makes.");
        var byId = bodies.ToDictionary(b => b.Id);
        List<CelestialBody> Chain(CelestialBody b)
        {
            var chain = new List<CelestialBody> { b };
            while (chain[^1].ParentId is { } p && byId.TryGetValue(p, out var parent)) chain.Add(parent);
            return chain;
        }

        var up = Chain(from);
        var down = Chain(to);
        var common = up.FirstOrDefault(b => down.Any(d => d.Id == b.Id)) ?? throw new ArgumentException($"{from.Name} and {to.Name} aren't in the same system.");
        up = up.TakeWhile(b => b.Id != common.Id).ToList();          // from … child of the common body
        down = down.TakeWhile(b => b.Id != common.Id).Reverse().ToList(); // child of the common body … to

        var hops = new List<Hop>();
        // Up to just below the common body, unless the trip goes all the way up to it.
        var upTo = down.Count == 0 ? up.Count : up.Count - 1;
        for (var i = 0; i < upTo; i++) hops.Add(new Hop(HopKind.Up, up[i], i + 1 < up.Count ? up[i + 1] : common, i + 1 < up.Count ? up[i + 1] : common));
        if (down.Count > 0)
        {
            if (up.Count > 0) hops.Add(new Hop(HopKind.Across, up[^1], down[0], common));
            else hops.Add(new Hop(HopKind.Down, common, down[0], common));
            for (var i = 1; i < down.Count; i++) hops.Add(new Hop(HopKind.Down, down[i - 1], down[i], down[i - 1]));
        }
        return hops;
    }

    /// <summary>
    /// The ways of going from <paramref name="craft"/> (around <paramref name="from"/>) to a circular orbit at
    /// <paramref name="altitudeM"/> around <paramref name="to"/>, nothing before <paramref name="earliestUt"/>. A trip with a
    /// launch window gets up to three (the cheapest window, a faster one, and leaving at the first chance); one to a tilted
    /// moon gets the cheapest and leaving at once when those differ; otherwise there's one. Throws
    /// <see cref="ArgumentException"/> when the trip can't be planned.
    /// </summary>
    public static IReadOnlyList<TripOption> Options(CelestialBody from, CelestialBody to, OrbitalElements craft, double earliestUt, double altitudeM,
        IReadOnlyCollection<CelestialBody> bodies)
    {
        var hops = Route(from, to, bodies);
        double Height(int i) => i == hops.Count - 1 ? altitudeM : hops[i].To.DefaultParkingAltitudeM;
        var across = hops.ToList().FindIndex(h => h.Kind == HopKind.Across);

        if (across < 0)
        {
            var cheapest = Legs(hops, 0, craft, earliestUt, Height, soonest: false, window: null);
            if (!cheapest.Legs.Any(l => l.Plan.Waits)) return [new TripOption("", cheapest.Legs)];
            var now = Legs(hops, 0, craft, earliestUt, Height, soonest: true, window: null);
            return Label([new TripOption("", cheapest.Legs), new TripOption("", now.Legs)]);
        }

        // The legs up to the window are the same whichever window is taken.
        var before = Legs(hops.Take(across).ToList(), 0, craft, earliestUt, Height, soonest: false, window: null);
        var hop = hops[across];
        var startOrbit = before.EndOrbit ?? craft;
        var startUt = before.EndUt;
        var options = new List<TripOption>();
        string? problem = null;
        foreach (var (_, dep, tof) in Windows(hop, startOrbit, startUt, Height(across)))
        {
            try
            {
                var rest = Legs(hops, across, startOrbit, startUt, Height, soonest: false, window: (dep, tof));
                options.Add(new TripOption("", [.. before.Legs, .. rest.Legs]));
            }
            catch (ArgumentException ex) { problem ??= ex.Message; }
        }
        if (options.Count == 0) throw new ArgumentException(problem ?? $"No way to {to.Name} found.");
        return Label(options);
    }

    /// <summary>
    /// Names each option for what it's best at, from its planned burns: the cheapest, the soonest to arrive, the shortest
    /// flight (time in space), the soonest to leave. One best at nothing is dropped; the cheapest comes first. A single
    /// option keeps no name.
    /// </summary>
    private static IReadOnlyList<TripOption> Label(IReadOnlyList<TripOption> options)
    {
        static double Leaves(TripOption o) => double.IsNaN(o.DepartureUt) ? double.NegativeInfinity : o.DepartureUt;
        var cheapest = options.MinBy(o => o.DeltaV)!;
        var arrives = options.MinBy(o => o.ArrivalUt)!;
        var shortest = options.MinBy(o => o.ArrivalUt - Leaves(o))!;
        var leaves = options.MinBy(Leaves)!;
        var named = options.Select(o =>
            {
                var bests = new[]
                {
                    o == cheapest ? "cheapest" : null,
                    o == arrives ? "arrives soonest" : null,
                    o == shortest ? "shortest flight" : null,
                    o == leaves ? "leaves soonest" : null,
                }.OfType<string>().ToList();
                var label = string.Join(" · ", bests);
                return o with { Label = label.Length == 0 ? "" : char.ToUpperInvariant(label[0]) + label[1..] };
            })
            .Where(o => o.Label.Length > 0).OrderBy(o => o.DeltaV).ToList();
        return named.Count == 1 ? [named[0] with { Label = "" }] : named;
    }

    /// <summary>Plans <paramref name="hops"/> from index <paramref name="start"/> on, each from where the last leaves the craft.</summary>
    private static (List<TripLeg> Legs, OrbitalElements? EndOrbit, double EndUt) Legs(IReadOnlyList<Hop> hops, int start, OrbitalElements craft, double earliestUt,
        Func<int, double> height, bool soonest, (double Departure, double FlightTime)? window)
    {
        var legs = new List<TripLeg>();
        OrbitalElements? orbit = craft;
        var ut = earliestUt;
        for (var i = start; i < hops.Count; i++)
        {
            var hop = hops[i];
            if (orbit is null)
                throw new ArgumentException($"The stop at {hops[i - 1].To.Name} is in its atmosphere, so the trip can't go on from there.");
            var plan = hop.Kind switch
            {
                HopKind.Down => Hops.Down(hop.From, hop.To, orbit, ut, height(i), soonest),
                HopKind.Up => Hops.Up(hop.From, hop.To, orbit, ut, height(i)),
                _ => window is { } w
                    ? Hops.Across(hop.From, hop.To, hop.Parent, orbit, ut, height(i), w.Departure, w.FlightTime)
                    : throw new ArgumentException("A hop across needs a launch window."),
            };
            if (i > 0) plan = plan with { Departure = plan.Departure.Select(b => b with { Note = Later(b.Note, hop) }).ToList() };
            legs.Add(new TripLeg(hop, plan, height(i)));
            orbit = Hops.Captured(plan, hop.To);
            ut = (plan.Capture?.Ut ?? plan.Arrival.PeriapsisUt) + Rendezvous.BurnGapS;
        }
        return (legs, orbit, ut);
    }

    private static string Later(string? note, Hop hop) =>
        $"Planned from where the trip should leave you around {hop.From.Name}: once there, plan Go to {hop.To.Name} again from here." +
        (note is null ? "" : " " + note);

    /// <summary>
    /// The windows worth offering for a hop across, from the Transfer Planner's porkchop plot (§3.4) searched from
    /// <paramref name="earliestUt"/>: the cheapest; the fastest costing at most <see cref="FasterCostRatio"/> times as much;
    /// and the cheapest leaving at the first chance. Near-duplicates are dropped.
    /// </summary>
    public static IReadOnlyList<(string Label, double DepartureUt, double FlightTimeS)> Windows(Hop hop, OrbitalElements craft, double earliestUt,
        double arrivalAltitudeM)
    {
        var parking = Math.Max(0, craft.SemiMajorAxisM - hop.From.EquatorialRadiusM);
        var request = new TransferRequest(hop.From, hop.To, hop.Parent, parking, arrivalAltitudeM, earliestUt);
        var plan = TransferPlanner.Plan(request, 80, 60);
        var best = plan.BestWindow;
        var grid = plan.Porkchop!;
        var windows = new List<(string, double, double)> { ("Cheapest", best.DepartureUt, best.FlightTimeS) };

        // Faster: the shortest flight among the cells within the cost limit.
        var limit = best.TotalDv * FasterCostRatio;
        (int C, int R)? fastest = null;
        for (var c = 0; c < grid.Columns; c++)
        for (var r = 0; r < grid.Rows; r++)
            if (grid[c, r] <= limit && (fastest is not { } f || r < f.R || r == f.R && grid[c, r] < grid[f.C, f.R]))
                fastest = (c, r);
        if (fastest is { } fc && grid.FlightTimeAt(fc.R) < best.FlightTimeS * 0.9)
            windows.Add(("Faster", grid.DepartureAt(fc.C), grid.FlightTimeAt(fc.R)));

        // Leave at the first chance: the cheapest flight time leaving now, narrowed down.
        if (best.DepartureUt - earliestUt > 0.05 * plan.Hohmann.SynodicPeriodS)
        {
            double Cost(double tof) => TransferPlanner.Evaluate(request, earliestUt, tof)?.TotalDv ?? double.PositiveInfinity;
            var step = (grid.FlightTimeMaxS - grid.FlightTimeMinS) / (grid.Rows - 1);
            var tofs = Enumerable.Range(0, grid.Rows).Select(grid.FlightTimeAt).Where(t => double.IsFinite(Cost(t))).ToList();
            if (tofs.Count > 0)
            {
                var tof = tofs.MinBy(Cost);
                for (var round = 0; round < 6; round++, step /= 4)
                    tof = Enumerable.Range(-4, 9).Select(i => Math.Max(60, tof + i * step / 4)).MinBy(Cost);
                windows.Add(("Leave at the first chance", earliestUt, tof));
            }
        }
        return windows;
    }
}
