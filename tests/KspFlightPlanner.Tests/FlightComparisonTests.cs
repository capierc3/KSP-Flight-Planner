using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.Tests;

public sealed class FlightComparisonTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly PlannerDatabase _db;

    public FlightComparisonTests()
    {
        _db = new PlannerDatabase(_path);
        _db.Initialize();
    }

    public void Dispose()
    {
        TestDb.Release(_path);
        File.Delete(_path);
    }

    private static FlightLog Flight(long id, double launchUt, double massKg, int? parts, double joinedAtMet = 0) => new()
    {
        Id = id,
        VesselName = "Kerbal X",
        LaunchUt = launchUt,
        JoinedAtMetS = joinedAtMet,
        LaunchPartCount = parts,
        Samples = [new FlightSample(launchUt + joinedAtMet, joinedAtMet, "Kerbin", "FLYING", 3, 0, 0, 175, 0, double.NaN, double.NaN, 0, 0, 90, 90, 1,
            massKg, 200_000, 300, 0, 1, 1)],
    };

    [Fact]
    public void Versions_of_a_design_are_told_apart_by_part_count_and_launch_mass()
    {
        var designs = FlightComparison.Designs(
        [
            Flight(1, 1_000, 30_000, 42),
            Flight(2, 2_000, 30_200, 42),    // 0.7 % heavier: same design, a little more fuel
            Flight(3, 3_000, 30_000, 43),    // a part more
            Flight(4, 4_000, 33_000, 42),    // 10 % heavier
            Flight(5, 5_000, 30_100, null),  // parts not counted (left the pad unseen): goes by mass
            Flight(6, 6_000, 20_000, 30, joinedAtMet: 3_600), // joined in orbit: no launch to compare
        ]);

        Assert.Equal(["A", "A", "B", "C", "A", "—"], new long[] { 1, 2, 3, 4, 5, 6 }.Select(id => designs[id]));
    }

    [Fact]
    public void The_best_of_a_figure_is_the_lowest_of_the_flights_that_have_it()
    {
        var a = new FlightFigures(1, 0, FlightLogOutcome.Recovered, "A", 5_000, 3_400, 470, 1_200, 850, 350, 24_000, 6.5, 11);
        var b = a with { LogId = 2, DeltaVToOrbit = 3_300, AscentLoss = null, TouchdownSpeed = 6 };
        Assert.Equal(3_300, FlightComparison.Best([a, b], f => f.DeltaVToOrbit));
        Assert.Equal(1_200, FlightComparison.Best([a, b], f => f.AscentLoss));
        Assert.Equal(6, FlightComparison.Best([a, b], f => f.TouchdownSpeed));
        Assert.Null(FlightComparison.Best([b], f => f.AscentLoss));
    }

    [Fact]
    public void A_flight_that_never_reached_orbit_has_no_orbit_figures()
    {
        var log = Flight(1, 1_000, 30_000, 42);
        var report = FlightAnalysis.Analyze(log, _ => null);
        var figures = FlightComparison.Figures(log, report, "A");
        Assert.Null(figures.DeltaVToOrbit);
        Assert.Null(figures.TimeToOrbitS);
        Assert.Null(figures.TouchdownSpeed);
        Assert.Equal("A", figures.Design);
    }

    [Fact]
    public void The_launch_part_count_and_mass_are_stored()
    {
        var repo = new FlightLogRepository(_db);
        var recorder = new FlightLogRecorder("Kerbal X", Flight(0, 1_000, 30_000, null).Samples[0], null);
        recorder.SetLaunchPartCount(42);
        var id = repo.Save(recorder.Log);

        Assert.Equal(42, repo.Get(id)!.LaunchPartCount);
        var summary = Assert.Single(repo.GetAll());
        Assert.Equal((42, 30_000.0), (summary.LaunchPartCount, summary.LaunchMassKg));
    }
}
