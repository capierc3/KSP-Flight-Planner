using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core;

/// <summary>
/// The launch window chosen for a transfer leg: the earliest date searched from, and the departure and flight time
/// picked (the best window, or a point clicked on the porkchop plot). The Δv is worked out again from these on load.
/// </summary>
public sealed record TransferLeg(double EarliestDepartureUt, double DepartureUt, double FlightTimeS);

/// <summary>
/// Where a transfer window fits in one leg's Δv-map steps: from the parking orbit around <see cref="Origin"/> out to
/// <see cref="Destination"/> (both orbiting <see cref="Parent"/>), and down to its low orbit if the leg goes there.
/// Steps [<see cref="EjectionStart"/>, <see cref="EjectionEnd"/>] are the ejection burn; steps
/// [<see cref="CaptureStart"/>, <see cref="CaptureEnd"/>] the capture burn (none when the leg doesn't stop in low orbit).
/// </summary>
public sealed record TransferSpan(
    CelestialBody Origin,
    CelestialBody Destination,
    CelestialBody Parent,
    double ParkingAltitudeM,
    double? ArrivalAltitudeM,
    int EjectionStart,
    int EjectionEnd,
    int CaptureStart,
    int CaptureEnd)
{
    public bool Captures => ArrivalAltitudeM is not null;

    public TransferRequest Request(double earliestDepartureUt) => new(Origin, Destination, Parent, ParkingAltitudeM,
        ArrivalAltitudeM ?? Destination.DefaultParkingAltitudeM, earliestDepartureUt,
        Captures ? ArrivalMode.Circularize : ArrivalMode.Flyby);
}

/// <summary>A transfer leg worked out: where it applies and the window flown.</summary>
public sealed record TransferLegResult(TransferSpan Span, TransferWindow Window);

/// <summary>
/// A rendezvous leg as planned on the Rendezvous tab: meeting the placed custom location (or imported vessel)
/// <paramref name="TargetLocationId"/>, starting at <paramref name="StartUt"/>. Where craft are depends on the date and the
/// save, so the result is kept as planned: <paramref name="OrbitalDv"/> (plane match, phasing, transfer and match burns)
/// and <paramref name="ArrivalUt"/>. The picks (launch, phasing orbit, porkchop point; null = the planner's) let the
/// Rendezvous tab show the same plan again.
/// </summary>
public sealed record RendezvousLeg(
    long TargetLocationId,
    double StartUt,
    double ArrivalUt,
    double OrbitalDv,
    int? LaunchIndex = null,
    PhasingChoice? Phasing = null,
    InterceptPoint? Intercept = null);

/// <summary>
/// Where a rendezvous fits in a leg's Δv-map steps: steps from <see cref="Start"/> on are the orbital part (the launch
/// before it stays the map's). <see cref="ChaserLocationId"/> is the craft it starts from; null for a launch from KSC.
/// </summary>
public sealed record RendezvousSpan(string BodyName, long TargetLocationId, long? ChaserLocationId, int Start)
{
    public bool IsLaunch => ChaserLocationId is null;
}

/// <summary>
/// Releasing <paramref name="SatelliteCount"/> satellites, evenly spaced, into the circular orbit of the stop, from a
/// resonant (or dive) carrier orbit. <paramref name="PhaseIndex"/> is the rocket phase that flies the carrier's burns;
/// null for the phase of the leg arriving at the stop.
/// </summary>
public sealed record DeployStep(int SatelliteCount, bool PreferDiveOrbit = false, bool UseOcclusion = false, int? PhaseIndex = null);

/// <summary>
/// A deployment worked out. The carrier burns <see cref="EnterDv"/> into the carrier orbit and, if the mission goes on,
/// <see cref="ReturnDv"/> back to the circular orbit after the last release. Each satellite burns
/// <see cref="SatelliteDv"/> itself to circularize; that comes from the satellites, not the rocket.
/// </summary>
public sealed record DeployResult(CelestialBody Body, double AltitudeM, ResonantOrbitResult Orbit, double EnterDv, double ReturnDv)
{
    public double CarrierDv => EnterDv + ReturnDv;
    public double SatelliteDv => Orbit.InjectionDeltaV;
}

/// <summary>
/// Tools that belong to a mission's legs. A tool replaces the Δv-map values of the steps it covers with its own result,
/// keeping the steps themselves (so per-step choices such as aerobraking and phases stay where they were).
/// See docs/MATH.md §19.
/// </summary>
public static class LegTools
{
    /// <summary>
    /// Finds the interplanetary part of a leg: the step(s) between two bodies orbiting the same parent (Kerbin → Duna),
    /// the climb out from the origin's low orbit before it and the drop into the destination's low orbit after it.
    /// Null when the leg doesn't cross between sibling bodies or doesn't start from the origin's low orbit.
    /// </summary>
    public static TransferSpan? FindTransfer(IReadOnlyList<RouteLeg> legs, IReadOnlyDictionary<string, CelestialBody> bodies)
    {
        var first = -1;
        var last = -1;
        for (var i = 0; i < legs.Count; i++)
        {
            if (!AreSiblings(legs[i].From.BodyName, legs[i].To.BodyName, bodies)) continue;
            if (first < 0) first = i;
            last = i;
        }
        if (first < 0) return null;

        var origin = bodies[legs[first].From.BodyName];
        var destination = bodies[legs[last].To.BodyName];
        if (origin.Id == destination.Id || origin.ParentId != destination.ParentId) return null;
        var parent = bodies.Values.FirstOrDefault(b => b.Id == origin.ParentId);
        if (parent is null || origin.Orbit is null || destination.Orbit is null) return null;

        // Back from the crossing to the step that leaves the origin's low orbit (the parking orbit).
        var start = first;
        while (start > 0 && legs[start].From.Kind != "LowOrbit" && legs[start - 1].From.BodyName == origin.Name) start--;
        if (legs[start].From.Kind != "LowOrbit" || legs[start].From.BodyName != origin.Name
            || legs[start].From.AltitudeM is not { } parking) return null;

        // On from the crossing to the step that arrives in the destination's low orbit, if the leg goes there.
        var captureEnd = -1;
        for (var i = last + 1; i < legs.Count && legs[i].To.BodyName == destination.Name; i++)
        {
            if (legs[i].To.Kind != "LowOrbit") continue;
            captureEnd = i;
            break;
        }
        var arrival = captureEnd >= 0 ? legs[captureEnd].To.AltitudeM : null;
        if (arrival is null) captureEnd = -1;

        return new TransferSpan(origin, destination, parent, parking, arrival, start, last,
            captureEnd >= 0 ? last + 1 : -1, captureEnd);
    }

    /// <summary>The window for a saved choice, or null if the leg no longer has a transfer (or no solution).</summary>
    public static TransferLegResult? EvaluateTransfer(IReadOnlyList<RouteLeg> legs, TransferLeg choice,
        IReadOnlyDictionary<string, CelestialBody> bodies)
    {
        if (FindTransfer(legs, bodies) is not { } span) return null;
        try
        {
            return TransferPlanner.Evaluate(span.Request(choice.EarliestDepartureUt), choice.DepartureUt, choice.FlightTimeS) is { } window
                ? new TransferLegResult(span, window)
                : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The leg's steps with the window's Δv in place of the map's: the whole ejection burn on the first climb-out step
    /// and the capture burn on the first step down to low orbit; the other steps they cover cost nothing extra. The
    /// window's burns already point the right way in 3-D, so no plane change is added to them.
    /// </summary>
    public static IReadOnlyList<RouteLeg> ApplyTransfer(IReadOnlyList<RouteLeg> legs, TransferLegResult transfer)
    {
        var (span, window) = transfer;
        var result = legs.ToList();
        for (var i = span.EjectionStart; i <= span.EjectionEnd; i++)
            result[i] = legs[i] with
            {
                DeltaV = i == span.EjectionStart ? Math.Round(window.EjectionDv) : 0,
                PlaneChangeDv = 0,
                CanAerobrake = false,
                AerobrakeDv = 0,
                ToolNote = i == span.EjectionStart ? "Ejection burn of the chosen transfer window" : "Part of the ejection burn",
            };
        if (span.Captures)
            for (var i = span.CaptureStart; i <= span.CaptureEnd; i++)
                result[i] = legs[i] with
                {
                    DeltaV = i == span.CaptureStart ? Math.Round(window.CaptureDv) : 0,
                    PlaneChangeDv = 0,
                    ToolNote = i == span.CaptureStart ? "Capture burn of the chosen transfer window" : "Part of the capture burn",
                };
        return result;
    }

    /// <summary>
    /// Applies each stop's leg tools to the leg arriving there (segment i ends at stop i + 1). Tools that no longer fit
    /// the route (stops changed since) are left out, and the map values stay.
    /// </summary>
    public static IReadOnlyList<RouteSegment> Apply(IReadOnlyList<RouteSegment> segments, IReadOnlyList<MissionStop> stops,
        IReadOnlyDictionary<string, CelestialBody> bodies) =>
        segments.Select(s =>
            s.ToStop < stops.Count && stops[s.ToStop].Transfer is { } choice && EvaluateTransfer(s.Legs, choice, bodies) is { } transfer
                ? s with { Legs = ApplyTransfer(s.Legs, transfer) }
                : s).ToList();

    /// <summary>
    /// A leg can be a rendezvous when it arrives at a placed custom location (its position is known, so there is a craft
    /// to meet) and starts either on the surface of the body it orbits, where there is a launch site (KSC), or at another
    /// placed location around the same body. Null otherwise.
    /// </summary>
    /// <param name="isPlaced">Whether a custom location (by id) is placed.</param>
    public static RendezvousSpan? FindRendezvous(IReadOnlyList<RouteLeg> legs, DvMapNode from, DvMapNode to, Func<long, bool> isPlaced)
    {
        if (legs.Count == 0 || to.CustomLocationId is not { } target || !isPlaced(target) || from.BodyName != to.BodyName) return null;
        if (from.Kind == "Surface" && from.BodyName == LaunchSite.Ksc.BodyName)
        {
            var start = legs.TakeWhile(l => l.From.Kind == "Surface").Count();
            return start < legs.Count ? new RendezvousSpan(to.BodyName, target, null, start) : null;
        }
        return from.CustomLocationId is { } chaser && chaser != target && isPlaced(chaser)
            ? new RendezvousSpan(to.BodyName, target, chaser, 0)
            : null;
    }

    /// <summary>
    /// The leg's steps with the rendezvous in place of the map's orbital steps: the planned orbital Δv on the first of
    /// them, nothing on the rest. The plan's burns already include any plane change, and can't be aerobraked.
    /// </summary>
    public static IReadOnlyList<RouteLeg> ApplyRendezvous(IReadOnlyList<RouteLeg> legs, RendezvousSpan span, RendezvousLeg rendezvous) =>
        legs.Select((leg, i) => i < span.Start ? leg : leg with
        {
            DeltaV = i == span.Start ? Math.Round(rendezvous.OrbitalDv) : 0,
            PlaneChangeDv = 0,
            CanAerobrake = false,
            AerobrakeDv = 0,
            ToolNote = i == span.Start ? "Rendezvous burns as planned (plane match, phasing, transfer, match)" : "Part of the rendezvous",
        }).ToList();

    /// <summary>A constellation can be released at stops that are an orbit (not a surface, intercept or escape).</summary>
    public static bool CanDeploy(DvMapNode stop, IReadOnlyDictionary<string, CelestialBody> bodies) =>
        stop.Kind is "LowOrbit" or "Synchronous" or "Custom" && stop.AltitudeM is > 0 && bodies.ContainsKey(stop.BodyName);

    /// <summary>
    /// The carrier orbit for a deployment at <paramref name="stop"/> (§6). Entering it from the circular orbit and
    /// leaving it again are the same size of burn as each satellite's circularization (all at the shared apsis).
    /// Null if the stop can't host one or the orbit doesn't work (e.g. the carrier would leave the sphere of influence).
    /// </summary>
    /// <param name="continues">More stops follow, so the carrier returns to the circular orbit after the last release.</param>
    public static DeployResult? EvaluateDeploy(DvMapNode stop, DeployStep step, IReadOnlyDictionary<string, CelestialBody> bodies,
        bool continues)
    {
        if (!CanDeploy(stop, bodies)) return null;
        var body = bodies[stop.BodyName];
        var altitude = stop.AltitudeM!.Value;
        try
        {
            var orbit = ResonantOrbitCalculator.Calculate(new ResonantOrbitRequest(
                body, step.SatelliteCount, altitude, step.PreferDiveOrbit, new OcclusionSettings(step.UseOcclusion)));
            var burn = Math.Round(orbit.InjectionDeltaV, 1);
            return new DeployResult(body, altitude, orbit, burn, continues ? burn : 0);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool AreSiblings(string a, string b, IReadOnlyDictionary<string, CelestialBody> bodies) =>
        a != b && bodies.TryGetValue(a, out var x) && bodies.TryGetValue(b, out var y)
        && x.ParentId is not null && x.ParentId == y.ParentId;
}
