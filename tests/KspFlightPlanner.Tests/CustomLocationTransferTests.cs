using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.Tests;

public sealed class CustomLocationTransferTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly PlannerDatabase _db;
    private readonly Dictionary<string, CelestialBody> _stock;

    public CustomLocationTransferTests()
    {
        _db = new PlannerDatabase(_path);
        _db.Initialize();
        var repo = new BodyRepository(_db);
        _stock = repo.GetBodies(repo.GetSystems()[0].Id).ToDictionary(b => b.Name);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private CelestialBody Kerbin => _stock["Kerbin"];
    private CelestialBody Kerbol => _stock["Kerbol"];

    private CelestialBody Stand(CustomLocation location) =>
        CustomLocationOrbits.ToBody(location, _stock[location.BodyName], Kerbin, Kerbol);

    private TransferRequest ToLocation(CelestialBody target, double earliest = 0) =>
        new(Kerbin, target, Kerbol, ParkingAltitudeM: 80_000, ArrivalAltitudeM: 1, earliest);

    // ---------------- Position ----------------

    [Fact]
    public void Phase_angle_entry_puts_the_location_ahead_of_kerbin()
    {
        var eye = CustomLocation.Circular(1, "Eye", "Kerbol", 10_000_000_000) with
        {
            IsPlaced = true, Mode = PositionMode.PhaseAngle, PhaseAngleDeg = 45, EpochUt = 1_000_000,
        };
        var orbit = CustomLocationOrbits.ToElements(eye, Kerbol, Kerbin, Kerbol);

        Assert.Equal(45, CustomLocationOrbits.PhaseAngleFromHome(orbit, Kerbol, Kerbin, 1_000_000), 0.01);

        // Later the gap changes by (n_eye − n_kerbin)·t: the inner orbit pulls ahead.
        var nEye = 2 * Math.PI / Kepler.OrbitalPeriod(orbit, Kerbol.Mu);
        var nKerbin = 2 * Math.PI / Kepler.OrbitalPeriod(Kerbin.Orbit!, Kerbol.Mu);
        var expected = 45 + (nEye - nKerbin) * 500_000 * 180 / Math.PI;
        Assert.Equal(expected, CustomLocationOrbits.PhaseAngleFromHome(orbit, Kerbol, Kerbin, 1_500_000), 0.01);
    }

    [Fact]
    public void Element_entry_uses_mean_anomaly_at_the_given_date()
    {
        var station = CustomLocation.Circular(2, "Station", "Kerbin", 150_000) with
        {
            IsPlaced = true, Mode = PositionMode.Elements, MeanAnomalyDeg = 90, EpochUt = 50_000,
        };
        var orbit = CustomLocationOrbits.ToElements(station, Kerbin);
        Assert.Equal(Math.PI / 2, orbit.MeanAnomalyAtEpochRad, 1e-12);
        Assert.Equal(50_000, orbit.EpochS);

        // Circular, LAN = ω = 0: at the epoch it sits a quarter turn round, on the +Y axis.
        var p = Kepler.StateAt(orbit, Kerbin.Mu, 50_000).Position;
        Assert.Equal(750_000, p.Y, 0.001);
        Assert.Equal(0, p.X, 0.001);
    }

    [Theory]
    [InlineData("Kerbin", 150_000, 150_000, 0, "orbits around the star")]
    [InlineData("Kerbol", 9_000_000_000, 11_000_000_000, 0, "circular orbit with 0°")]
    [InlineData("Kerbol", 10_000_000_000, 10_000_000_000, 5, "circular orbit with 0°")]
    public void Phase_angle_entry_is_only_for_simple_sun_orbits(string body, double pe, double ap, double inc, string reason)
    {
        var location = new CustomLocation(1, "Test", body, pe, ap, inc, IsPlaced: true, Mode: PositionMode.PhaseAngle);
        var ex = Assert.Throws<ArgumentException>(() => CustomLocationOrbits.ValidatePosition(location, _stock[body], Kerbin));
        Assert.Contains(reason, ex.Message);
    }

    // ---------------- Transfer planning ----------------

    [Fact]
    public void Planned_location_has_no_window_and_matches_the_dv_map()
    {
        var eye = Stand(CustomLocation.Circular(1, "Eye of Kerbol", "Kerbol", 10_000_000_000));
        var plan = TransferPlanner.PlanAnyTime(ToLocation(eye, earliest: 123_456));
        var w = plan.BestWindow;

        Assert.True(plan.IsAnyTime);
        Assert.Null(plan.Porkchop);
        Assert.Equal(123_456, w.DepartureUt);
        Assert.True(double.IsNaN(w.PhaseAngleDeg));
        // Same Hohmann numbers as the Δv map (docs/MATH.md §4b): ejection 1,013.7 from 80 km, arrival 723.2.
        Assert.Equal(1_013.7, w.EjectionDv, 0.1);
        Assert.Equal(723.2, w.CaptureDv, 0.1);
        Assert.InRange(w.FlightTimeS, 174 * 21_600.0, 176 * 21_600.0);
    }

    [Fact]
    public void Placed_location_gets_a_real_window_and_a_rendezvous_arrival()
    {
        var eye = Stand(CustomLocation.Circular(1, "Eye of Kerbol", "Kerbol", 10_000_000_000) with
        {
            IsPlaced = true, Mode = PositionMode.PhaseAngle, PhaseAngleDeg = 0, EpochUt = 0,
        });
        var plan = TransferPlanner.Plan(ToLocation(eye));
        var w = plan.BestWindow;

        Assert.False(plan.IsAnyTime);
        Assert.NotNull(plan.Porkchop);
        Assert.InRange(w.DepartureUt, 0, plan.Hohmann.SynodicPeriodS * 1.1);
        Assert.Equal(w.ArrivalVInf, w.CaptureDv, 1e-6);       // no gravity: arrival burn = relative speed
        Assert.True(w.Ejection.InclinationDeg is > -1 and < 1);
        Assert.InRange(w.TotalDv, 1_700, 2_000);              // close to the ideal 1,013.7 + 723.2
    }

    [Fact]
    public void Leaving_a_custom_location_is_a_direct_burn()
    {
        var eye = Stand(CustomLocation.Circular(1, "Eye of Kerbol", "Kerbol", 10_000_000_000));
        var home = new TransferRequest(eye, Kerbin, Kerbol, 1, 80_000, 0, ArrivalMode.Aerobrake);
        var h = TransferPlanner.Hohmann(home);

        Assert.True(h.Ejection.IsDirect);
        Assert.Equal(h.DepartureVInf, h.EjectionDv, 1e-6);
        Assert.Equal(723.2, h.EjectionDv, 0.1);
    }

    [Fact]
    public void Budget_to_a_custom_location_has_a_rendezvous_and_no_landing()
    {
        var eye = Stand(CustomLocation.Circular(1, "Eye of Kerbol", "Kerbol", 10_000_000_000));
        var request = ToLocation(eye);
        var budget = MissionBudget.Build(request, TransferPlanner.PlanAnyTime(request).BestWindow, includeLanding: true);

        Assert.Contains(budget, l => l.Stage == "Match orbit with Eye of Kerbol (rendezvous)");
        Assert.DoesNotContain(budget, l => l.Stage.StartsWith("Landing"));
        Assert.Contains(budget, l => l.Stage.StartsWith("Launch: Kerbin surface"));
    }

    // ---------------- Storage ----------------

    [Fact]
    public void Position_fields_round_trip_and_old_rows_default_to_planned()
    {
        var repo = new CustomLocationRepository(_db);
        var id = repo.Add(CustomLocation.Circular(0, "Eye", "Kerbol", 10_000_000_000) with
        {
            IsPlaced = true, Mode = PositionMode.PhaseAngle, PhaseAngleDeg = -30, EpochUt = 21_600 * 500,
        });
        var saved = repo.Get(id)!;
        Assert.True(saved.IsPlaced);
        Assert.Equal(PositionMode.PhaseAngle, saved.Mode);
        Assert.Equal(-30, saved.PhaseAngleDeg);
        Assert.Equal(21_600 * 500, saved.EpochUt);

        var quick = repo.Get(repo.Add("Station", "Kerbin", 150_000))!;
        Assert.False(quick.IsPlaced);
        Assert.Equal(PositionMode.Elements, quick.Mode);
    }
}
