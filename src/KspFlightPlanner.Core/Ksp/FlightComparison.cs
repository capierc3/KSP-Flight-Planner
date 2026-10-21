namespace KspFlightPlanner.Core.Ksp;

/// <summary>One flight's headline figures, side by side with the rocket's other flights. Null where it doesn't apply.</summary>
public sealed record FlightFigures(
    long LogId,
    double LaunchUt,
    FlightLogOutcome Outcome,
    string Design,
    double DeltaVUsed,
    double? DeltaVToOrbit,
    double? TimeToOrbitS,
    double? AscentLoss,
    double? GravityLoss,
    double? DragAndSteeringLoss,
    double? MaxQPa,
    double MaxG,
    double? TouchdownSpeed);

/// <summary>A rocket's flights compared: the same figures for each, and which version of the design each flew. docs/MATH.md §8.9.</summary>
public static class FlightComparison
{
    /// <summary>Launch masses within this fraction of each other are the same design (fuel loads round, a crew member more or less).</summary>
    public const double SameMassFraction = 0.01;

    /// <summary>
    /// A letter per version of the design, in the order first flown: the same part count on the pad (when both were
    /// counted) and a launch mass within <see cref="SameMassFraction"/>. A flight joined under way has no launch to go
    /// by and gets "—".
    /// </summary>
    public static IReadOnlyDictionary<long, string> Designs(IEnumerable<FlightLog> logs)
    {
        var designs = new List<(int? Parts, double MassKg)>();
        var result = new Dictionary<long, string>();
        foreach (var log in logs.OrderBy(l => l.LaunchUt))
        {
            if (!log.FromLaunch || log.LaunchMassKg <= 0)
            {
                result[log.Id] = "—";
                continue;
            }
            var index = designs.FindIndex(d => (d.Parts is null || log.LaunchPartCount is null || d.Parts == log.LaunchPartCount)
                                               && Math.Abs(d.MassKg - log.LaunchMassKg) <= SameMassFraction * d.MassKg);
            if (index < 0)
            {
                designs.Add((log.LaunchPartCount, log.LaunchMassKg));
                index = designs.Count - 1;
            }
            result[log.Id] = Letter(index);
        }
        return result;
    }

    public static FlightFigures Figures(FlightLog log, FlightReport report, string design)
    {
        var ascent = report.Ascent;
        var reached = ascent is { ReachedOrbit: true };
        return new FlightFigures(log.Id, log.LaunchUt, log.Outcome, design, log.DeltaVUsed,
            reached ? ascent!.DeltaVSpent : null,
            reached ? ascent!.EndMetS : null,
            reached ? ascent!.TotalLoss : null,
            reached ? ascent!.GravityLoss : null,
            reached ? ascent!.DragAndSteeringLoss : null,
            ascent is { MaxQPa: > 0 } ? ascent.MaxQPa : null,
            report.MaxG,
            report.Touchdowns.Count > 0 ? Math.Abs(report.Touchdowns[^1].VerticalSpeed) : null);
    }

    /// <summary>The lowest of a figure across flights (lower is better for every compared figure), or null if none has it.</summary>
    public static double? Best(IEnumerable<FlightFigures> flights, Func<FlightFigures, double?> figure) =>
        flights.Select(figure).Where(v => v is not null).Min();

    private static string Letter(int index) => index < 26 ? ((char)('A' + index)).ToString() : $"#{index + 1}";
}
