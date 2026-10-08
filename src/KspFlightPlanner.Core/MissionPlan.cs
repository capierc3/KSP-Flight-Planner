namespace KspFlightPlanner.Core;

/// <summary>A rocket phase (stage group) that has to supply the Δv of the legs assigned to it.</summary>
public sealed record PhaseDefinition(string Name, double MarginPercent = 10, double ExtraDv = 0);

/// <summary>Per-leg choices: which phase flies it and how.</summary>
public sealed record LegSetting(int PhaseIndex, bool UseAerobrake, bool IncludePlaneChange);

/// <summary>
/// A place the mission visits, with an optional note ("Dock at station, pick up Jeb") and the tools of the leg that
/// arrives here: <paramref name="Transfer"/> makes it a transfer leg flown in a chosen launch window.
/// </summary>
public sealed record MissionStop(string NodeKey, string? Note = null, TransferLeg? Transfer = null);

/// <summary>
/// A saved mission: ordered stops plus phase/leg choices, the route-wide options, the design checked against it
/// (by .craft file path) and free-form notes. Serialised to the database as JSON; added fields need defaults so older
/// missions still read.
/// </summary>
public sealed record MissionDefinition(
    IReadOnlyList<MissionStop> Stops,
    IReadOnlyList<PhaseDefinition> Phases,
    IReadOnlyList<LegSetting> Legs,
    bool UseAerobraking = true,
    bool IncludePlaneChanges = false,
    string? DesignPath = null,
    string? Notes = null);

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

    public static IReadOnlyList<RouteLeg> AllLegs(IReadOnlyList<RouteSegment> segments) =>
        segments.SelectMany(s => s.Legs).ToList();

    /// <summary>Δv this leg actually costs with the chosen options.</summary>
    public static double EffectiveDv(RouteLeg leg, LegSetting setting) =>
        setting.UseAerobrake && leg.CanAerobrake ? leg.AerobrakeDv
        : leg.DeltaV + (setting.IncludePlaneChange ? leg.PlaneChangeDv : 0);

    /// <summary>
    /// Default split:
    /// <list type="bullet">
    /// <item>Launch — from a surface start until the first low orbit.</item>
    /// <item>Lander — everything after the last intermediate stop on a surface (ascent and trip home);
    /// for a one-way trip ending on a surface, just the final descent.</item>
    /// <item>Return — if there is no landing, everything after the last intermediate stop.</item>
    /// <item>Transfer — everything else (including the descent to a landing site).</item>
    /// </list>
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

        // Index of the first leg after the last intermediate stop (stops 1 .. n-2).
        var lastIntermediate = stops.Count > 2 ? stops.Count - 2 : -1;
        var homewardStart = lastIntermediate > 0
            ? segments.Where(s => s.ToStop <= lastIntermediate).Sum(s => s.Legs.Count)
            : legs.Count;
        var landedBeforeHome = lastIntermediate > 0 && stops[lastIntermediate].Kind == "Surface";
        var oneWayLanding = stops.Count == 2 && stops[1].Kind == "Surface" && legs.Count > launchCount;

        var phases = new List<PhaseDefinition>();
        int Add(string name) { phases.Add(new PhaseDefinition(name)); return phases.Count - 1; }

        var launch = launchCount > 0 ? Add("Launch") : -1;
        var transfer = legs.Count > launchCount ? Add("Transfer") : -1;
        var lander = landedBeforeHome || oneWayLanding ? Add("Lander") : -1;
        var homeward = lastIntermediate > 0 && !landedBeforeHome && homewardStart < legs.Count ? Add("Return") : -1;
        if (phases.Count == 0) Add("Transfer");

        var settings = new List<LegSetting>();
        for (var i = 0; i < legs.Count; i++)
        {
            var phase = i < launchCount ? launch
                : lander >= 0 && oneWayLanding && i == legs.Count - 1 ? lander
                : i >= homewardStart && lander >= 0 && landedBeforeHome ? lander
                : i >= homewardStart && homeward >= 0 ? homeward
                : transfer >= 0 ? transfer : 0;
            settings.Add(new LegSetting(phase, useAerobraking && AerobrakeByDefault(legs, i), false));
        }
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

    public static IReadOnlyList<PhaseSummary> Summarize(
        IReadOnlyList<PhaseDefinition> phases, IReadOnlyList<RouteLeg> legs, IReadOnlyList<LegSetting> settings)
    {
        if (legs.Count != settings.Count) throw new ArgumentException("Each leg needs a setting.");

        var required = new double[phases.Count];
        for (var i = 0; i < legs.Count; i++)
            required[Math.Clamp(settings[i].PhaseIndex, 0, phases.Count - 1)] += EffectiveDv(legs[i], settings[i]);

        return phases.Select((p, i) =>
            new PhaseSummary(p, required[i], required[i] * p.MarginPercent / 100 + p.ExtraDv)).ToList();
    }
}
