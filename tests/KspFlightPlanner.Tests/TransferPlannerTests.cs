using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;
using Xunit.Abstractions;

namespace KspFlightPlanner.Tests;

public sealed class TransferPlannerTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly Dictionary<string, CelestialBody> _stock;

    public TransferPlannerTests(ITestOutputHelper output)
    {
        _output = output;
        var db = new PlannerDatabase(_path);
        db.Initialize();
        var repo = new BodyRepository(db);
        _stock = repo.GetBodies(repo.GetSystems()[0].Id).ToDictionary(b => b.Name);
    }

    public void Dispose()
    {
        TestDb.Release(_path);
        File.Delete(_path);
    }

    private TransferRequest KerbinToDuna(ArrivalMode arrival = ArrivalMode.Circularize) => new(
        _stock["Kerbin"], _stock["Duna"], _stock["Kerbol"],
        ParkingAltitudeM: 80_000, ArrivalAltitudeM: 60_000, EarliestDepartureUt: 0, arrival);

    [Fact]
    public void Lambert_matches_curtis_example_5_2()
    {
        // Curtis, Orbital Mechanics for Engineering Students, Example 5.2 (km, km/s)
        var s = Lambert.Solve(new Vector3d(5000, 10000, 2100), new Vector3d(-14600, 2500, 7000), 3600, 398600)!.Value;

        Assert.Equal(-5.9925, s.DepartureVelocity.X, 0.001);
        Assert.Equal(1.9254, s.DepartureVelocity.Y, 0.001);
        Assert.Equal(3.2456, s.DepartureVelocity.Z, 0.001);
        Assert.Equal(-3.3125, s.ArrivalVelocity.X, 0.001);
        Assert.Equal(-4.1966, s.ArrivalVelocity.Y, 0.001);
        Assert.Equal(-0.38529, s.ArrivalVelocity.Z, 0.001);
    }

    [Fact]
    public void Kepler_propagates_kerbin_on_a_circle()
    {
        var kerbin = _stock["Kerbin"];
        var mu = _stock["Kerbol"].Mu;
        var period = Kepler.OrbitalPeriod(kerbin.Orbit!, mu);
        Assert.Equal(9_203_545, period, 1.0); // Kerbin's sidereal year

        var start = Kepler.StateAt(kerbin.Orbit!, mu, 0);
        var full = Kepler.StateAt(kerbin.Orbit!, mu, period);
        Assert.Equal(kerbin.Orbit!.SemiMajorAxisM, start.Position.Length, 1.0);
        Assert.Equal(9284.5, start.Velocity.Length, 0.5);
        Assert.True((start.Position - full.Position).Length < 1.0);
        Assert.True(start.Position.X < 0); // mean anomaly ≈ π at epoch
    }

    [Fact]
    public void Kerbin_to_duna_hohmann_matches_known_values()
    {
        var h = TransferPlanner.Hohmann(KerbinToDuna());

        Assert.Equal(44.36, h.PhaseAngleDeg, 0.05);
        Assert.InRange(h.TransferTimeS, 6_522_000, 6_526_000);
        Assert.InRange(h.EjectionDv, 1_050, 1_090); // community Δv map: ~1,060 m/s
        Assert.Equal("prograde", h.Ejection.Reference);
        Assert.InRange(h.Ejection.AngleDeg, 140, 150);
    }

    [Fact]
    public void Kerbin_to_duna_best_window_is_close_to_hohmann()
    {
        var plan = TransferPlanner.Plan(KerbinToDuna());
        var w = plan.BestWindow;
        var cal = GameCalendar.Kerbin;

        _output.WriteLine($"Depart {cal.FormatDate(w.DepartureUt)}  arrive {cal.FormatDate(w.ArrivalUt)}  flight {cal.FormatDuration(w.FlightTimeS)}");
        _output.WriteLine($"Phase {w.PhaseAngleDeg:0.00}°  eject {w.EjectionDv:0} m/s ({w.Ejection})  capture {w.CaptureDv:0} m/s  total {w.TotalDv:0}");

        Assert.InRange(w.DepartureUt, 0, plan.Hohmann.SynodicPeriodS * 1.1);
        Assert.InRange(w.PhaseAngleDeg, 30, 60);
        Assert.InRange(w.EjectionDv, 1_000, 1_200);
        Assert.True(w.TotalDv < plan.Hohmann.EjectionDv + plan.Hohmann.CaptureDv + 300);

        // The refined optimum is no worse than any grid cell.
        var gridMin = plan.Porkchop.TotalDv.Where(double.IsFinite).Min();
        Assert.True(w.TotalDv <= gridMin + 0.01);
    }

    [Fact]
    public void Aerobraking_removes_capture_burn()
    {
        var w = TransferPlanner.Plan(KerbinToDuna(ArrivalMode.Aerobrake)).BestWindow;
        Assert.Equal(0, w.CaptureDv);
        Assert.Equal(w.EjectionDv, w.TotalDv);
    }

    [Fact]
    public void Reverse_swaps_bodies_and_altitudes()
    {
        var back = KerbinToDuna().Reverse(1234, ArrivalMode.Aerobrake);

        Assert.Equal("Duna", back.Origin.Name);
        Assert.Equal("Kerbin", back.Destination.Name);
        Assert.Equal(60_000, back.ParkingAltitudeM);
        Assert.Equal(80_000, back.ArrivalAltitudeM);
        Assert.Equal(1234, back.EarliestDepartureUt);
        Assert.Equal(ArrivalMode.Aerobrake, back.Arrival);
    }

    [Fact]
    public void Duna_round_trip_returns_after_arrival()
    {
        var outbound = KerbinToDuna();
        var there = TransferPlanner.Plan(outbound).BestWindow;
        var returnRequest = outbound.Reverse(there.ArrivalUt, ArrivalMode.Aerobrake);
        var back = TransferPlanner.Plan(returnRequest).BestWindow;
        var cal = GameCalendar.Kerbin;

        _output.WriteLine($"Arrive Duna {cal.FormatDate(there.ArrivalUt)}");
        _output.WriteLine($"Leave Duna {cal.FormatDate(back.DepartureUt)} (stay {cal.FormatDuration(back.DepartureUt - there.ArrivalUt)}), " +
                          $"home {cal.FormatDate(back.ArrivalUt)}");
        _output.WriteLine($"Phase {back.PhaseAngleDeg:0.0}°  eject {back.EjectionDv:0} m/s ({back.Ejection})  v∞ home {back.ArrivalVInf:0} m/s");

        Assert.True(back.DepartureUt >= there.ArrivalUt);
        Assert.InRange(back.EjectionDv, 550, 800);  // Hohmann from 60 km Duna orbit ≈ 616 m/s
        Assert.Equal(0, back.CaptureDv);             // aerobraking at Kerbin
        Assert.True(back.PhaseAngleDeg < 0);          // Kerbin is behind Duna for an inward transfer
        Assert.Equal("retrograde", back.Ejection.Reference);

        var budget = MissionBudget.Build(outbound, there, includeLanding: true, (returnRequest, back));
        Assert.Contains(budget, l => l.Stage == "Launch: Duna surface → 60 km orbit" && l.DeltaV == 1450);
        Assert.Contains(budget, l => l.Stage == "Return ejection burn");
        Assert.Contains(budget, l => l.Stage.StartsWith("Capture at Kerbin by aerobraking") && l.Note!.Contains("Atmosphere entry"));
    }

    [Fact]
    public void Rejects_bodies_with_different_parents()
    {
        var req = KerbinToDuna() with { Destination = _stock["Mun"] };
        Assert.Throws<ArgumentException>(() => TransferPlanner.Plan(req));
    }

    [Fact]
    public void Kerbin_calendar_formats_dates()
    {
        var cal = GameCalendar.Kerbin;
        Assert.Equal("Year 1, Day 1, 0:00:00", cal.FormatDate(0));
        Assert.Equal("Year 2, Day 1, 0:00:00", cal.FormatDate(426 * 6 * 3600));
        Assert.Equal(235 * 21600 + 3 * 3600, cal.ToUniversalTime(1, 236, 3));
    }
}
