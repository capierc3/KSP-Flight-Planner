using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core;

public enum FlightKind
{
    /// <summary>Meet craft in orbit (Rendezvous pane).</summary>
    Rendezvous,
    /// <summary>Deploy a constellation from a carrier orbit (Resonant Orbits pane).</summary>
    Resonant,
    /// <summary>
    /// Burns planned from where the craft was (From here pane): flown as they are, not re-planned, except a trip to a body
    /// (Go to), which keeps <see cref="Flight.DestinationName"/> and <see cref="Flight.AltitudeM"/> to be planned again on the way.
    /// </summary>
    Burns,
    /// <summary>To another planet or moon in a launch window (Transfer Planner): re-planned from wherever the craft is on the way.</summary>
    Transfer,
    /// <summary>Land at a chosen spot (From here, or a mission's landing spot): re-planned from the craft's real orbit.</summary>
    Landing,
}

/// <summary>One burn of a flight, as a maneuver node.</summary>
/// <param name="Title">What it does ("Transfer burn", "Circularize satellite 2").</param>
public sealed record FlightBurn(string Title, double Ut, NodeBurn Burn)
{
    public double DeltaV => Burn.Total;

    /// <summary>
    /// Flown by a craft released from the flight's craft (a satellite after it's decoupled), not by the craft itself:
    /// it doesn't change the flight craft's path.
    /// </summary>
    public bool ByReleasedCraft { get; init; }

    /// <summary>What to do just before, if anything ("Release the next satellite, then switch to it").</summary>
    public string? Note { get; init; }

    /// <summary>A rendezvous burn's part in its leg (a course correction is a <see cref="ManeuverKind.Transfer"/>); null otherwise.</summary>
    public ManeuverKind? Kind { get; init; }
}

/// <summary>The craft flying a flight. <see cref="SaveVesselId"/> is null for a craft not in the save yet.</summary>
public sealed record FlightCraft(string Name, string? SaveVesselId);

/// <summary>A craft to meet, with its orbit when planned and when the plan arrives alongside it.</summary>
public sealed record FlightTarget(string Name, string? SaveVesselId, OrbitalElements Orbit, double ArrivalUt);

/// <summary>One Δv-map step of the way to a flight's body ("Mun — Intercept → Kerbin — Low orbit (80 km)").</summary>
public sealed record FlightApproachStep(string Title, double DeltaV);

/// <summary>
/// A flight that starts around another body: the Δv map's steps from <paramref name="FromBody"/>'s low orbit to the
/// flight body's <paramref name="ParkingAltitudeM"/> orbit, flown before its burns (which start there).
/// </summary>
public sealed record FlightApproach(string FromBody, double ParkingAltitudeM, IReadOnlyList<FlightApproachStep> Steps)
{
    public double DeltaV => Steps.Sum(s => s.DeltaV);
}

/// <summary>
/// A plan handed from a planning pane to the Flight pane: its burns as maneuver nodes, the orbit they start from (to see
/// how far the real craft has drifted), and the plan's inputs, so it can be planned again from the craft's real orbit.
/// </summary>
public sealed record Flight
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public FlightKind Kind { get; init; }
    public string BodyName { get; init; } = "";

    /// <summary>Who flies it; null until chosen (a plan that starts with a launch).</summary>
    public FlightCraft? Craft { get; init; }

    /// <summary>The way there when the craft starts around another body; null once it's around <see cref="BodyName"/>.</summary>
    public FlightApproach? Approach { get; init; }

    /// <summary>The orbit the burns start from (for a launch, the parking orbit), stated at its epoch.</summary>
    public OrbitalElements? StartOrbit { get; init; }

    /// <summary>The burns, in time order.</summary>
    public IReadOnlyList<FlightBurn> Burns { get; init; } = [];

    /// <summary>Game time the burns were (re)planned.</summary>
    public double PlannedUt { get; init; }

    /// <summary>Something to know about the plan (it starts with a launch, an orbit is off…).</summary>
    public string? Note { get; init; }

    /// <summary>Minimum time from re-planning to the first burn.</summary>
    public double LeadS { get; init; } = 300;

    // Rendezvous inputs.
    public IReadOnlyList<FlightTarget> Targets { get; init; } = [];
    public double StayS { get; init; }
    public double? DeorbitPeriapsisAltitudeM { get; init; }

    // Transfer inputs (DestinationName is also a From here trip's): the window (leaving OriginName at TransferDepartureUt, TransferFlightTimeS long) and the arrival.
    // BodyName is the body the burns were last planned around: the origin, then its parent on the way.
    public string? OriginName { get; init; }
    public string? DestinationName { get; init; }
    public double TransferDepartureUt { get; init; }
    public double TransferFlightTimeS { get; init; }
    public ArrivalMode Arrival { get; init; } = ArrivalMode.Circularize;

    /// <summary>A From here trip's inclination at the destination (90° polar); null for however it arrives.</summary>
    public double? ArrivalInclinationDeg { get; init; }

    // Landing inputs: the spot and lander, and whether to take the sooner pass over the cheapest.
    public LandingLeg? Landing { get; init; }
    public bool LandSooner { get; init; }

    // Resonant inputs (AltitudeM is also a transfer's or From here trip's arrival height).
    public int SatelliteCount { get; init; }
    public double AltitudeM { get; init; }
    public bool PreferDiveOrbit { get; init; }

    /// <summary>Satellites released before <see cref="Burns"/> was planned.</summary>
    public int SatellitesReleased { get; init; }

    public override string ToString() => Name;

    /// <summary>
    /// Where the flight craft should be at <paramref name="ut"/> if every burn before then was flown exactly: the start
    /// orbit with each of the craft's own burns applied in turn. Null before the start orbit's epoch (still launching).
    /// </summary>
    public OrbitalElements? PlannedOrbitAt(double ut, double mu)
    {
        if (StartOrbit is not { } orbit || ut < orbit.EpochS) return null;
        foreach (var burn in Burns.Where(b => !b.ByReleasedCraft && b.Ut <= ut && b.Ut >= orbit.EpochS).OrderBy(b => b.Ut))
            orbit = Rendezvous.OrbitAfter(burn.Ut, burn.Burn, orbit, mu);
        return orbit;
    }

    /// <summary>Distance at <paramref name="ut"/> between the craft (on <paramref name="actual"/>) and where the plan has it. docs/MATH.md §8.3.</summary>
    public double? DriftM(OrbitalElements actual, double ut, double mu) =>
        PlannedOrbitAt(ut, mu) is { } planned
            ? (Kepler.StateAt(actual, mu, ut).Position - Kepler.StateAt(planned, mu, ut).Position).Length
            : null;
}

/// <summary>The burns that deploy a constellation from the craft's real orbit, and what to know about them.</summary>
/// <param name="StartOrbit">The craft's orbit the burns start from.</param>
/// <param name="Inserting">The first burn puts the craft on the carrier orbit (false: it's already on it).</param>
public sealed record DeploymentPlan(IReadOnlyList<FlightBurn> Burns, OrbitalElements StartOrbit, bool Inserting, IReadOnlyList<string> Warnings);

/// <summary>
/// Flying a resonant deployment (Resonant Orbits pane): get on the carrier orbit, then release one satellite each time
/// the carrier passes the shared apsis and circularize it there. docs/MATH.md §5.2.
/// </summary>
public static class ResonantDeployment
{
    /// <summary>The craft counts as on the carrier orbit when its period and shared apsis are this close (relative).</summary>
    public const double OnCarrierTolerance = 0.005;

    /// <param name="craft">The carrier's current orbit around <paramref name="body"/>.</param>
    /// <param name="satellites">Satellites still to release.</param>
    /// <param name="earliestUt">No burn before this time.</param>
    /// <param name="firstNumber">The first satellite's number (satellites already released count before it).</param>
    /// <param name="nextReleaseAfterUt">
    /// Partway through (a satellite already released): on the carrier orbit, the next release is the first pass after
    /// this, however soon. Skipping a pass to keep the lead time would put the next satellite in the wrong slot.
    /// </param>
    public static DeploymentPlan Plan(CelestialBody body, ResonantOrbitResult carrier, OrbitalElements craft, int satellites, double earliestUt,
        int firstNumber = 1, double? nextReleaseAfterUt = null)
    {
        var mu = body.Mu;
        var r = carrier.SatelliteRadiusM;
        var carrierOtherApsis = 2 * carrier.CarrierSemiMajorAxisM - r;
        var sharedAnomaly = carrier.IsDiveOrbit ? Math.PI : 0; // the shared apsis: carrier's apoapsis when diving, else periapsis
        var warnings = new List<string>();
        var burns = new List<FlightBurn>();

        var orbit = craft;
        var sharedRadius = craft.SemiMajorAxisM * (carrier.IsDiveOrbit ? 1 + craft.Eccentricity : 1 - craft.Eccentricity);
        var onCarrier = Math.Abs(Kepler.OrbitalPeriod(craft, mu) / carrier.CarrierPeriodS - 1) < OnCarrierTolerance
                        && Math.Abs(sharedRadius / r - 1) < OnCarrierTolerance;
        double after;
        if (onCarrier)
        {
            after = nextReleaseAfterUt ?? earliestUt;
        }
        else
        {
            // Enter where the craft's orbit comes closest to the deployment altitude: an apsis, or anywhere if circular.
            var pe = craft.SemiMajorAxisM * (1 - craft.Eccentricity);
            var ap = craft.SemiMajorAxisM * (1 + craft.Eccentricity);
            var atPeriapsis = Math.Abs(pe - r) <= Math.Abs(ap - r);
            var circular = craft.Eccentricity < 1e-3;
            var ut = circular ? earliestUt : Kepler.NextTimeAtMeanAnomaly(craft, mu, earliestUt, atPeriapsis ? 0 : Math.PI);
            var state = Kepler.StateAt(craft, mu, ut);
            if (Math.Abs(state.Position.Length - r) > 1000)
            {
                // It misses r: first bring that apsis to r, burning half an orbit before (at the other apsis), then enter there.
                var raiseUt = circular ? earliestUt : Kepler.NextTimeAtMeanAnomaly(craft, mu, earliestUt, atPeriapsis ? Math.PI : 0);
                var raiseState = Kepler.StateAt(craft, mu, raiseUt);
                var raiseRadius = raiseState.Position.Length;
                var raiseTarget = QuickBurns.HorizontalVelocity(raiseState, OrbitMath.VisViva(mu, raiseRadius, (raiseRadius + r) / 2));
                var raise = NodeBurn.From(raiseState, raiseTarget - raiseState.Velocity);
                var apsis = r > raiseRadius ? "apoapsis" : "periapsis";
                burns.Add(new FlightBurn($"Bring the {apsis} to {(r - body.EquatorialRadiusM) / 1000:#,0.#} km", raiseUt, raise)
                {
                    Note = $"{(raise.Prograde >= 0 ? "Raises" : "Lowers")} the far side to the deployment altitude, where the carrier orbit starts.",
                });
                orbit = Rendezvous.OrbitAfter(raiseUt, raise, craft, mu);
                // Half an orbit on, at the new apsis.
                ut = Kepler.NextTimeAtMeanAnomaly(orbit, mu, raiseUt + 1, r > raiseRadius ? Math.PI : 0);
                state = Kepler.StateAt(orbit, mu, ut);
            }
            var radius = state.Position.Length;

            // Horizontal at the burn point with the speed for an orbit whose other apsis is the carrier's.
            var target = QuickBurns.HorizontalVelocity(state, OrbitMath.VisViva(mu, radius, (radius + carrierOtherApsis) / 2));
            var burn = NodeBurn.From(state, target - state.Velocity);
            burns.Add(new FlightBurn("Enter the carrier orbit", ut, burn)
            {
                Note = $"{(burn.Prograde >= 0 ? "Raises" : "Lowers")} the far side to {(carrierOtherApsis - body.EquatorialRadiusM) / 1000:#,0.#} km.",
            });
            orbit = Rendezvous.OrbitAfter(ut, burn, orbit, mu);
            after = ut + 1; // releases start on the next pass
        }

        // One release per pass of the shared apsis; each satellite circularizes there.
        var period = Kepler.OrbitalPeriod(orbit, mu);
        var first = Kepler.NextTimeAtMeanAnomaly(orbit, mu, after, sharedAnomaly);
        for (var i = 0; i < satellites; i++)
        {
            var ut = first + i * period;
            var state = Kepler.StateAt(orbit, mu, ut);
            var target = QuickBurns.HorizontalVelocity(state, OrbitMath.CircularVelocity(mu, state.Position.Length));
            burns.Add(new FlightBurn($"Circularize satellite {firstNumber + i}", ut, NodeBurn.From(state, target - state.Velocity))
            {
                ByReleasedCraft = true,
                Note = "A minute or two before: release the satellite, switch to it and send this node to it. The carrier flies on unchanged.",
            });
        }
        return new DeploymentPlan(burns, craft, !onCarrier, warnings);
    }
}

/// <summary>
/// Flying a launch window from the Transfer Planner: the burns of a hop across (§3.8) from the craft's orbit around the
/// origin, or once it has left, a course correction from its path around the parent (§3.7). docs/MATH.md §3.8.
/// </summary>
public static class TransferFlights
{
    /// <summary>A window is gone once its departure is this far (as a fraction of the flight) in the past.</summary>
    public const double LateFraction = 0.02;

    /// <summary>
    /// Aerobraking aims the periapsis this far up the atmosphere (as a fraction of its height), when the arrival height
    /// asked for is above it: as a deorbit (docs/MATH.md §2.5).
    /// </summary>
    public const double AerobrakeFraction = 0.43;

    /// <summary>
    /// <paramref name="flight"/> (a <see cref="FlightKind.Transfer"/>) with its burns planned from <paramref name="orbit"/>,
    /// the craft's orbit around <paramref name="around"/> at <paramref name="now"/>, none sooner than
    /// <paramref name="leadS"/> from then. Throws <see cref="ArgumentException"/> when it can't be planned from there.
    /// </summary>
    public static Flight Plan(Flight flight, IReadOnlyCollection<CelestialBody> bodies, CelestialBody around, OrbitalElements orbit, double now, double leadS)
    {
        CelestialBody Named(string? name) => bodies.FirstOrDefault(b => b.Name == name) ?? throw new ArgumentException($"{name} isn't in the planner's bodies.");
        var origin = Named(flight.OriginName);
        var destination = Named(flight.DestinationName);
        var parent = bodies.FirstOrDefault(b => b.Id == origin.ParentId) ?? throw new ArgumentException($"{origin.Name} doesn't orbit anything.");
        var earliest = now + leadS;
        var height = flight.Arrival == ArrivalMode.Aerobrake && destination.AtmosphereHeightM > 0
            ? Math.Min(flight.AltitudeM, AerobrakeFraction * destination.AtmosphereHeightM)
            : flight.AltitudeM;

        HopPlan plan;
        if (around.Id == origin.Id)
        {
            if (now > flight.TransferDepartureUt + LateFraction * flight.TransferFlightTimeS)
                throw new ArgumentException($"This window left {origin.Name} {GameCalendar.Kerbin.FormatDuration(now - flight.TransferDepartureUt)} ago: " +
                                            $"pick another on the Transfer Planner, or use Go to {destination.Name} in the From here pane.");
            plan = Hops.Across(origin, destination, parent, orbit, earliest, height, flight.TransferDepartureUt, flight.TransferFlightTimeS);
        }
        else if (around.Id == parent.Id)
        {
            plan = Hops.Down(parent, destination, orbit, earliest, height);
        }
        else if (around.Id == destination.Id)
        {
            throw new ArgumentException($"Already around {destination.Name}: use Circularize at periapsis in the From here pane to capture.");
        }
        else
        {
            throw new ArgumentException($"The craft is around {around.Name}: this window leaves from {origin.Name}.");
        }

        var notes = plan.Warnings.ToList();
        if (height < flight.AltitudeM)
        {
            // The hop's own note on the atmosphere says the same, less exactly.
            notes.RemoveAll(n => n.Contains("atmosphere"));
            notes.Add($"Aerobrakes: the periapsis is aimed at {height / 1000:#,0.#} km, in {destination.Name}'s atmosphere, so there's no capture " +
                      "burn. Check the path in KSP: a little higher to skim and aerobrake again, lower to come straight down.");
        }
        if (flight.Arrival != ArrivalMode.Circularize && plan.Capture is not null)
        {
            plan = plan with { Capture = null };
            notes.Add(flight.Arrival == ArrivalMode.Aerobrake
                ? $"No capture burn: you chose to aerobrake at {destination.Name}."
                : $"No capture burn: a flyby of {destination.Name}.");
        }
        return flight with
        {
            BodyName = around.Name,
            StartOrbit = orbit,
            Burns = plan.Burns,
            PlannedUt = now,
            Note = notes.Count > 0 ? string.Join(" ", notes) : null,
        };
    }
}
