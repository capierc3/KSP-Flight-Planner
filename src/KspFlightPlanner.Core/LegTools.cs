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
    InterceptPoint? Intercept = null)
{
    /// <summary>
    /// The plan starts before the mission gets to the start of the leg (<paramref name="earliestUt"/>, the previous leg's
    /// arrival; null when unknown). Where both craft are then isn't where they'll be, so the plan needs making again.
    /// </summary>
    public bool StartsBefore(double? earliestUt) => earliestUt is { } t && StartUt < t - 1;
}

/// <summary>
/// Where a rendezvous fits in a leg's Δv-map steps: steps from <see cref="Start"/> on are the orbital part (the steps
/// before it, a launch or the trip over from another body, stay the map's). <see cref="ChaserLocationId"/> is the craft
/// it starts from; null for a launch from KSC or, with <see cref="FromParking"/>, a low orbit the mission arrives in
/// (its position there isn't known, so the plan starts anywhere on it).
/// </summary>
public sealed record RendezvousSpan(string BodyName, long TargetLocationId, long? ChaserLocationId, int Start, bool FromParking = false)
{
    public bool IsLaunch => ChaserLocationId is null && !FromParking;
}

/// <summary>
/// The way from a craft around <see cref="FromBody"/> to a rendezvous around <see cref="ToBody"/>: the Δv map's steps
/// from the first body's low orbit to the second's, where the rendezvous starts (at <see cref="ParkingAltitudeM"/>).
/// <see cref="TripTimeS"/> estimates how long the steps take; null when they cross between planets, where the wait for a
/// launch window matters more than the flight (pick one on the Transfer Planner).
/// </summary>
public sealed record RendezvousApproach(string FromBody, string ToBody, IReadOnlyList<RouteLeg> Steps, double FromAltitudeM,
    double ParkingAltitudeM, double? TripTimeS)
{
    /// <summary>The steps' map Δv (no plane changes, no aerobraking).</summary>
    public double DeltaV => Steps.Sum(s => s.DeltaV);

    /// <summary>The worst-case plane changes the map adds to the steps.</summary>
    public double PlaneChangeDv => Steps.Sum(s => s.PlaneChangeDv);
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
/// See docs/MATH.md §6.2.
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
    /// <param name="location">Looks up custom locations by id, so plane changes after an inclined launch are measured
    /// from it (<see cref="MeasurePlaneChanges"/>); null leaves them as the map has them.</param>
    public static IReadOnlyList<RouteSegment> Apply(IReadOnlyList<RouteSegment> segments, IReadOnlyList<MissionStop> stops,
        IReadOnlyDictionary<string, CelestialBody> bodies, Func<long, CustomLocation?>? location = null)
    {
        var launches = new List<(int Segment, LaunchPlan Plan)>();
        var result = segments.Select((s, index) =>
        {
            if (s.ToStop >= stops.Count) return s;
            var stop = stops[s.ToStop];
            if (stop.Transfer is { } choice && EvaluateTransfer(s.Legs, choice, bodies) is { } transfer)
                s = s with { Legs = ApplyTransfer(s.Legs, transfer) };
            if (stop.Landing is { } landing && EvaluateLanding(s.Legs, landing, bodies) is { } plan)
                s = s with { Legs = ApplyLanding(s.Legs, plan) };
            if (stop.Launch is { } launch && EvaluateLaunch(s.Legs, launch, bodies) is { } launchPlan)
            {
                s = s with { Legs = ApplyLaunch(s.Legs, launchPlan) };
                launches.Add((index, launchPlan));
            }
            return s;
        }).ToList();
        if (location is not null)
            foreach (var (index, plan) in launches)
                result = MeasurePlaneChanges(result, index, plan, location).ToList();
        return result;
    }

    /// <summary>
    /// The leg a launch tool works on: the first one, when it lifts off from the surface of a body with a launch site
    /// (<see cref="LaunchInclination.Site"/>) into that body's low orbit (or straight on to a higher one), and the rest of
    /// the leg stays in orbit around it. Its index, or -1.
    /// </summary>
    public static int FindLaunch(IReadOnlyList<RouteLeg> legs, IReadOnlyDictionary<string, CelestialBody> bodies)
    {
        if (legs.Count == 0 || legs[0].From.Kind != "Surface" || !bodies.TryGetValue(legs[0].From.BodyName, out var body)
            || LaunchInclination.Site(body) is null) return -1;
        var climb = legs[0].Parts?[0] ?? legs[0];
        return climb.To is { Kind: "LowOrbit", AltitudeM: > 0 } && climb.To.BodyName == body.Name
               && legs.All(l => l.To.BodyName == body.Name && l.To.Kind != "Surface")
            ? 0 : -1;
    }

    /// <summary>The launch into <paramref name="launch"/>'s inclination at the low orbit the leg climbs to (§2.7), or null when the leg isn't a launch.</summary>
    public static LaunchPlan? EvaluateLaunch(IReadOnlyList<RouteLeg> legs, LaunchLeg launch, IReadOnlyDictionary<string, CelestialBody> bodies)
    {
        if (FindLaunch(legs, bodies) is not (var i and >= 0)) return null;
        var climb = legs[i].Parts?[0] ?? legs[i];
        return LaunchInclination.Plan(bodies[climb.From.BodyName], climb.To.AltitudeM!.Value, Math.Clamp(launch.InclinationDeg, 0, 180));
    }

    /// <summary>
    /// Adds the launch's extra Δv over a due-east launch (§2.7) to the launch step. The map's value (or your edit of it)
    /// stays underneath: the extra is on top of it.
    /// </summary>
    public static IReadOnlyList<RouteLeg> ApplyLaunch(IReadOnlyList<RouteLeg> legs, LaunchPlan plan)
    {
        if (FindLaunch(legs, new Dictionary<string, CelestialBody> { [plan.Body.Name] = plan.Body }) is not (var index and >= 0)) return legs;
        var note = $"Launch to {plan.InclinationDeg:0.#}° (heading {plan.HeadingText}): " +
                   $"+{plan.LaunchDv:0} m/s over due east" +
                   (plan.IsReachable ? "" : $", +{plan.TrimDv:0} m/s plane change in orbit for the last {Math.Abs(plan.InclinationDeg - plan.FlownInclinationDeg):0.##}°");
        return legs.Select((leg, i) => i != index ? leg : leg with { DeltaV = leg.DeltaV + Math.Round(plan.ExtraDv), ToolNote = note }).ToList();
    }

    /// <summary>
    /// After a launch into an inclined orbit (segment <paramref name="launchSegment"/>), the plane change to each custom
    /// orbit around the same body is measured from the plane the craft is in, not from the equator: |i_location − i_now|
    /// at the location's apoapsis speed (§2.3, §2.7). Arriving there puts the craft in that orbit's plane for the next one.
    /// It stops where the mission lands or leaves the body. A rendezvous's own burns already match the plane.
    /// </summary>
    public static IReadOnlyList<RouteSegment> MeasurePlaneChanges(IReadOnlyList<RouteSegment> segments, int launchSegment, LaunchPlan plan,
        Func<long, CustomLocation?> location)
    {
        var body = plan.Body;
        var inclination = plan.InclinationDeg;
        var result = segments.ToList();
        for (var k = launchSegment; k < result.Count; k++)
        {
            var legs = result[k].Legs.ToList();
            for (var i = 0; i < legs.Count; i++)
            {
                var leg = legs[i];
                if (leg.To.BodyName != body.Name || leg.To.Kind == "Surface")
                {
                    result[k] = result[k] with { Legs = legs };
                    return result;
                }
                if (leg.To.CustomLocationId is not { } id || location(id) is not { } place || place.BodyName != body.Name) continue;
                var isLaunch = k == launchSegment && i == 0;
                if (!leg.IsFromTool || isLaunch)
                {
                    var r = body.EquatorialRadiusM;
                    var apoapsisSpeed = OrbitMath.VisViva(body.Mu, r + place.ApoapsisAltitudeM, r + (place.PeriapsisAltitudeM + place.ApoapsisAltitudeM) / 2);
                    var mapPlaneChange = leg.Parts?[^1].PlaneChangeDv ?? leg.PlaneChangeDv;
                    var planeChange = Math.Round(OrbitMath.PlaneChangeDv(apoapsisSpeed, place.InclinationDeg - inclination));
                    legs[i] = leg with { PlaneChangeDv = leg.PlaneChangeDv - mapPlaneChange + planeChange };
                }
                inclination = place.InclinationDeg;
            }
            result[k] = result[k] with { Legs = legs };
        }
        return result;
    }

    /// <summary>
    /// The leg a landing tool works on: the last one, when it comes down from an orbit of a body to that body's surface.
    /// Its index, or -1.
    /// </summary>
    public static int FindLanding(IReadOnlyList<RouteLeg> legs) =>
        legs.Count > 0 && legs[^1] is { To.Kind: "Surface", IsCombined: false } leg && leg.From.BodyName == leg.To.BodyName
                       && leg.From.AltitudeM is > 0
            ? legs.Count - 1 : -1;

    /// <summary>The landing at <paramref name="landing"/>'s spot from the orbit the leg starts in (§2.6), or null when the leg doesn't land.</summary>
    public static LandingPlan? EvaluateLanding(IReadOnlyList<RouteLeg> legs, LandingLeg landing, IReadOnlyDictionary<string, CelestialBody> bodies) =>
        FindLanding(legs) is var i and >= 0 && bodies.TryGetValue(legs[i].To.BodyName, out var body)
            ? Landing.Plan(body, legs[i].From.AltitudeM!.Value, landing)
            : null;

    /// <summary>
    /// Replaces the map's landing value with the planned deorbit and braking burns, and the worst-case plane change with
    /// the one this spot needs. On a body with air the map's value stays (aerobraking and parachutes do much of the
    /// work there); only the note changes.
    /// </summary>
    public static IReadOnlyList<RouteLeg> ApplyLanding(IReadOnlyList<RouteLeg> legs, LandingPlan plan)
    {
        var index = FindLanding(legs);
        if (index < 0) return legs;
        var name = plan.Target.Name;
        return legs.Select((leg, i) => i != index ? leg
            : plan.IsRoughEstimate ? leg with { ToolNote = $"Landing at {name}: map value kept ({leg.To.BodyName} has air, so the spot is a rough estimate)" }
            : leg with
            {
                DeltaV = Math.Round(plan.TotalDv),
                PlaneChangeDv = Math.Round(plan.PlaneChangeDv),
                CanAerobrake = false,
                AerobrakeDv = 0,
                ToolNote = $"Landing at {name}: deorbit {plan.DeorbitDv:0} m/s + braking {plan.BrakingDv:0} m/s",
            }).ToList();
    }

    /// <summary>
    /// A leg can be a rendezvous when it arrives at a placed custom location (its position is known, so there is a craft
    /// to meet). It starts from another placed location around the same body, with a launch from the surface where
    /// there is a launch site (KSC), or otherwise from the low orbit of the target's body the map's steps arrive in
    /// (from that body's other orbits, its surface, or another body altogether). Null otherwise.
    /// </summary>
    /// <param name="isPlaced">Whether a custom location (by id) is placed.</param>
    public static RendezvousSpan? FindRendezvous(IReadOnlyList<RouteLeg> legs, DvMapNode from, DvMapNode to, Func<long, bool> isPlaced)
    {
        if (legs.Count == 0 || to.CustomLocationId is not { } target || !isPlaced(target)) return null;
        if (from.BodyName == to.BodyName && from.CustomLocationId is { } chaser && chaser != target && isPlaced(chaser))
            return new RendezvousSpan(to.BodyName, target, chaser, 0);
        if (from.BodyName == to.BodyName && from.Kind == "Surface" && from.BodyName == LaunchSite.Ksc.BodyName)
        {
            var start = legs.TakeWhile(l => l.From.Kind == "Surface").Count();
            return start < legs.Count ? new RendezvousSpan(to.BodyName, target, null, start) : null;
        }
        // The map reaches a craft around a planet or moon from that body's low orbit: the last step leaves it.
        var last = legs[^1].From;
        return last.Kind == "LowOrbit" && !last.IsCustom && last.BodyName == to.BodyName && last.AltitudeM is > 0
            ? new RendezvousSpan(to.BodyName, target, null, legs.Count - 1, FromParking: true)
            : null;
    }

    /// <summary>
    /// How a craft around <paramref name="fromBody"/> gets to a rendezvous around <paramref name="toBody"/>, by the same
    /// rule as a mission leg from another body (<see cref="FindRendezvous"/>): the map's steps from the first body's low
    /// orbit to the second's, where the rendezvous starts. Null for the same body, or where the map has no low orbit.
    /// </summary>
    public static RendezvousApproach? FindApproach(DeltaVMap map, string fromBody, string toBody,
        IReadOnlyDictionary<string, CelestialBody> bodies)
    {
        if (fromBody == toBody || !bodies.ContainsKey(fromBody) || !bodies.ContainsKey(toBody)) return null;
        if (LowOrbit(map, fromBody) is not { AltitudeM: { } fromAltitude } from || LowOrbit(map, toBody) is not { AltitudeM: { } parking } to)
            return null;
        var steps = map.Route(from, to);
        return new RendezvousApproach(fromBody, toBody, steps, fromAltitude, parking, TripTimeS(steps, bodies));
    }

    private static DvMapNode? LowOrbit(DeltaVMap map, string bodyName) =>
        map.Nodes.FirstOrDefault(n => n.BodyName == bodyName && n.Kind == "LowOrbit" && !n.IsCustom && n.AltitudeM is > 0);

    /// <summary>
    /// About how long the steps take: a Hohmann half-orbit for each step between a moon and its planet, from the moon's
    /// orbit to the planet's low orbit (or back). Time spent inside a sphere of influence is left out. Null when a step
    /// crosses between planets (it waits for a window) or a body's orbit isn't known. See docs/MATH.md §4.6.
    /// </summary>
    public static double? TripTimeS(IReadOnlyList<RouteLeg> steps, IReadOnlyDictionary<string, CelestialBody> bodies)
    {
        var total = 0.0;
        foreach (var step in steps)
        {
            if (step.From.BodyName == step.To.BodyName) continue;
            if (!bodies.TryGetValue(step.From.BodyName, out var a) || !bodies.TryGetValue(step.To.BodyName, out var b)) return null;
            // The moon and the planet it orbits, whichever way the step goes; the planet's end of the step is its orbit.
            var (moon, planet, planetNode) = a.ParentId == b.Id ? (a, b, step.To) : b.ParentId == a.Id ? (b, a, step.From) : (null, null, null);
            if (moon?.Orbit is not { } orbit || planet!.ParentId is null) return null; // between planets, or around the star
            var low = planet.EquatorialRadiusM + (planetNode!.AltitudeM ?? planet.DefaultParkingAltitudeM);
            total += OrbitMath.Hohmann(planet.Mu, orbit.SemiMajorAxisM, low).TransferTimeS;
        }
        return total;
    }

    /// <summary>
    /// The leg's steps with the rendezvous in place of the map's orbital steps: the planned orbital Δv on the first of
    /// them, nothing on the rest. The plan's burns already include any plane change, and can't be aerobraked. A plan that
    /// starts before <paramref name="earliestUt"/> (when the mission gets there) is still used, with a note to plan it again.
    /// </summary>
    public static IReadOnlyList<RouteLeg> ApplyRendezvous(IReadOnlyList<RouteLeg> legs, RendezvousSpan span, RendezvousLeg rendezvous,
        double? earliestUt = null) =>
        legs.Select((leg, i) => i < span.Start ? leg : leg with
        {
            DeltaV = i == span.Start ? Math.Round(rendezvous.OrbitalDv) : 0,
            PlaneChangeDv = 0,
            CanAerobrake = false,
            AerobrakeDv = 0,
            ToolNote = i != span.Start ? "Part of the rendezvous"
                : rendezvous.StartsBefore(earliestUt) ? "Rendezvous planned to start before the mission gets here: open the leg to plan it again"
                : "Rendezvous burns as planned (plane match, phasing, transfer, match)",
        }).ToList();

    /// <summary>A constellation can be released at stops that are an orbit (not a surface, intercept or escape).</summary>
    public static bool CanDeploy(DvMapNode stop, IReadOnlyDictionary<string, CelestialBody> bodies) =>
        stop.Kind is "LowOrbit" or "Synchronous" or "Custom" && stop.AltitudeM is > 0 && bodies.ContainsKey(stop.BodyName);

    /// <summary>
    /// The carrier orbit for a deployment at <paramref name="stop"/> (§5.1). Entering it from the circular orbit and
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
