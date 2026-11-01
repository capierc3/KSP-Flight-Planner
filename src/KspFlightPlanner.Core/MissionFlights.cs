using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core;

public enum MissionStepKind
{
    /// <summary>Lift off and fly up to orbit by hand: no burns, and the steps after are planned from the orbit it aims for.</summary>
    Launch,
    /// <summary>Change the orbit around the same body: tilt it, then raise or lower each side (§2.8).</summary>
    ChangeOrbit,
    /// <summary>Go to another body: leave, correct on the way, capture (<see cref="TripPlanner"/>, §3.8).</summary>
    Trip,
    /// <summary>Meet a craft in orbit (§4.1).</summary>
    Rendezvous,
    /// <summary>Release a constellation from a resonant carrier orbit (§5.2).</summary>
    Deploy,
    /// <summary>Come down: at a chosen spot (§2.9), with a deorbit into the air, or by hand.</summary>
    Land,
    /// <summary>A stop the planner doesn't fly (a flyby, an escape orbit): fly it by hand, then go on to the next step.</summary>
    ByHand,
}

/// <summary>
/// One step of flying a mission, made from its stops: what it does, the body it ends at, and what it needs to be planned.
/// It holds no dates: each step is planned from wherever the craft really is (or where the step before leaves it).
/// </summary>
/// <param name="StopIndex">The mission stop the step gets to.</param>
public sealed record MissionStep(MissionStepKind Kind, string Title, string BodyName, int StopIndex)
{
    /// <summary>Launch, change orbit: the periapsis height to end in. Trip: the arrival height. Deploy: the constellation's height.</summary>
    public double AltitudeM { get; init; }

    /// <summary>Change orbit: the far side, for an orbit that isn't round; null for a round one.</summary>
    public double? ApoapsisAltitudeM { get; init; }

    /// <summary>Launch, change orbit: the inclination to end in; null for any (a launch goes due east).</summary>
    public double? InclinationDeg { get; init; }

    /// <summary>Trip: the launch window chosen on the mission for its hop across.</summary>
    public TransferLeg? Window { get; init; }

    /// <summary>Rendezvous: the craft met, with its orbit when the mission was flown.</summary>
    public FlightTarget? Target { get; init; }

    // Deploy.
    public int SatelliteCount { get; init; }
    public bool PreferDiveOrbit { get; init; }
    public bool UseOcclusion { get; init; }

    /// <summary>Land: the spot; null to come down anywhere.</summary>
    public LandingLeg? Landing { get; init; }
}

/// <summary>Where a mission starts: a body, and the orbit's height there (null on the surface).</summary>
public sealed record MissionStart(string BodyName, double? AltitudeM);

/// <summary>
/// One mission step planned from <see cref="StartOrbit"/> around <see cref="StartBodyName"/> at <see cref="StartUt"/>
/// (the craft's real orbit for the step being flown; where the step before leaves it for the rest).
/// </summary>
public sealed record MissionStepPlan(int Step, string StartBodyName, OrbitalElements? StartOrbit, double StartUt, IReadOnlyList<FlightBurn> Burns)
{
    public string EndBodyName { get; init; } = "";

    /// <summary>The orbit (or path) the step leaves the craft on; null once it's landed.</summary>
    public OrbitalElements? EndOrbit { get; init; }

    public bool EndsLanded { get; init; }

    /// <summary>When the step is over: its last burn, the capture, the meeting or the touchdown.</summary>
    public double EndUt { get; init; }

    /// <summary>Something to know about the step.</summary>
    public string? Note { get; init; }

    /// <summary>Planned from an orbit taken as flown exactly (a launch still to fly) rather than one the craft is in.</summary>
    public bool Assumed { get; init; }

    /// <summary>Not planned: why (it couldn't be, or a step before it is flown by hand or couldn't be).</summary>
    public string? Problem { get; init; }

    public bool IsPlanned => Problem is null;

    /// <summary>The craft's own Δv (not released satellites').</summary>
    public double DeltaV => Burns.Where(b => !b.ByReleasedCraft).Sum(b => b.DeltaV);
}

/// <summary>Where the craft is: the body, its orbit or path there (null on the surface), and the time.</summary>
public sealed record MissionSituation(string BodyName, OrbitalElements? Orbit, bool Landed, double Now);

/// <summary>What planning needs beyond the steps.</summary>
/// <param name="LeadS">The soonest the step being flown has a burn after now.</param>
/// <param name="InitialRotationDeg">A body's rotation angle at UT 0 (for a landing at a spot), when known.</param>
/// <param name="Bake">A body's surface bake, when there is one.</param>
/// <param name="TargetOrbit">A rendezvous target's orbit now (live while connected); null keeps the step's.</param>
public sealed record MissionPlanContext(
    IReadOnlyDictionary<string, CelestialBody> Bodies,
    double LeadS,
    Func<string, double?>? InitialRotationDeg = null,
    Func<string, SurfaceBake?>? Bake = null,
    Func<FlightTarget, OrbitalElements?>? TargetOrbit = null);

/// <summary>
/// Flying a whole mission: its stops become steps (launch, change orbit, go to a body, meet a craft, release satellites,
/// land), flown in order. The step being flown is planned from the craft's real orbit, and every step after it from
/// where the one before leaves the craft, so a launch to 75 km instead of 80 changes the trip after it, and that the
/// landing after that. Each step is planned again from the real orbit once the craft gets to it. docs/MATH.md §8.12.
/// </summary>
public static class MissionFlights
{
    /// <summary>How long a launch takes from lift-off to orbit, for the steps planned after one still to fly.</summary>
    public const double AscentS = 600;

    /// <summary>An orbit counts as the one asked for when each side is within this.</summary>
    public const double OnOrbitM = 2_000;

    /// <summary>An inclination closer than this to the one asked for isn't changed.</summary>
    public const double OnInclinationDeg = 0.1;

    /// <summary>Burns smaller than this aren't worth a node.</summary>
    public const double SmallBurnDv = 0.5;

    // ---------------- Stops to steps ----------------

    /// <summary>
    /// The steps that fly <paramref name="mission"/>'s stops in order, and where it starts. Leaving a surface is a launch;
    /// a stop around another body is a trip there; a placed craft is a rendezvous; any other orbit around the same body
    /// is an orbit change; a surface is a landing (at the stop's spot if it has one; on a body with air and no spot, the
    /// trip there aims straight into the atmosphere). A constellation released at a stop is a step after it. Throws
    /// <see cref="ArgumentException"/> when the mission can't be flown (fewer than two stops, a stop not on the map).
    /// </summary>
    /// <param name="map">The Δv map with the mission's places on it.</param>
    /// <param name="location">A custom location by id (the mission's places and the Locations pane's).</param>
    public static (MissionStart Start, IReadOnlyList<MissionStep> Steps) Steps(MissionDefinition mission, DeltaVMap map,
        IReadOnlyDictionary<string, CelestialBody> bodies, Func<long, CustomLocation?> location)
    {
        if (mission.Stops.Count < 2) throw new ArgumentException("Add a second stop to the mission to fly it.");
        var nodes = mission.Stops.Select(s => map.TryGet(s.NodeKey, out var n) ? n : throw new ArgumentException($"A stop ({s.NodeKey}) isn't on the Δv map.")).ToList();
        foreach (var n in nodes)
            if (!bodies.ContainsKey(n.BodyName)) throw new ArgumentException($"{n.BodyName} isn't in the planner's bodies.");

        double LowOrbit(string body) =>
            map.Nodes.FirstOrDefault(n => n.BodyName == body && n.Kind == "LowOrbit" && !n.IsCustom)?.AltitudeM ?? bodies[body].DefaultParkingAltitudeM;
        CustomLocation? Place(DvMapNode n) => n.CustomLocationId is { } id ? location(id) : null;
        double Periapsis(DvMapNode n) => Place(n)?.PeriapsisAltitudeM ?? n.AltitudeM ?? LowOrbit(n.BodyName);

        var first = nodes[0];
        var start = new MissionStart(first.BodyName, first.Kind == "Surface" ? null : Periapsis(first));
        var here = first.BodyName;
        var landed = first.Kind == "Surface";
        // The orbit the craft is in after each stop, as far as the steps know it (null: not known).
        double? altitude = landed ? null : Periapsis(first);
        double? inclination = Place(first)?.InclinationDeg ?? (landed ? (double?)null : 0);
        var steps = new List<MissionStep>();

        for (var i = 1; i < nodes.Count; i++)
        {
            var stop = mission.Stops[i];
            var to = nodes[i];
            var there = to.BodyName;
            var body = bodies[there];
            var place = Place(to);

            if (landed)
            {
                if (to.Kind == "Surface" && there == here)
                {
                    steps.Add(new MissionStep(MissionStepKind.ByHand, $"Fly to {to.DisplayName}", there, i));
                    continue;
                }
                var height = there == here && to.Kind == "LowOrbit" && to.AltitudeM is { } low ? low : LowOrbit(here);
                inclination = stop.Launch?.InclinationDeg ?? 0;
                altitude = height;
                steps.Add(new MissionStep(MissionStepKind.Launch, $"Launch to {Km(height)} around {here}", here, i)
                {
                    AltitudeM = height,
                    InclinationDeg = stop.Launch?.InclinationDeg,
                });
                landed = false;
            }

            switch (to.Kind)
            {
                case "Surface":
                    if (there != here)
                    {
                        // Into the air with nowhere in particular to land: no capture, the atmosphere does the braking.
                        var direct = body.AtmosphereHeightM > 0 && stop.Landing is null;
                        var height = direct ? TransferFlights.AerobrakeFraction * body.AtmosphereHeightM : LowOrbit(there);
                        steps.Add(new MissionStep(MissionStepKind.Trip, direct ? $"Go to {there}, straight into the atmosphere" : $"Go to {there} ({Km(height)} orbit)", there, i)
                        {
                            AltitudeM = height,
                            Window = stop.Transfer,
                        });
                    }
                    steps.Add(new MissionStep(MissionStepKind.Land, stop.Landing is { } spot ? $"Land at {spot.Target.Name}" : $"Land on {there}", there, i)
                    {
                        Landing = stop.Landing,
                    });
                    landed = true;
                    altitude = inclination = null;
                    break;

                case "LowOrbit" or "Synchronous" or "Custom":
                {
                    var pe = Periapsis(to);
                    var ap = place?.ApoapsisAltitudeM ?? pe;
                    var target = place is { IsPlaced: true } ? Target(place, body, bodies) : null;
                    if (there != here)
                    {
                        var height = target is null ? pe : LowOrbit(there);
                        steps.Add(new MissionStep(MissionStepKind.Trip, $"Go to {there} ({Km(height)} orbit)", there, i)
                        {
                            AltitudeM = height,
                            Window = stop.Transfer,
                        });
                        altitude = height;
                        inclination = null;
                    }
                    if (target is not null)
                    {
                        steps.Add(new MissionStep(MissionStepKind.Rendezvous, $"Meet {place!.Name}", there, i) { Target = target });
                        inclination = place.InclinationDeg;
                    }
                    else
                    {
                        // An orbit change when the height, shape or (for a custom orbit) tilt isn't what the craft is in.
                        var tilted = place is not null && (inclination is not { } now || Math.Abs(place.InclinationDeg - now) > OnInclinationDeg);
                        if (altitude is not { } h || Math.Abs(h - pe) > OnOrbitM || Math.Abs(ap - pe) > OnOrbitM || tilted)
                            steps.Add(new MissionStep(MissionStepKind.ChangeOrbit, $"Change orbit to {OrbitName(to, pe, ap)}", there, i)
                            {
                                AltitudeM = pe,
                                ApoapsisAltitudeM = Math.Abs(ap - pe) > OnOrbitM ? ap : null,
                                InclinationDeg = place?.InclinationDeg,
                            });
                        if (place is not null) inclination = place.InclinationDeg;
                    }
                    altitude = pe;
                    break;
                }

                default:
                    steps.Add(new MissionStep(MissionStepKind.ByHand, $"Fly to {to.DisplayName}", there, i));
                    altitude = inclination = null;
                    break;
            }
            here = there;

            if (stop.Deploy is { } deploy && LegTools.CanDeploy(to, bodies))
                steps.Add(new MissionStep(MissionStepKind.Deploy, $"Release {deploy.SatelliteCount} satellites at {Km(to.AltitudeM!.Value)}", there, i)
                {
                    AltitudeM = to.AltitudeM!.Value,
                    SatelliteCount = deploy.SatelliteCount,
                    PreferDiveOrbit = deploy.PreferDiveOrbit,
                    UseOcclusion = deploy.UseOcclusion,
                });
        }
        return (start, steps);
    }

    /// <summary>A placed craft to meet, with its orbit; null when its orbit can't be worked out.</summary>
    private static FlightTarget? Target(CustomLocation place, CelestialBody body, IReadOnlyDictionary<string, CelestialBody> bodies)
    {
        var home = bodies.Values.FirstOrDefault(b => b.IsDefault);
        var star = home is null ? null : bodies.Values.FirstOrDefault(b => b.Id == home.ParentId);
        try { return new FlightTarget(place.Name, place.KspVesselId, CustomLocationOrbits.ToElements(place, body, home, star), 0); }
        catch (ArgumentException) { return null; }
    }

    private static string OrbitName(DvMapNode node, double pe, double ap) =>
        node.CustomName is { } name ? $"{name} ({OrbitSize(pe, ap)})" : $"{OrbitSize(pe, ap)} around {node.BodyName}";

    private static string OrbitSize(double pe, double ap) => Math.Abs(ap - pe) > OnOrbitM ? $"{Km(pe)} × {Km(ap)}" : Km(pe);

    // ---------------- Planning ----------------

    /// <summary>
    /// Plans the steps from <paramref name="from"/> on: the first from <paramref name="here"/>, each later one from where
    /// the one before leaves the craft (no burn sooner than <see cref="Rendezvous.BurnGapS"/> after the step before ends).
    /// A step that can't be planned, or is flown by hand, says why, and the steps after it wait for it.
    /// </summary>
    public static IReadOnlyList<MissionStepPlan> Plan(IReadOnlyList<MissionStep> steps, int from, MissionSituation here, MissionPlanContext context)
    {
        var plans = new List<MissionStepPlan>();
        var (body, orbit, landed, ut) = (here.BodyName, here.Orbit, here.Landed, here.Now);
        var earliest = here.Now + context.LeadS;
        var assumed = false;
        int? waitingFor = null;
        for (var i = from; i < steps.Count; i++)
        {
            if (waitingFor is { } w)
            {
                plans.Add(new MissionStepPlan(i, body, null, ut, []) { EndBodyName = steps[i].BodyName, Problem = $"Planned once step {w + 1} is flown." });
                continue;
            }
            MissionStepPlan plan;
            try
            {
                plan = PlanStep(steps[i], i, body, orbit, landed, ut, earliest, context);
            }
            catch (ArgumentException ex)
            {
                plan = new MissionStepPlan(i, body, orbit, ut, []) { EndBodyName = steps[i].BodyName, Problem = ex.Message };
            }
            plans.Add(plan = plan with { Assumed = plan.Assumed || assumed });
            if (!plan.IsPlanned || steps[i].Kind == MissionStepKind.ByHand)
            {
                waitingFor = i;
                continue;
            }
            assumed = plan.Assumed;
            (body, orbit, landed, ut) = (plan.EndBodyName, plan.EndOrbit, plan.EndsLanded, plan.EndUt);
            earliest = ut + Rendezvous.BurnGapS;
        }
        return plans;
    }

    /// <summary>One step from the craft around <paramref name="body"/> on <paramref name="orbit"/> (null: landed) at <paramref name="ut"/>.</summary>
    private static MissionStepPlan PlanStep(MissionStep step, int index, string body, OrbitalElements? orbit, bool landed, double ut, double earliest,
        MissionPlanContext context)
    {
        var bodies = context.Bodies;
        CelestialBody Named(string name) => bodies.GetValueOrDefault(name) ?? throw new ArgumentException($"{name} isn't in the planner's bodies.");
        var start = new MissionStepPlan(index, body, landed ? null : orbit, ut, []) { EndBodyName = step.BodyName, EndUt = earliest };

        if (step.Kind == MissionStepKind.Launch)
        {
            if (!landed && orbit is not null && body == step.BodyName && orbit.Eccentricity < 1)
                return start with { EndOrbit = orbit, Note = $"Already in orbit around {body}." };
            var launchBody = Named(step.BodyName);
            var inclination = step.InclinationDeg ?? 0;
            var heading = LaunchInclination.Plan(launchBody, step.AltitudeM, Math.Clamp(inclination, 0, 180)) is { } launch ? $", heading {launch.HeadingText}" : "";
            var insertion = earliest + AscentS;
            return start with
            {
                EndOrbit = new OrbitalElements(launchBody.EquatorialRadiusM + step.AltitudeM, 0, inclination, 0, 0, 0, insertion),
                EndUt = insertion,
                Assumed = true,
                Note = $"Fly the launch by hand to a round {Km(step.AltitudeM)} orbit{heading}. The steps after are planned from that orbit " +
                       "until you're up, then again from the one you really reach.",
            };
        }
        if (landed || orbit is null)
            throw new ArgumentException($"The craft is on the ground on {body}: it needs to launch first.");
        if (step.Kind == MissionStepKind.ByHand)
            return start with { EndOrbit = orbit, Note = "The planner doesn't fly this one: fly it by hand, then go on to the next step." };

        var here = Named(body);
        var mu = here.Mu;
        if (step.Kind != MissionStepKind.Trip && body != step.BodyName)
            throw new ArgumentException($"This step starts in orbit around {step.BodyName}, but the craft is around {body}.");
        if (step.Kind is not (MissionStepKind.Trip or MissionStepKind.Land) && orbit.Eccentricity >= 1)
            throw new ArgumentException($"The craft is on a path that escapes {body}: capture into orbit first (Circularize at periapsis in the From here pane).");

        switch (step.Kind)
        {
            case MissionStepKind.ChangeOrbit:
            {
                var burns = ChangeOrbit(here, orbit, earliest, step);
                var end = burns.Aggregate(orbit, (o, b) => Rendezvous.OrbitAfter(b.Ut, b.Burn, o, mu));
                return start with
                {
                    Burns = burns,
                    EndOrbit = end,
                    EndUt = burns.Count > 0 ? burns[^1].Ut : earliest,
                    Note = burns.Count == 0 ? "Already in that orbit." : null,
                };
            }

            case MissionStepKind.Trip:
            {
                var to = Named(step.BodyName);
                if (body == step.BodyName)
                {
                    // Arrived (or launched where the trip ends): capture if still on the way in.
                    if (orbit.Eccentricity < 1) return start with { EndOrbit = orbit, Note = $"Already around {body}." };
                    var capture = QuickBurns.Circularize(here, orbit, earliest, 0, atApoapsis: false);
                    return start with
                    {
                        Burns = [capture with { Title = $"Capture at {body}" }],
                        EndOrbit = Rendezvous.OrbitAfter(capture.Ut, capture.Burn, orbit, mu),
                        EndUt = capture.Ut,
                    };
                }
                (double, double)? window = null;
                string? late = null;
                if (step.Window is { } w)
                {
                    if (earliest <= w.DepartureUt + TransferFlights.LateFraction * w.FlightTimeS) window = (w.DepartureUt, w.FlightTimeS);
                    else late = $"The launch window chosen for this leg ({GameCalendar.Kerbin.FormatDate(w.DepartureUt)}) has gone, so this is the next good one.";
                }
                var option = TripPlanner.Plan(here, to, orbit, earliest, step.AltitudeM, bodies.Values.ToList(), window);
                var last = option.Legs[^1].Plan;
                var notes = option.Warnings.ToList();
                if (late is not null) notes.Insert(0, late);
                return start with
                {
                    Burns = option.Burns.Select(b => b with { Note = LaterNote(b.Note) }).ToList(),
                    EndOrbit = Hops.Captured(last, to) ?? last.Arrival.Path,
                    EndUt = last.Capture?.Ut ?? last.Arrival.PeriapsisUt,
                    Note = notes.Count > 0 ? string.Join(" ", notes) : null,
                };
            }

            case MissionStepKind.Rendezvous:
            {
                if (step.Target is not { } target) throw new ArgumentException("This step has no craft to meet.");
                var goal = context.TargetOrbit?.Invoke(target) ?? target.Orbit;
                var plan = Rendezvous.Plan(new RendezvousRequest(here, [new RendezvousTarget(target.Name, goal)], earliest, 0, new RendezvousTarget("craft", orbit)));
                var burns = RendezvousFlights.Burns(plan);
                return start with { Burns = burns, EndOrbit = goal, EndUt = plan.Legs[0].ArrivalUt };
            }

            case MissionStepKind.Deploy:
            {
                var carrier = ResonantOrbitCalculator.Calculate(new ResonantOrbitRequest(here, step.SatelliteCount, step.AltitudeM, step.PreferDiveOrbit,
                    new OcclusionSettings(step.UseOcclusion)));
                var plan = ResonantDeployment.Plan(here, carrier, orbit, step.SatelliteCount, earliest);
                var end = plan.Burns.Where(b => !b.ByReleasedCraft).Aggregate(orbit, (o, b) => Rendezvous.OrbitAfter(b.Ut, b.Burn, o, mu));
                return start with
                {
                    Burns = plan.Burns,
                    EndOrbit = end,
                    EndUt = plan.Burns.Count > 0 ? plan.Burns[^1].Ut : earliest,
                    Note = plan.Warnings.Count > 0 ? string.Join(" ", plan.Warnings) : null,
                };
            }

            case MissionStepKind.Land:
                return Land(start with { EndsLanded = true }, step, here, orbit, earliest, context);

            default:
                throw new ArgumentException($"Can't plan a {step.Kind} step.");
        }
    }

    /// <summary>
    /// An orbit change around one body (§2.8): two apsis burns, from the periapsis raising (or lowering) the far side to
    /// the new apoapsis, then from there setting the new periapsis (going lower all round, the periapsis comes down
    /// first); and a tilt at the equator if the inclination is off, after going up or before coming down.
    /// </summary>
    private static List<FlightBurn> ChangeOrbit(CelestialBody body, OrbitalElements orbit, double earliest, MissionStep step)
    {
        var mu = body.Mu;
        var burns = new List<FlightBurn>();
        var o = orbit;
        var t = earliest;
        void Fly(FlightBurn burn)
        {
            if (burn.DeltaV < SmallBurnDv) return;
            burns.Add(burn);
            o = Rendezvous.OrbitAfter(burn.Ut, burn.Burn, o, mu);
            t = burn.Ut + Rendezvous.BurnGapS;
        }

        void Tilt()
        {
            if (step.InclinationDeg is { } inclination && Math.Abs(o.InclinationDeg - inclination) > OnInclinationDeg)
                Fly(LandingFlights.Tilt(body, o, t, inclination) with { Note = null });
        }

        var r = body.EquatorialRadiusM;
        double Pe() => o.SemiMajorAxisM * (1 - o.Eccentricity) - r;
        double Ap() => o.SemiMajorAxisM * (1 + o.Eccentricity) - r;
        var pe = step.AltitudeM;
        var ap = step.ApoapsisAltitudeM ?? pe;
        // The tilt is cheapest where the craft is slowest: after going up, before coming down.
        var up = pe + ap > Pe() + Ap();
        if (!up) Tilt();
        if (Math.Abs(Pe() - pe) >= OnOrbitM || Math.Abs(Ap() - ap) >= OnOrbitM)
        {
            if (ap >= Pe())
            {
                if (Math.Abs(Ap() - ap) >= OnOrbitM / 2) Fly(QuickBurns.SetApsis(body, o, t, 0, ap, apoapsis: true));
                if (Math.Abs(Pe() - pe) >= OnOrbitM / 2) Fly(QuickBurns.SetApsis(body, o, t, 0, pe, apoapsis: false));
            }
            else
            {
                Fly(QuickBurns.SetApsis(body, o, t, 0, pe, apoapsis: false));
                if (Math.Abs(Ap() - ap) >= OnOrbitM / 2) Fly(QuickBurns.SetApsis(body, o, t, 0, ap, apoapsis: true));
            }
        }
        if (up) Tilt();
        return burns;
    }

    /// <summary>
    /// Coming down on <paramref name="body"/>: at the step's spot (§2.9); with no spot on a body with air, a deorbit
    /// into it (§2.5); with no spot and no air, by hand. Already coming down, there's nothing to burn.
    /// </summary>
    private static MissionStepPlan Land(MissionStepPlan none, MissionStep step, CelestialBody body, OrbitalElements orbit, double earliest,
        MissionPlanContext context)
    {
        var air = body.AtmosphereHeightM;
        if (Kepler.PeriapsisRadius(orbit) < body.EquatorialRadiusM + air)
        {
            var down = Kepler.NextPeriapsisUt(orbit, body.Mu, earliest) ?? earliest;
            return none with
            {
                EndUt = down,
                Note = air > 0
                    ? $"Already coming down into {body.Name}'s atmosphere: the air does the braking. Open the parachutes when it's safe."
                    : "Already coming down: burn off the speed before the ground.",
            };
        }
        if (step.Landing is { } leg)
        {
            var theta0 = context.InitialRotationDeg?.Invoke(body.Name)
                         ?? throw new ArgumentException($"{body.Name}'s rotation isn't known yet: connect to KSP (or bake its surface data) to plan the landing.");
            var option = LandingFlights.Options(body, leg, orbit, earliest, 0, theta0, context.Bake?.Invoke(body.Name))[0];
            return none with
            {
                Burns = option.Burns,
                EndUt = option.LandingUt,
                Note = option.Warnings.Count > 0 ? string.Join(" ", option.Warnings) : null,
            };
        }
        if (air > 0)
        {
            var periapsis = TransferFlights.AerobrakeFraction * air;
            var deorbit = Rendezvous.Deorbit(body, orbit, earliest, periapsis);
            return none with
            {
                Burns = [new FlightBurn($"Deorbit (periapsis {Km(periapsis)})", deorbit.Ut, deorbit.Burn)],
                EndUt = deorbit.Ut + Kepler.OrbitalPeriod(orbit, body.Mu) / 2,
                Note = "The air does the braking from there: open the parachutes when it's safe.",
            };
        }
        return none with
        {
            Note = $"No landing spot on this stop: land by hand, or pick a spot for it in Plan mode to have the burns planned.",
        };
    }

    // ---------------- Progress ----------------

    /// <summary>
    /// The step being flown: from <paramref name="index"/> on, past every step the craft has clearly finished. A launch
    /// is done once the craft is in a stable orbit around its body; a trip once it's around the destination (in orbit,
    /// or on its way down when a landing follows); a landing once it's on the ground there; an orbit change once the
    /// orbit is the one asked for; the rest once their last burn has been flown (<paramref name="settleS"/> after its
    /// node). A step flown by hand waits for the player.
    /// </summary>
    /// <param name="plans">The steps as last planned (for their burn times).</param>
    public static int Current(IReadOnlyList<MissionStep> steps, int index, MissionSituation here, IReadOnlyList<MissionStepPlan> plans,
        IReadOnlyDictionary<string, CelestialBody> bodies, double settleS)
    {
        while (index < steps.Count && Done(index)) index++;
        return index;

        bool Done(int i)
        {
            var step = steps[i];
            var around = !here.Landed && here.BodyName == step.BodyName && here.Orbit is not null;
            var closed = around && here.Orbit!.Eccentricity < 1;
            var burns = plans.FirstOrDefault(p => p.Step == i && p.IsPlanned)?.Burns ?? [];
            var own = burns.Where(b => !b.ByReleasedCraft).ToList();
            var flown = own.Count > 0 && own[^1].Ut + settleS < here.Now;
            return step.Kind switch
            {
                MissionStepKind.Launch => closed && bodies.GetValueOrDefault(step.BodyName) is { } b && QuickBurns.ClearOfAtmosphere(b, here.Orbit!),
                MissionStepKind.Trip => closed || here.Landed && here.BodyName == step.BodyName
                                        || around && i + 1 < steps.Count && steps[i + 1].Kind == MissionStepKind.Land,
                MissionStepKind.Land => here.Landed && here.BodyName == step.BodyName,
                MissionStepKind.ChangeOrbit => closed && (flown || OnOrbit(here.Orbit!, step, bodies)),
                MissionStepKind.Rendezvous => closed && flown,
                MissionStepKind.Deploy => closed && burns.Count > 0 && burns[^1].Ut + settleS < here.Now,
                _ => false,
            };
        }
    }

    private static bool OnOrbit(OrbitalElements orbit, MissionStep step, IReadOnlyDictionary<string, CelestialBody> bodies)
    {
        if (bodies.GetValueOrDefault(step.BodyName) is not { } body) return false;
        var r = body.EquatorialRadiusM;
        return Math.Abs(orbit.SemiMajorAxisM * (1 - orbit.Eccentricity) - r - step.AltitudeM) < OnOrbitM
               && Math.Abs(orbit.SemiMajorAxisM * (1 + orbit.Eccentricity) - r - (step.ApoapsisAltitudeM ?? step.AltitudeM)) < OnOrbitM
               && (step.InclinationDeg is not { } i || Math.Abs(orbit.InclinationDeg - i) <= OnInclinationDeg);
    }

    /// <summary>
    /// <paramref name="flight"/> (a <see cref="FlightKind.Mission"/>) with its steps from <see cref="Flight.StepIndex"/>
    /// planned from <paramref name="here"/>: the step being flown's burns become the flight's.
    /// </summary>
    public static Flight Replan(Flight flight, MissionSituation here, MissionPlanContext context)
    {
        var plans = Plan(flight.Steps, flight.StepIndex, here, context);
        return WithPlans(flight, plans, here.Now);
    }

    /// <summary>
    /// <paramref name="flight"/> planned without its craft: from where the mission starts, at <paramref name="now"/>, as
    /// if every step before <see cref="Flight.StepIndex"/> was flown exactly.
    /// </summary>
    public static Flight PlanFromStart(Flight flight, double now, MissionPlanContext context)
    {
        if (flight.MissionStart is not { } start || context.Bodies.GetValueOrDefault(start.BodyName) is not { } body)
            throw new ArgumentException("This mission doesn't say where it starts.");
        var orbit = start.AltitudeM is { } h ? new OrbitalElements(body.EquatorialRadiusM + h, 0, 0, 0, 0, 0, now) : null;
        var plans = Plan(flight.Steps, 0, new MissionSituation(start.BodyName, orbit, orbit is null, now), context)
            .Where(p => p.Step >= flight.StepIndex).Select(p => p with { Assumed = true }).ToList();
        return WithPlans(flight, plans, now);
    }

    private static Flight WithPlans(Flight flight, IReadOnlyList<MissionStepPlan> plans, double now)
    {
        var current = plans.FirstOrDefault(p => p.Step == flight.StepIndex);
        return flight with
        {
            StepPlans = plans,
            BodyName = current?.StartBodyName ?? flight.Steps.LastOrDefault()?.BodyName ?? flight.BodyName,
            StartOrbit = current?.StartOrbit,
            Burns = current?.Burns ?? [],
            PlannedUt = now,
            Note = current is null ? "Every step is flown." : current.Problem ?? current.Note,
        };
    }

    /// <summary>
    /// A trip's later hops tell you to plan Go to again once you're there; flying a mission, that happens by itself.
    /// </summary>
    private static string? LaterNote(string? note)
    {
        const string again = "again from here.";
        // The capture's exact burn: the mission re-plans the trip from inside the sphere of influence itself.
        note = note?.Replace("plan Circularize at periapsis again from here for the exact burn", "click Re-plan from here for the exact burn",
            StringComparison.Ordinal);
        if (note is null || !note.StartsWith("Planned from where the trip should leave you", StringComparison.Ordinal)) return note;
        var end = note.IndexOf(again, StringComparison.Ordinal);
        var rest = end < 0 ? "" : note[(end + again.Length)..].Trim();
        return "Planned from where the trip should leave you: it's planned again once you're there." + (rest.Length > 0 ? " " + rest : "");
    }

    private static string Km(double metres) => $"{metres / 1000:#,0.#} km";
}
