namespace KspFlightPlanner.Core;

/// <summary>A rocket phase (stage group) that has to supply the Δv of the legs assigned to it.</summary>
public sealed record PhaseDefinition(string Name, double MarginPercent = 10, double ExtraDv = 0);

/// <summary>Per-leg choices: which phase flies it and how.</summary>
public sealed record LegSetting(int PhaseIndex, bool UseAerobrake, bool IncludePlaneChange);

/// <summary>
/// A place the mission visits, with an optional note ("Dock at station, pick up Jeb") and the tools of the leg that
/// arrives here: <paramref name="Transfer"/> makes it a transfer leg flown in a chosen launch window,
/// <paramref name="Rendezvous"/> a rendezvous leg that meets the craft at this stop. <paramref name="Deploy"/> is a step at
/// the stop itself: releasing a satellite constellation from a resonant orbit.
/// </summary>
public sealed record MissionStop(string NodeKey, string? Note = null, TransferLeg? Transfer = null, DeployStep? Deploy = null,
    RendezvousLeg? Rendezvous = null);

/// <summary>
/// A saved mission: ordered stops plus phase/leg choices, the route-wide options, the design checked against it
/// (by .craft file path) and free-form notes. <paramref name="Places"/> are the mission's own places its stops can be
/// at (crafts met, constellation orbits), kept here rather than on the Locations tab. Serialised to the database as JSON; added fields need defaults so older
/// missions still read.
/// </summary>
public sealed record MissionDefinition(
    IReadOnlyList<MissionStop> Stops,
    IReadOnlyList<PhaseDefinition> Phases,
    IReadOnlyList<LegSetting> Legs,
    bool UseAerobraking = true,
    bool IncludePlaneChanges = false,
    string? DesignPath = null,
    string? Notes = null,
    IReadOnlyList<CustomLocation>? Places = null);

/// <summary>The legs flown to reach stop <see cref="ToStop"/> from the previous stop.</summary>
public sealed record RouteSegment(int ToStop, IReadOnlyList<RouteLeg> Legs);

public sealed record PhaseSummary(PhaseDefinition Phase, double RequiredDv, double MarginDv)
{
    public double TotalDv => RequiredDv + MarginDv;
}

public static class MissionPlanner
{
    /// <summary>One segment per consecutive pair of stops (segment i ends at stop i + 1).</summary>
    public static IReadOnlyList<RouteSegment> BuildRoute(DeltaVMap map, IReadOnlyList<DvMapNode> stops) =>
        Enumerable.Range(1, Math.Max(0, stops.Count - 1))
            .Select(i => new RouteSegment(i, map.Route(stops[i - 1], stops[i])))
            .ToList();

    /// <summary>
    /// Flies "surface → low orbit → higher orbit" as one leg (and the same down): a launch goes straight to the orbit
    /// it wants rather than parking in the low orbit first. The Δv is the two map edges added (the low orbit's climb
    /// plus the Hohmann raise), which is what the launch costs either way. Only orbits <paramref name="isOrbit"/> picks
    /// are joined, and only where the low orbit isn't a stop and no leg tool supplies either edge.
    /// </summary>
    public static IReadOnlyList<RouteSegment> CombineClimbs(IReadOnlyList<RouteSegment> segments, Func<DvMapNode, bool> isOrbit) =>
        segments.Select(segment =>
        {
            var legs = new List<RouteLeg>();
            foreach (var leg in segment.Legs)
            {
                if (legs.Count > 0 && legs[^1] is { IsCombined: false, IsFromTool: false } previous && !leg.IsFromTool
                    && previous.To.Kind == "LowOrbit" && leg.From == previous.To
                    && (previous.From.Kind == "Surface" && isOrbit(leg.To) || isOrbit(previous.From) && leg.To.Kind == "Surface"))
                    legs[^1] = Combine(previous, leg);
                else legs.Add(leg);
            }
            return legs.Count == segment.Legs.Count ? segment : segment with { Legs = legs };
        }).ToList();

    /// <summary>
    /// Flies "craft → low orbit → craft" around one body as one leg. The map reaches a craft only through its body's low
    /// orbit, but going from one craft to the next you never stop there: the leg is a rendezvous (§6.5), so a low orbit
    /// in the middle would read like a stop the mission makes. <paramref name="isCraft"/> picks the craft.
    /// </summary>
    public static IReadOnlyList<RouteSegment> CombineCraftHops(IReadOnlyList<RouteSegment> segments, Func<DvMapNode, bool> isCraft) =>
        segments.Select(segment =>
        {
            var legs = segment.Legs;
            if (legs.Count < 2 || legs[0].From == legs[^1].To || !isCraft(legs[0].From) || !isCraft(legs[^1].To)
                || legs.Any(l => l.IsFromTool || l.From.BodyName != legs[0].From.BodyName || l.To.BodyName != legs[0].From.BodyName))
                return segment;
            return segment with { Legs = [Combine(legs.SelectMany(l => l.Parts ?? [l]).ToList())] };
        }).ToList();

    private static RouteLeg Combine(RouteLeg a, RouteLeg b) => Combine([a, b]);

    private static RouteLeg Combine(IReadOnlyList<RouteLeg> parts) => new(
        parts[0].From, parts[^1].To, parts[0].EdgeKey, parts.Sum(p => p.DeltaV), parts.Sum(p => p.MapDeltaV), parts.Sum(p => p.PlaneChangeDv),
        CanAerobrake: parts.Any(p => p.CanAerobrake),
        FlightTime: null,
        // Aerobraking replaces what it can on each part; the rest is still burned.
        AerobrakeDv: parts.Sum(p => p.CanAerobrake ? p.AerobrakeDv : p.DeltaV),
        Parts: parts);

    public static IReadOnlyList<RouteLeg> AllLegs(IReadOnlyList<RouteSegment> segments) =>
        segments.SelectMany(s => s.Legs).ToList();

    /// <summary>Δv this leg actually costs with the chosen options.</summary>
    public static double EffectiveDv(RouteLeg leg, LegSetting setting) =>
        setting.UseAerobrake && leg.CanAerobrake ? leg.AerobrakeDv
        : leg.DeltaV + (setting.IncludePlaneChange ? leg.PlaneChangeDv : 0);

    /// <summary>
    /// Default split. A trip "heads home" when it has an intermediate stop and ends on the start body (its surface
    /// or an orbit around it), e.g. Kerbin surface → Mun orbit → Kerbin surface:
    /// <list type="bullet">
    /// <item>Launch — from a surface start until the first low orbit (or a custom orbit around the same body).</item>
    /// <item>Lander — heading home after landing at the last intermediate stop: everything after it (ascent and
    /// trip home). On a one-way trip ending on a surface: just the final descent.</item>
    /// <item>Return — heading home without that landing: everything after the last intermediate stop.</item>
    /// <item>Transfer — everything else, so a one-way trip through stops (Kerbin surface → Kerbin LO → Duna orbit)
    /// is Launch / Transfer.</item>
    /// </list>
    /// Only phases that fly at least one leg are created (a route with no legs gets a single Transfer phase).
    /// </summary>
    public static (IReadOnlyList<PhaseDefinition> Phases, IReadOnlyList<LegSetting> Legs) DefaultPhases(
        IReadOnlyList<RouteSegment> segments, IReadOnlyList<DvMapNode> stops, bool useAerobraking = true)
    {
        var legs = AllLegs(segments);

        // Launch: leading legs from a surface into orbit, including raising to a custom orbit (e.g. a station)
        // around the same body.
        var launchBody = legs.Count > 0 ? legs[0].From.BodyName : null;
        var launchCount = legs.Count > 0 && legs[0].From.Kind == "Surface"
            ? legs.TakeWhile(l => l.From.Kind == "Surface"
                || (l.From.Kind == "LowOrbit" && l.To.Kind == "Custom" && l.To.BodyName == launchBody)).Count()
            : 0;

        // The trip heads home when it ends on the start body (surface or orbit) after an intermediate stop;
        // homewardStart is the first leg after the last intermediate stop (stops 1 .. n-2).
        var lastIntermediate = stops.Count > 2 ? stops.Count - 2 : -1;
        var headsHome = lastIntermediate > 0 && stops[^1].BodyName == stops[0].BodyName;
        var homewardStart = headsHome
            ? segments.Where(s => s.ToStop <= lastIntermediate).Sum(s => s.Legs.Count)
            : legs.Count;
        var landedBeforeHome = headsHome && stops[lastIntermediate].Kind == "Surface";
        var finalLanding = !headsHome && stops.Count >= 2 && stops[^1].Kind == "Surface";

        var names = Enumerable.Range(0, legs.Count).Select(i =>
            i < launchCount ? "Launch"
            : finalLanding && i == legs.Count - 1 ? "Lander"
            : i >= homewardStart ? (landedBeforeHome ? "Lander" : "Return")
            : "Transfer").ToList();

        // Only the phases some leg flies, in burn order.
        var phases = new[] { "Launch", "Transfer", "Lander", "Return" }
            .Where(names.Contains).Select(n => new PhaseDefinition(n)).ToList();
        if (phases.Count == 0) phases.Add(new PhaseDefinition("Transfer"));

        var settings = names.Select((name, i) => new LegSetting(
            phases.FindIndex(p => p.Name == name), useAerobraking && AerobrakeByDefault(legs, i), false)).ToList();
        return (phases, settings);
    }

    /// <summary>
    /// Aerobraking down to a low orbit only makes sense by default when the trip continues to the surface
    /// (re-entry). Dipping into the atmosphere to reach low orbit and then leaving it again is left to the user.
    /// </summary>
    private static bool AerobrakeByDefault(IReadOnlyList<RouteLeg> legs, int index)
    {
        if (legs[index].To.Kind != "LowOrbit") return true;
        return index + 1 >= legs.Count || legs[index + 1].To.Kind == "Surface";
    }

    /// <param name="steps">Δv of steps at stops (e.g. a constellation deployment), each charged to a phase.</param>
    public static IReadOnlyList<PhaseSummary> Summarize(
        IReadOnlyList<PhaseDefinition> phases, IReadOnlyList<RouteLeg> legs, IReadOnlyList<LegSetting> settings,
        IEnumerable<(int PhaseIndex, double DeltaV)>? steps = null)
    {
        if (legs.Count != settings.Count) throw new ArgumentException("Each leg needs a setting.");

        var required = new double[phases.Count];
        for (var i = 0; i < legs.Count; i++)
            required[Math.Clamp(settings[i].PhaseIndex, 0, phases.Count - 1)] += EffectiveDv(legs[i], settings[i]);
        foreach (var (phase, dv) in steps ?? [])
            required[Math.Clamp(phase, 0, phases.Count - 1)] += dv;

        return phases.Select((p, i) =>
            new PhaseSummary(p, required[i], required[i] * p.MarginPercent / 100 + p.ExtraDv)).ToList();
    }
}
