using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core;

/// <summary>A background trip planned again from where the ship really is: its burns and when it meets the target.</summary>
public sealed record HaulerTrip(IReadOnlyList<FlightBurn> Burns, double MeetUt);

/// <summary>
/// Planning a background trip again when the game finds the ship off course (docs/MATH.md §11.3). The planning is the
/// same as From here › Go to… a craft: a meeting around the target's body, with the trip there first when the ship is
/// around another body. Two extra cases come from a ship caught mid-trip:
/// <list type="bullet">
/// <item>On a path that escapes another body (leaving the Mun): nothing can be planned until it's out, so it waits.</item>
/// <item>On a path that escapes the target's body (coming in from another planet): it's captured at periapsis first.</item>
/// </list>
/// </summary>
public static class HaulerReplan
{
    /// <summary>
    /// The trip from <paramref name="ship"/> (around <paramref name="shipBody"/>) to meet <paramref name="target"/>
    /// (around <paramref name="targetBody"/>), its first burn no sooner than <paramref name="leadS"/> after
    /// <paramref name="now"/>. Null, with <paramref name="wait"/> saying why, when it can't be planned yet. Throws
    /// <see cref="ArgumentException"/> when it can't be planned at all from here.
    /// </summary>
    public static HaulerTrip? Plan(CelestialBody shipBody, OrbitalElements ship, string shipName, CelestialBody targetBody, RendezvousTarget target,
        IReadOnlyCollection<CelestialBody> bodies, double now, double leadS, out string? wait)
    {
        wait = null;
        if (target.Orbit.Eccentricity >= 1) throw new ArgumentException($"{target.Name} isn't on a closed orbit round {targetBody.Name}.");
        var after = now + leadS;
        var escaping = ship.Eccentricity >= 1;

        if (shipBody.Id != targetBody.Id)
        {
            if (escaping)
            {
                wait = $"{shipName} is on its way out of {shipBody.Name}'s pull: it's planned again once it's out.";
                return null;
            }
            var far = Rendezvous.Plan(new RendezvousRequest(targetBody, [target], after, 0, new RendezvousTarget(shipName, ship))
            {
                From = new TripStart(shipBody, bodies),
            });
            return new HaulerTrip(RendezvousFlights.Burns(far), far.Legs[^1].ArrivalUt);
        }

        var burns = new List<FlightBurn>();
        if (escaping)
        {
            var capture = QuickBurns.Circularize(shipBody, ship, now, leadS, atApoapsis: false);
            burns.Add(capture with { Title = "Capture: " + capture.Title });
            ship = Rendezvous.OrbitAfter(capture.Ut, capture.Burn, ship, shipBody.Mu);
            after = capture.Ut + Rendezvous.BurnGapS;
        }
        var meet = Rendezvous.Plan(new RendezvousRequest(targetBody, [target], after, 0, new RendezvousTarget(shipName, ship)));
        burns.AddRange(RendezvousFlights.Burns(meet));
        return new HaulerTrip(burns, meet.Legs[^1].ArrivalUt);
    }
}
