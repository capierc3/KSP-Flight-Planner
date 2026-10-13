using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core;

/// <summary>A rendezvous flight's burns and targets re-planned partway through, and what changed, in a line.</summary>
public sealed record RestPlan(IReadOnlyList<FlightBurn> Burns, IReadOnlyList<FlightTarget> Targets, string Summary);

/// <summary>
/// Re-planning the rest of a rendezvous leg once some of it has been flown, keeping its steps: the burns already flown
/// stay, any still to come before the last one stay as planned, and the last burn before the meeting is solved again
/// from the craft's real orbit. On the coast after the transfer, a course correction is added. The meeting time may
/// move a little, to keep clear of the 180° transfer. docs/MATH.md §8.4.
/// </summary>
public static class FlightReplan
{
    /// <summary>A predicted miss closer than this isn't worth a correction burn; the match-velocity burn is still updated.</summary>
    public const double CorrectionMissM = 500;

    /// <summary>Closer to the meeting than this, nothing is re-planned: fly the last part by eye.</summary>
    public const double MinCoastS = 120;

    /// <summary>How far (as a fraction of the time left to the meeting) the re-solved burn and the meeting may move.</summary>
    public const double TimeSlack = 0.15;

    /// <summary>
    /// The flight's burns with the current leg's rest re-planned from <paramref name="craft"/> (the real orbit at
    /// <paramref name="now"/>), or null when the leg hasn't started (nothing flown, or the plane match still to come):
    /// then the whole leg is planned again (<see cref="Rendezvous.Plan"/>). Throws <see cref="ArgumentException"/> when
    /// the meeting is too close to re-plan.
    /// </summary>
    /// <param name="targetOrbit">The target's orbit now (live when known, else as planned).</param>
    /// <param name="leadS">The soonest a new burn is placed after <paramref name="now"/>.</param>
    public static RestPlan? RendezvousRest(Flight flight, CelestialBody body, OrbitalElements craft, Func<FlightTarget, OrbitalElements> targetOrbit,
        double now, double leadS)
    {
        var mu = body.Mu;
        var index = flight.Targets.ToList().FindIndex(t => t.ArrivalUt > now);
        if (index < 0) return null;
        var target = flight.Targets[index];
        var legStart = index == 0 ? double.NegativeInfinity : flight.Targets[index - 1].ArrivalUt;
        bool InLeg(FlightBurn b) => !b.ByReleasedCraft && b.Ut > legStart && b.Ut <= target.ArrivalUt + 1;

        var leg = flight.Burns.Where(InLeg).OrderBy(b => b.Ut).ToList();
        var upcoming = leg.Where(b => b.Ut >= now).ToList();
        if (upcoming.Count == 0 || upcoming[^1].Kind != ManeuverKind.MatchVelocity) return null;
        if (upcoming.Count == leg.Count || upcoming.Any(b => b.Kind == ManeuverKind.PlaneChange)) return null;

        var match = upcoming[^1];
        var arrival = match.Ut;
        var goal = targetOrbit(target);
        // The meeting can't slip into the next leg's first burn.
        var latestArrival = flight.Burns.Where(b => !b.ByReleasedCraft && b.Ut > arrival + 1).Select(b => b.Ut - Rendezvous.BurnGapS)
            .DefaultIfEmpty(double.PositiveInfinity).Min();

        // Where the craft meets nobody if it flies the rest as planned: the error being fixed.
        var asPlanned = upcoming.Take(upcoming.Count - 1).Aggregate(craft, (o, b) => Rendezvous.OrbitAfter(b.Ut, b.Burn, o, mu));
        var miss = (Kepler.StateAt(asPlanned, mu, arrival).Position - Kepler.StateAt(goal, mu, arrival).Position).Length;

        List<FlightBurn> rest;
        string summary;
        if (upcoming.Count >= 2)
        {
            // Burns still to come before the meeting: keep all but the last as planned, and solve the last again.
            var last = upcoming[^2];
            var before = upcoming.Take(upcoming.Count - 2).ToList();
            var orbit = before.Aggregate(craft, (o, b) => Rendezvous.OrbitAfter(b.Ut, b.Burn, o, mu));
            var earliest = Math.Max(now + leadS, before.Count > 0 ? before[^1].Ut + Rendezvous.BurnGapS : double.NegativeInfinity);
            var departure = Math.Max(last.Ut, earliest);
            var flight0 = arrival - departure;
            if (flight0 < MinCoastS) throw new ArgumentException("Too close to the meeting to re-plan: fly the last part by eye.");
            var (transfer, arrive) = Solve(body, orbit, goal, Math.Max(earliest, departure - TimeSlack * flight0), departure + TimeSlack * flight0,
                flight0 * (1 - TimeSlack), flight0 * (1 + TimeSlack), latestArrival);
            rest = [.. before, last with { Ut = transfer.Ut, Burn = transfer.Burn }, match with { Ut = arrive.Ut, Burn = arrive.Burn }];
            summary = $"{Name(last)} re-solved from the real orbit: {Speed(transfer.DeltaV)} (was {Speed(last.DeltaV)})";
        }
        else
        {
            // Coasting to the meeting: a course correction, if the miss is worth one.
            var coast = arrival - (now + leadS);
            if (coast < MinCoastS)
                throw new ArgumentException($"Passing {Distance(miss)} from {target.Name}, too close to the meeting to correct: fly the last part by eye.");
            if (miss < CorrectionMissM)
            {
                var s = Kepler.StateAt(craft, mu, arrival);
                rest = [match with { Burn = NodeBurn.From(s, Kepler.StateAt(goal, mu, arrival).Velocity - s.Velocity) }];
                summary = $"On course: passing {Distance(miss)} from {target.Name}, no correction needed";
            }
            else
            {
                // Correcting later is cheaper for a tilt, sooner for a speed error: search the first half of the coast.
                var start = now + leadS;
                var (correction, arrive) = Solve(body, craft, goal, start, start + 0.5 * coast,
                    0.5 * coast * (1 - TimeSlack), coast * (1 + TimeSlack), latestArrival);
                rest =
                [
                    new FlightBurn($"Course correction: {target.Name}", correction.Ut, correction.Burn) { Kind = ManeuverKind.Transfer },
                    match with { Ut = arrive.Ut, Burn = arrive.Burn },
                ];
                summary = $"Course correction {Speed(correction.DeltaV)}: without it you'd pass {Distance(miss)} from {target.Name}";
            }
        }
        var newArrival = rest[^1].Ut;
        summary += $"; match velocity {Speed(rest[^1].DeltaV)} (was {Speed(match.DeltaV)})";
        if (Math.Abs(newArrival - arrival) >= 1) summary += $", meeting {Shift(newArrival - arrival)}";
        summary += ".";

        var kept = flight.Burns.Where(b => !InLeg(b) || b.Ut < now);
        var targets = flight.Targets.Select((t, i) => i == index ? t with { Orbit = goal, ArrivalUt = newArrival } : t).ToList();
        return new RestPlan(kept.Concat(rest).OrderBy(b => b.Ut).ToList(), targets, summary);
    }

    /// <summary>The cheapest intercept in the window that meets no later than <paramref name="latestArrival"/>.</summary>
    private static (Maneuver Transfer, Maneuver Match) Solve(CelestialBody body, OrbitalElements chaser, OrbitalElements target,
        double depStart, double depEnd, double tofMin, double tofMax, double latestArrival)
    {
        tofMax = Math.Min(tofMax, latestArrival - depStart);
        if (tofMax <= Math.Max(60, tofMin) - 1) tofMin = Math.Max(60, tofMax * 0.8);
        var best = Rendezvous.CheapestIntercept(chaser, target, body.Mu, depStart, depEnd, tofMin, tofMax);
        return Rendezvous.Intercept(body, chaser, target, best.DepartureUt, best.FlightTimeS);
    }

    private static string Name(FlightBurn b) => b.Title.Split(':')[0];

    private static string Speed(double metresPerSecond) => $"{metresPerSecond:0.0} m/s";

    private static string Distance(double metres) => metres < 1000 ? $"{metres:0} m" : $"{metres / 1000:#,0.#} km";

    private static string Shift(double seconds)
    {
        var size = Math.Abs(seconds) < 60 ? $"{Math.Abs(seconds):0} s" : $"{Math.Abs(seconds) / 60:0.#} min";
        return seconds > 0 ? $"{size} later" : $"{size} sooner";
    }
}
