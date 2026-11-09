using System.Globalization;
using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core;

/// <summary>
/// A background trip planned again from where the ship really is: its burns, when it meets the target, which way of
/// making the trip it is ("Meets soonest", "Cheapest"…, empty when there was only one), and, when it isn't the way
/// asked for, why (<see cref="Note"/>).
/// </summary>
public sealed record RouteTrip(IReadOnlyList<FlightBurn> Burns, double MeetUt)
{
    public string Way { get; init; } = "";
    public string? Note { get; init; }
    public double DeltaV => Burns.Sum(b => b.DeltaV);
}

/// <summary>
/// Planning a background trip again when the game finds the ship off course (docs/MATH.md §11.3). The planning is the
/// same as From here › Go to… a craft: a meeting around the target's body, with the trip there first when the ship is
/// around another body, the way of making it the player picked when it fits the fuel, else the next that does.
/// Extra cases come from a ship caught mid-trip:
/// <list type="bullet">
/// <item>On a path that escapes another body (leaving the Mun): nothing can be planned until it's out, so it waits.</item>
/// <item>On a path that escapes the target's body, or a stretched orbit round it: it's captured at periapsis first,
/// straight onto the meeting's phasing orbit when the meeting starts with one.</item>
/// </list>
/// </summary>
public static class RouteReplan
{
    /// <summary>The fuel a trip must leave on top of its Δv: this share of it, plus <see cref="FuelMarginMs"/> (the add-on checks the same).</summary>
    public const double FuelMarginShare = 0.05, FuelMarginMs = 30;

    /// <summary>The Δv a trip of <paramref name="deltaV"/> needs in the tanks, with the margin for real burns' losses and braking in.</summary>
    public static double WithMargin(double deltaV) => deltaV * (1 + FuelMarginShare) + FuelMarginMs;

    /// <summary>
    /// The trip from <paramref name="ship"/> (around <paramref name="shipBody"/>) to meet <paramref name="target"/>
    /// (around <paramref name="targetBody"/>), its first burn no sooner than <paramref name="leadS"/> after
    /// <paramref name="now"/>: the way <paramref name="way"/> names when it fits <paramref name="deltaVLeft"/> (with the
    /// margin), else the cheapest that does. Null, with <paramref name="wait"/> saying why, when it can't be planned yet.
    /// Throws <see cref="ArgumentException"/> when it can't be planned at all from here, or no way fits the fuel.
    /// </summary>
    public static RouteTrip? Plan(CelestialBody shipBody, OrbitalElements ship, string shipName, CelestialBody targetBody, RendezvousTarget target,
        IReadOnlyCollection<CelestialBody> bodies, double now, double leadS, out string? wait, string? way = null, double? deltaVLeft = null)
    {
        wait = null;
        if (target.Orbit.Eccentricity >= 1) throw new ArgumentException($"{target.Name} isn't on a closed orbit round {targetBody.Name}.");
        var after = now + leadS;
        var escaping = ship.Eccentricity >= 1;

        List<RouteTrip> ways;
        if (shipBody.Id != targetBody.Id)
        {
            if (escaping)
            {
                wait = $"{shipName} is on its way out of {shipBody.Name}'s pull: it's planned again once it's out.";
                return null;
            }
            // Every way of making the trip, as From here lists them: the trip's burns, then the meeting's from its capture.
            ways = Rendezvous.TripsToTarget(new TripStart(shipBody, bodies), new RendezvousTarget(shipName, ship), targetBody, target, after)
                .Select(t => new RouteTrip(t.Trip.Burns.Concat(RendezvousFlights.Burns(new RendezvousPlan([], -1, [t.Leg], null))).ToList(), t.Leg.ArrivalUt)
                {
                    Way = t.Label,
                })
                .ToList();
        }
        else ways = [Around(shipBody, ship, shipName, targetBody, target, now, leadS, escaping)];

        return Pick(ways, shipName, way, deltaVLeft);
    }

    /// <summary>
    /// The way named (matched by its first word, so "Meets soonest" finds "Balanced · meets soonest" too) if it fits the
    /// fuel, else the cheapest that fits, with a note saying why. Throws when none fits.
    /// </summary>
    private static RouteTrip Pick(List<RouteTrip> ways, string shipName, string? way, double? deltaVLeft)
    {
        var asked = string.IsNullOrWhiteSpace(way) ? null
            : ways.FirstOrDefault(w => w.Way == way) ?? ways.FirstOrDefault(w => w.Way.Contains(way!.Split(' ')[^1], StringComparison.OrdinalIgnoreCase));
        bool Fits(RouteTrip t) => deltaVLeft is not { } left || WithMargin(t.DeltaV) <= left;
        if (asked is not null && Fits(asked)) return asked;
        var cheapest = ways.Where(Fits).OrderBy(t => t.DeltaV).FirstOrDefault();
        var c = CultureInfo.InvariantCulture;
        if (cheapest is null)
        {
            var least = ways.MinBy(t => t.DeltaV)!;
            throw new ArgumentException($"{shipName} has {deltaVLeft!.Value.ToString("N0", c)} m/s of fuel; the cheapest way needs {least.DeltaV.ToString("N0", c)} m/s " +
                                        $"({WithMargin(least.DeltaV).ToString("N0", c)} with the margin).");
        }
        if (asked is null || asked == cheapest) return cheapest;
        return cheapest with
        {
            Note = $"{asked.Way} needs {WithMargin(asked.DeltaV).ToString("N0", c)} m/s with the margin and {shipName} has {deltaVLeft!.Value.ToString("N0", c)}: " +
                   $"took {(cheapest.Way.Length > 0 ? cheapest.Way : "the cheapest")} ({cheapest.DeltaV.ToString("N0", c)} m/s).",
        };
    }

    /// <summary>
    /// A meeting round the body it's already at: a plain rendezvous, or from an orbit still on its way in (stretched or
    /// escaping), the capture into the target's plane straight onto the phasing orbit first (<see cref="ArrivalMeet"/>).
    /// </summary>
    private static RouteTrip Around(CelestialBody body, OrbitalElements ship, string shipName, CelestialBody targetBody, RendezvousTarget target,
        double now, double leadS, bool escaping)
    {
        if (!escaping && !Stretched(ship, target.Orbit))
        {
            var meet = Rendezvous.Plan(new RendezvousRequest(targetBody, [target], now + leadS, 0, new RendezvousTarget(shipName, ship)));
            return new RouteTrip(RendezvousFlights.Burns(meet), meet.Legs[^1].ArrivalUt);
        }
        var arrival = ArrivalMeet.Plan(body, ship, shipName, target, now, leadS);
        return new RouteTrip(arrival.Burns, arrival.MeetUt);
    }

    /// <summary>Its apoapsis is more than half again as far out as the target's: an orbit that's still on its way in.</summary>
    public static bool Stretched(OrbitalElements ship, OrbitalElements target) => ship.Eccentricity < 1 && ArrivalMeet.Needed(ship, target);
}
