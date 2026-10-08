using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core;

public enum FlightKind
{
    /// <summary>Meet craft in orbit (Rendezvous tab).</summary>
    Rendezvous,
    /// <summary>Deploy a constellation from a carrier orbit (Resonant Orbits tab).</summary>
    Resonant,
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
}

/// <summary>The craft flying a flight. <see cref="SaveVesselId"/> is null for a craft not in the save yet.</summary>
public sealed record FlightCraft(string Name, string? SaveVesselId);

/// <summary>A craft to meet, with its orbit when planned and when the plan arrives alongside it.</summary>
public sealed record FlightTarget(string Name, string? SaveVesselId, OrbitalElements Orbit, double ArrivalUt);

/// <summary>
/// A plan handed from a planning tab to the Flight tab: its burns as maneuver nodes, the orbit they start from (to see
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

    // Resonant inputs.
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

    /// <summary>Distance at <paramref name="ut"/> between the craft (on <paramref name="actual"/>) and where the plan has it. docs/MATH.md §18.</summary>
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
/// Flying a resonant deployment (Resonant Orbits tab): get on the carrier orbit, then release one satellite each time
/// the carrier passes the shared apsis and circularize it there. docs/MATH.md §18b.
/// </summary>
public static class ResonantDeployment
{
    /// <summary>The craft counts as on the carrier orbit when its period and shared apsis are this close (relative).</summary>
    public const double OnCarrierTolerance = 0.005;

    /// <param name="craft">The carrier's current orbit around <paramref name="body"/>.</param>
    /// <param name="satellites">Satellites still to release.</param>
    /// <param name="earliestUt">No burn before this time.</param>
    /// <param name="firstNumber">The first satellite's number (satellites already released count before it).</param>
    public static DeploymentPlan Plan(CelestialBody body, ResonantOrbitResult carrier, OrbitalElements craft, int satellites, double earliestUt,
        int firstNumber = 1)
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
            after = earliestUt;
        }
        else
        {
            // Burn where the craft's orbit comes closest to the deployment altitude: an apsis, or anywhere if circular.
            var pe = craft.SemiMajorAxisM * (1 - craft.Eccentricity);
            var ap = craft.SemiMajorAxisM * (1 + craft.Eccentricity);
            var atPeriapsis = Math.Abs(pe - r) <= Math.Abs(ap - r);
            var ut = craft.Eccentricity < 1e-3 ? earliestUt : Kepler.NextTimeAtMeanAnomaly(craft, mu, earliestUt, atPeriapsis ? 0 : Math.PI);
            var state = Kepler.StateAt(craft, mu, ut);
            var radius = state.Position.Length;
            if (Math.Abs(radius - r) > 1000)
                warnings.Add($"Your orbit only reaches {(radius - body.EquatorialRadiusM) / 1000:#,0.#} km, not the deployment altitude of " +
                             $"{(r - body.EquatorialRadiusM) / 1000:#,0.#} km: the satellites' orbit and spacing will be off by that much. " +
                             $"Bring your {(atPeriapsis ? "periapsis" : "apoapsis")} to the deployment altitude first for the best result.");

            // Horizontal at the burn point with the speed for an orbit whose other apsis is the carrier's.
            var target = HorizontalVelocity(state, OrbitMath.VisViva(mu, radius, (radius + carrierOtherApsis) / 2));
            var burn = NodeBurn.From(state, target - state.Velocity);
            burns.Add(new FlightBurn("Enter the carrier orbit", ut, burn)
            {
                Note = $"{(burn.Prograde >= 0 ? "Raises" : "Lowers")} the far side to {(carrierOtherApsis - body.EquatorialRadiusM) / 1000:#,0.#} km.",
            });
            orbit = Rendezvous.OrbitAfter(ut, burn, craft, mu);
            after = ut + 1; // releases start on the next pass
        }

        // One release per pass of the shared apsis; each satellite circularizes there.
        var period = Kepler.OrbitalPeriod(orbit, mu);
        var first = Kepler.NextTimeAtMeanAnomaly(orbit, mu, after, sharedAnomaly);
        for (var i = 0; i < satellites; i++)
        {
            var ut = first + i * period;
            var state = Kepler.StateAt(orbit, mu, ut);
            var target = HorizontalVelocity(state, OrbitMath.CircularVelocity(mu, state.Position.Length));
            burns.Add(new FlightBurn($"Circularize satellite {firstNumber + i}", ut, NodeBurn.From(state, target - state.Velocity))
            {
                ByReleasedCraft = true,
                Note = "A minute or two before: release the satellite, switch to it and send this node to it. The carrier flies on unchanged.",
            });
        }
        return new DeploymentPlan(burns, craft, !onCarrier, warnings);
    }

    /// <summary>A velocity of <paramref name="speed"/> at right angles to the radius, in the orbit's plane and direction.</summary>
    private static Vector3d HorizontalVelocity(StateVector state, double speed)
    {
        var up = state.Position.Normalized;
        var horizontal = state.Velocity - up * Vector3d.Dot(state.Velocity, up);
        return horizontal.Normalized * speed;
    }
}
