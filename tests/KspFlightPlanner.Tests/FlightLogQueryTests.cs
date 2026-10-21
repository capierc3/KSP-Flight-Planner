using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.Tests;

public sealed class FlightLogQueryTests
{
    private static FlightLogSummary Log(long id, string vessel, double launchUt, FlightLogOutcome outcome = FlightLogOutcome.Recovered,
        double durationS = 600, double deltaV = 3000, double maxAltM = 80_000, string? route = null, string? lastBody = "Kerbin") =>
        new(id, vessel, launchUt, 0, outcome, launchUt + durationS, deltaV, maxAltM, null, lastBody, []) { Route = route };

    private static readonly FlightLogSummary[] Logs =
    [
        Log(1, "Mun Lander", 1000, FlightLogOutcome.InFlight, durationS: 90_000, deltaV: 5200, maxAltM: 12_000_000, route: "Kerbin › Mun", lastBody: "Mun"),
        Log(2, "Comsat", 3000, FlightLogOutcome.Lost, durationS: 120, deltaV: 900, maxAltM: 4_000),
        Log(3, "comsat", 2000, durationS: 1_200, deltaV: 3400, maxAltM: 750_000),
        Log(4, "Hopper", 4000, FlightLogOutcome.Reverted, durationS: 30, deltaV: 100, maxAltM: 500),
    ];

    private static long[] Ids(string? search = null, FlightLogShow show = FlightLogShow.All, FlightLogSort sort = FlightLogSort.Rocket) =>
        FlightLogQuery.Apply(Logs, search, show, sort).Select(l => l.Id).ToArray();

    [Fact]
    public void By_rocket_ignores_case_and_puts_each_rockets_newest_flight_first() =>
        Assert.Equal([2, 3, 4, 1], Ids());

    [Theory]
    [InlineData(FlightLogSort.Newest, new long[] { 4, 2, 3, 1 })]
    [InlineData(FlightLogSort.Oldest, new long[] { 1, 3, 2, 4 })]
    [InlineData(FlightLogSort.MostDeltaV, new long[] { 1, 3, 2, 4 })]
    [InlineData(FlightLogSort.Longest, new long[] { 1, 3, 2, 4 })]
    [InlineData(FlightLogSort.Highest, new long[] { 1, 3, 2, 4 })]
    public void Sorts(FlightLogSort sort, long[] expected) => Assert.Equal(expected, Ids(sort: sort));

    [Fact]
    public void Shows_only_flights_that_ended_the_chosen_way()
    {
        Assert.Equal([1], Ids(show: FlightLogShow.InFlight));
        Assert.Equal([3], Ids(show: FlightLogShow.Recovered));
        Assert.Equal([2], Ids(show: FlightLogShow.Lost));
        Assert.Equal([4], Ids(show: FlightLogShow.Reverted));
    }

    [Fact]
    public void Search_matches_rocket_names_and_bodies_with_every_word()
    {
        Assert.Equal([2, 3], Ids("COMSAT"));
        Assert.Equal([1], Ids("mun"));          // the name and the route both have it
        Assert.Equal([1], Ids("lander mun"));
        Assert.Empty(Ids("lander duna"));
        Assert.Equal([2, 3, 4, 1], Ids("   "));
    }

    [Fact]
    public void Search_and_filter_combine() => Assert.Equal([2], Ids("comsat", FlightLogShow.Lost));
}
