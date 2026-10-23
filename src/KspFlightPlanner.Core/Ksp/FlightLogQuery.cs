namespace KspFlightPlanner.Core.Ksp;

/// <summary>How the Flight log list is ordered.</summary>
public enum FlightLogSort
{
    /// <summary>By rocket name, each rocket's flights newest first (the list groups by rocket).</summary>
    Rocket,
    Newest,
    Oldest,
    MostDeltaV,
    Longest,
    Highest,
}

/// <summary>Which flights the Flight log list shows, by how they ended.</summary>
public enum FlightLogShow
{
    All,
    InFlight,
    Recovered,
    Lost,
    Reverted,
}

/// <summary>Filters and orders the Flight log list.</summary>
public static class FlightLogQuery
{
    /// <summary>
    /// The logs that match <paramref name="show"/> and every word of <paramref name="search"/> (in the rocket's name, the
    /// bodies it flew around, where it is now, or the name its vessel has now), in <paramref name="sort"/> order.
    /// </summary>
    /// <param name="vesselName">The name each flight's vessel has now (null when it's gone or unknown).</param>
    public static IEnumerable<FlightLogSummary> Apply(IEnumerable<FlightLogSummary> logs, string? search, FlightLogShow show, FlightLogSort sort,
        Func<FlightLogSummary, string?>? vesselName = null)
    {
        var words = (search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matching = logs.Where(l => Shows(l, show) && words.All(w => Mentions(l, w) || Has(vesselName?.Invoke(l), w)));
        return sort switch
        {
            FlightLogSort.Newest => matching.OrderByDescending(l => l.LaunchUt),
            FlightLogSort.Oldest => matching.OrderBy(l => l.LaunchUt),
            FlightLogSort.MostDeltaV => matching.OrderByDescending(l => l.DeltaVUsed).ThenByDescending(l => l.LaunchUt),
            FlightLogSort.Longest => matching.OrderByDescending(l => l.DurationS).ThenByDescending(l => l.LaunchUt),
            FlightLogSort.Highest => matching.OrderByDescending(l => l.MaxAltitudeM).ThenByDescending(l => l.LaunchUt),
            _ => matching.OrderBy(l => l.VesselName, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(l => l.LaunchUt),
        };
    }

    private static bool Shows(FlightLogSummary log, FlightLogShow show) => show switch
    {
        FlightLogShow.InFlight => log.Outcome == FlightLogOutcome.InFlight,
        FlightLogShow.Recovered => log.Outcome == FlightLogOutcome.Recovered,
        FlightLogShow.Lost => log.Outcome == FlightLogOutcome.Lost,
        FlightLogShow.Reverted => log.Outcome == FlightLogOutcome.Reverted,
        _ => true,
    };

    private static bool Mentions(FlightLogSummary log, string word) =>
        Has(log.VesselName, word) || Has(log.Route, word) || Has(log.LastBody, word);

    private static bool Has(string? text, string word) => text?.Contains(word, StringComparison.CurrentCultureIgnoreCase) == true;
}
