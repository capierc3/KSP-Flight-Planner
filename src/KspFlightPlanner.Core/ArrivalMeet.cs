using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core;

/// <summary>A meeting from an orbit still on its way in: the burns (the capture first), and the meeting's own plan after it.</summary>
public sealed record ArrivalMeetPlan(IReadOnlyList<FlightBurn> Burns, RendezvousPlan Meet)
{
    public double MeetUt => Meet.Legs[^1].ArrivalUt;
}

/// <summary>
/// Meeting a craft from a stretched or escaping orbit round its body (back from a moon, or in from another planet)
/// (docs/MATH.md §4.7). Planned straight from such an orbit, the cheapest meeting is an intercept that puts almost all
/// its Δv into the match at the target, which can't be flown safely; captured onto a round orbit first and then met,
/// it costs a plane change and a phasing burn as well. Instead the capture at periapsis puts the craft straight into
/// the target's plane on a round orbit, and when the meeting from there starts with its phasing burn at that moment
/// (it usually does: no plane change is left), the two are one burn. Used by From here (Go to a craft, Rendezvous) and
/// by background trips planned again (<see cref="RouteReplan"/>).
/// </summary>
public static class ArrivalMeet
{
    /// <summary>The orbit is still on its way in: escaping, or its apoapsis more than half again as far out as the target's.</summary>
    public static bool Needed(OrbitalElements craft, OrbitalElements target) =>
        craft.Eccentricity >= 1 || craft.SemiMajorAxisM * (1 + craft.Eccentricity) > 1.5 * target.SemiMajorAxisM * (1 + target.Eccentricity);

    /// <summary>
    /// The capture and the meeting from <paramref name="craft"/> round <paramref name="body"/> to meet
    /// <paramref name="target"/>, its first burn no sooner than <paramref name="leadS"/> after <paramref name="now"/>.
    /// Throws <see cref="ArgumentException"/> when the periapsis is in the air or below the ground.
    /// </summary>
    public static ArrivalMeetPlan Plan(CelestialBody body, OrbitalElements craft, string craftName, RendezvousTarget target, double now, double leadS)
    {
        var mu = body.Mu;
        // The periapsis to capture at (the quick burn's own choice of time, and its checks for the air and the ground).
        var round = QuickBurns.Circularize(body, craft, now, leadS, atApoapsis: false);
        var at = Kepler.StateAt(craft, mu, round.Ut);
        var r = at.Position;

        // Into the target's plane: horizontal, at the round-orbit speed, along the target's direction of travel there.
        var t = Kepler.StateAt(target.Orbit, mu, round.Ut);
        var n = Vector3d.Cross(t.Position, t.Velocity).Normalized;
        var along = Vector3d.Cross(n, r).Normalized;
        var want = along * Math.Sqrt(mu / r.Length);
        var capture = new FlightBurn($"Capture into {target.Name}'s plane ({Km(r.Length - body.EquatorialRadiusM)})", round.Ut, NodeBurn.From(at, want - at.Velocity));
        var parked = Rendezvous.OrbitAfter(capture.Ut, capture.Burn, craft, mu);

        var fromCapture = Rendezvous.Plan(new RendezvousRequest(body, [target], capture.Ut, 0, new RendezvousTarget(craftName, parked)));
        var meetBurns = RendezvousFlights.Burns(fromCapture);
        if (meetBurns.Count > 0 && Math.Abs(meetBurns[0].Ut - capture.Ut) < 1)
        {
            // At the same point and the same moment both burns have the same node axes, so their Δv adds.
            var a = capture.Burn;
            var b = meetBurns[0].Burn;
            var merged = new FlightBurn($"Capture onto the phasing orbit for {target.Name}", capture.Ut,
                new NodeBurn(a.Prograde + b.Prograde, a.Normal + b.Normal, a.Radial + b.Radial));
            return new ArrivalMeetPlan([merged, .. meetBurns.Skip(1)], fromCapture);
        }

        // No phasing burn to fold in: the capture, then the meeting a little after it.
        var later = Rendezvous.Plan(new RendezvousRequest(body, [target], capture.Ut + Rendezvous.BurnGapS, 0, new RendezvousTarget(craftName, parked)));
        return new ArrivalMeetPlan([capture, .. RendezvousFlights.Burns(later)], later);
    }

    private static string Km(double m) => $"{m / 1000:0} km";
}
