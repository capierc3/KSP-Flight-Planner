using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;
using Xunit.Abstractions;

namespace KspFlightPlanner.Tests;

public sealed class MoonTransferTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly CelestialBody _kerbin, _mun, _minmus;

    public MoonTransferTests(ITestOutputHelper output)
    {
        _output = output;
        var db = new PlannerDatabase(_path);
        db.Initialize();
        var repo = new BodyRepository(db);
        var bodies = repo.GetBodies(repo.GetSystems()[0].Id);
        _kerbin = bodies.Single(b => b.Name == "Kerbin");
        _mun = bodies.Single(b => b.Name == "Mun");
        _minmus = bodies.Single(b => b.Name == "Minmus");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    /// <summary>A circular equatorial orbit around Kerbin at <paramref name="altitudeM"/>, stated at UT 1,000,000.</summary>
    private OrbitalElements Circular(double altitudeM, double inclinationDeg = 0) =>
        new(_kerbin.EquatorialRadiusM + altitudeM, 0, inclinationDeg, 0, 0, 1.0, 1_000_000);

    private void Show(MoonTransferPlan plan)
    {
        foreach (var b in plan.Burns)
            _output.WriteLine($"{b.Title}: {b.DeltaV:0.00} m/s (pro {b.Burn.Prograde:0.00}, nrm {b.Burn.Normal:0.00}, rad {b.Burn.Radial:0.00}) at UT {b.Ut:0}");
        _output.WriteLine($"SOI at {plan.Arrival.CrossingUt:0}, periapsis {plan.Arrival.PeriapsisAltitudeM / 1000:0.000} km at {plan.Arrival.PeriapsisUt:0}, " +
                          $"inclination {plan.Arrival.Path.InclinationDeg:0.0}°, prograde {plan.Arrival.Prograde}");
        foreach (var w in plan.Warnings) _output.WriteLine("! " + w);
    }

    [Fact]
    public void A_hyperbola_round_trips_and_its_periapsis_is_where_the_path_turns()
    {
        var mu = _mun.Mu;
        var r = _mun.SoiRadiusM * 0.9;
        var state = new StateVector(new Vector3d(r, 30_000, 0), new Vector3d(-800, 120, 15)); // inbound, faster than escape
        var orbit = Kepler.ElementsFrom(state, mu, 5_000);
        Assert.True(orbit.Eccentricity > 1);
        Assert.True(orbit.SemiMajorAxisM < 0);
        Assert.True(orbit.MeanAnomalyAtEpochRad < 0); // still coming in

        var back = Kepler.StateAt(orbit, mu, 5_000);
        Assert.True((back.Position - state.Position).Length < 1e-3);
        Assert.True((back.Velocity - state.Velocity).Length < 1e-6);

        var periapsisUt = Kepler.NextPeriapsisUt(orbit, mu, 5_000)!.Value;
        var at = Kepler.StateAt(orbit, mu, periapsisUt);
        Assert.Equal(Kepler.PeriapsisRadius(orbit), at.Position.Length, 1e-3);
        Assert.True(Math.Abs(Vector3d.Dot(at.Position.Normalized, at.Velocity.Normalized)) < 1e-9);
        Assert.Null(Kepler.NextPeriapsisUt(orbit, mu, periapsisUt + 1));

        // Energy holds along the path.
        var later = Kepler.StateAt(orbit, mu, periapsisUt + 2_000);
        double Energy(StateVector s) => s.Velocity.Length * s.Velocity.Length / 2 - mu / s.Position.Length;
        Assert.Equal(Energy(state), Energy(later), 1e-6);
    }

    [Fact]
    public void From_low_kerbin_orbit_to_the_mun_passes_at_the_asked_height_and_captures()
    {
        var craft = Circular(100_000);
        var plan = MoonTransfer.Plan(_kerbin, _mun, craft, 1_000_120, 30_000);
        Show(plan);

        Assert.False(plan.IsCorrection);
        var transfer = plan.Transfer!;
        Assert.InRange(transfer.Ut, 1_000_120, 1_000_120 + 1.1 * Kepler.OrbitalPeriod(craft, _kerbin.Mu) * 1.05 + 3600);
        Assert.InRange(transfer.DeltaV, 820, 880); // Hohmann from 100 km out to the Mun's distance: 842 m/s
        Assert.True(transfer.Burn.Prograde > 0);
        Assert.Equal(30_000, plan.Arrival.PeriapsisAltitudeM, 50.0);
        Assert.True(plan.Arrival.Prograde);
        Assert.True(plan.Arrival.Path.InclinationDeg < 90);

        // Flying the transfer exactly arrives as planned.
        var flown = Rendezvous.OrbitAfter(transfer.Ut, transfer.Burn, craft, _kerbin.Mu);
        var arrival = MoonTransfer.EnterSoi(_kerbin, _mun, flown, transfer.Ut, transfer.Ut + 10 * 86_400)!;
        Assert.Equal(plan.Arrival.CrossingUt, arrival.CrossingUt, 1.0);

        // The capture burn leaves a circular orbit at the periapsis.
        var capture = plan.Capture!;
        Assert.Equal(plan.Arrival.PeriapsisUt, capture.Ut);
        Assert.InRange(capture.Burn.Prograde, -400, -150);
        var orbit = Rendezvous.OrbitAfter(capture.Ut, capture.Burn, arrival.Path, _mun.Mu);
        Assert.True(orbit.Eccentricity < 1e-3);
        Assert.Equal(_mun.EquatorialRadiusM + 30_000, orbit.SemiMajorAxisM, 100.0);
    }

    [Fact]
    public void Minmus_waits_for_its_tilted_orbit_to_cross_yours_unless_asked_to_leave_now()
    {
        var plan = MoonTransfer.Plan(_kerbin, _minmus, Circular(80_000), 1_000_120, 20_000);
        Show(plan);
        Assert.Equal(20_000, plan.Arrival.PeriapsisAltitudeM, 50.0);
        Assert.True(plan.Arrival.Prograde);
        Assert.InRange(plan.Transfer!.DeltaV, 900, 950); // Hohmann from 80 km out to Minmus's distance: 921 m/s
        Assert.True(Math.Abs(plan.Transfer.Burn.Normal) < 20);
        Assert.Contains(plan.Warnings, w => w.StartsWith("Waits"));

        var now = MoonTransfer.Plan(_kerbin, _minmus, Circular(80_000), 1_000_120, 20_000, soonest: true);
        Show(now);
        Assert.Equal(20_000, now.Arrival.PeriapsisAltitudeM, 50.0);
        Assert.True(now.Transfer!.Ut < plan.Transfer.Ut);
        Assert.True(now.Transfer.DeltaV > plan.Transfer.DeltaV);
        Assert.DoesNotContain(now.Warnings, w => w.StartsWith("Waits"));
    }

    [Fact]
    public void A_sloppy_transfer_burn_gets_a_small_course_correction()
    {
        var craft = Circular(100_000);
        var plan = MoonTransfer.Plan(_kerbin, _mun, craft, 1_000_120, 30_000);
        var transfer = plan.Transfer!;
        // Burned 3 m/s too much.
        var flown = Rendezvous.OrbitAfter(transfer.Ut, transfer.Burn with { Prograde = transfer.Burn.Prograde + 3 }, craft, _kerbin.Mu);
        var off = MoonTransfer.EnterSoi(_kerbin, _mun, flown, transfer.Ut, transfer.Ut + 10 * 86_400);
        _output.WriteLine($"Off course: periapsis {off?.PeriapsisAltitudeM / 1000:0.0} km, prograde {off?.Prograde}");

        var fix = MoonTransfer.Plan(_kerbin, _mun, flown, transfer.Ut + 3_600, 30_000);
        Show(fix);
        Assert.True(fix.IsCorrection);
        Assert.InRange(fix.Transfer!.DeltaV, 0.01, 15);
        Assert.Equal(30_000, fix.Arrival.PeriapsisAltitudeM, 50.0);
        Assert.True(fix.Arrival.Prograde);
    }

    [Fact]
    public void On_course_there_is_only_the_capture()
    {
        var craft = Circular(100_000);
        var transfer = MoonTransfer.Plan(_kerbin, _mun, craft, 1_000_120, 30_000).Transfer!;
        var flown = Rendezvous.OrbitAfter(transfer.Ut, transfer.Burn, craft, _kerbin.Mu);
        var plan = MoonTransfer.Plan(_kerbin, _mun, flown, transfer.Ut + 3_600, 30_000);
        Assert.Null(plan.Transfer);
        Assert.Single(plan.Burns);
    }

    [Fact]
    public void Inside_the_moons_sphere_circularize_at_periapsis_captures()
    {
        var craft = Circular(100_000);
        var plan = MoonTransfer.Plan(_kerbin, _mun, craft, 1_000_120, 30_000);
        var flyby = plan.Arrival.Path;
        var now = plan.Arrival.CrossingUt + 600;

        var burn = QuickBurns.Circularize(_mun, flyby, now, 120, atApoapsis: false);
        _output.WriteLine($"{burn.Title}: {burn.DeltaV:0.00} m/s at UT {burn.Ut:0}");
        Assert.Equal(plan.Arrival.PeriapsisUt, burn.Ut, 1e-3);
        Assert.Equal(plan.Capture!.Burn.Prograde, burn.Burn.Prograde, 1e-6);
        Assert.True(Rendezvous.OrbitAfter(burn.Ut, burn.Burn, flyby, _mun.Mu).Eccentricity < 1e-3);

        // Capturing into an ellipse is cheaper.
        var ellipse = QuickBurns.SetApsis(_mun, flyby, now, 120, 500_000, apoapsis: true);
        Assert.StartsWith("Capture", ellipse.Title);
        Assert.True(Math.Abs(ellipse.Burn.Prograde) < Math.Abs(burn.Burn.Prograde));
        var after = Rendezvous.OrbitAfter(ellipse.Ut, ellipse.Burn, flyby, _mun.Mu);
        Assert.Equal(_mun.EquatorialRadiusM + 500_000, after.SemiMajorAxisM * (1 + after.Eccentricity), 100.0);

        Assert.Throws<ArgumentException>(() => QuickBurns.Circularize(_mun, flyby, now, 120, atApoapsis: true));
        Assert.Throws<ArgumentException>(() => QuickBurns.Circularize(_mun, flyby, plan.Arrival.PeriapsisUt + 60, 120, atApoapsis: false));
    }

    /// <summary>A circular equatorial orbit around the Mun at <paramref name="altitudeM"/>, stated at UT 1,000,000.</summary>
    private OrbitalElements MunOrbit(double altitudeM) => new(_mun.EquatorialRadiusM + altitudeM, 0, 0, 0, 0, 0.3, 1_000_000);

    [Fact]
    public void From_the_mun_back_to_kerbin_comes_down_to_the_asked_height_and_captures()
    {
        var craft = MunOrbit(30_000);
        var plan = MoonTransfer.PlanReturn(_mun, _kerbin, craft, 1_000_120, 80_000);
        Show(plan);

        var escape = plan.Transfer!;
        Assert.False(plan.IsCorrection);
        Assert.InRange(escape.Ut, 1_000_120, 1_000_120 + Kepler.OrbitalPeriod(craft, _mun.Mu) + 1);
        Assert.InRange(escape.DeltaV, 240, 300); // 304 m/s with an infinite SOI (docs/MATH.md §3.7); less with the real one
        Assert.True(escape.Burn.Prograde > 0);
        Assert.Equal(80_000, plan.Arrival.PeriapsisAltitudeM, 50.0);
        Assert.True(plan.Arrival.Prograde);

        // Flying the burn exactly leaves the Mun as planned, onto that path round Kerbin.
        var flown = Rendezvous.OrbitAfter(escape.Ut, escape.Burn, craft, _mun.Mu);
        var left = MoonTransfer.LeaveSoi(_mun, _kerbin, flown, escape.Ut, escape.Ut + 10 * 86_400)!;
        Assert.Equal(plan.Arrival.CrossingUt, left.CrossingUt, 1.0);
        var exit = Kepler.StateAt(left.Path, _kerbin.Mu, left.CrossingUt);
        var mun = Kepler.StateAt(_mun.Orbit!, _kerbin.Mu, left.CrossingUt);
        _output.WriteLine($"Leaves the Mun's SOI {exit.Position.Length / 1000:#,0} km from Kerbin (the Mun is at {mun.Position.Length / 1000:#,0} km), " +
                          $"moving {exit.Velocity.Length:0.0} m/s, {Vector3d.Dot(exit.Velocity, exit.Position.Normalized):0.0} m/s of it outward; " +
                          $"angular momentum needs {Vector3d.Cross(exit.Position, exit.Velocity).Length / exit.Position.Length:0.0} m/s sideways there");

        var capture = plan.Capture!;
        Assert.InRange(capture.Burn.Prograde, -950, -750);
        var orbit = Rendezvous.OrbitAfter(capture.Ut, capture.Burn, left.Path, _kerbin.Mu);
        Assert.True(orbit.Eccentricity < 1e-3);
        Assert.Equal(_kerbin.EquatorialRadiusM + 80_000, orbit.SemiMajorAxisM, 100.0);
    }

    [Fact]
    public void Coming_home_through_the_atmosphere_has_no_capture_burn()
    {
        var plan = MoonTransfer.PlanReturn(_mun, _kerbin, MunOrbit(30_000), 1_000_120, 35_000);
        Show(plan);
        Assert.Null(plan.Capture);
        Assert.Single(plan.Burns);
        Assert.Equal(35_000, plan.Arrival.PeriapsisAltitudeM, 50.0);
        Assert.Contains(plan.Warnings, w => w.Contains("atmosphere"));
    }

    [Fact]
    public void A_sloppy_escape_burn_gets_a_small_course_correction()
    {
        var craft = MunOrbit(30_000);
        var escape = MoonTransfer.PlanReturn(_mun, _kerbin, craft, 1_000_120, 80_000).Transfer!;
        var flown = Rendezvous.OrbitAfter(escape.Ut, escape.Burn with { Prograde = escape.Burn.Prograde + 5 }, craft, _mun.Mu);
        var off = MoonTransfer.LeaveSoi(_mun, _kerbin, flown, escape.Ut, escape.Ut + 10 * 86_400);
        _output.WriteLine($"Off course: periapsis {off?.PeriapsisAltitudeM / 1000:0.0} km over Kerbin");

        var fix = MoonTransfer.PlanReturn(_mun, _kerbin, flown, escape.Ut + 1_800, 80_000);
        Show(fix);
        Assert.True(fix.IsCorrection);
        Assert.InRange(fix.Transfer!.DeltaV, 0.01, 15);
        Assert.Equal(80_000, fix.Arrival.PeriapsisAltitudeM, 50.0);
    }

    [Fact]
    public void Minmus_back_to_kerbin_works_too()
    {
        var craft = new OrbitalElements(_minmus.EquatorialRadiusM + 20_000, 0, 0, 0, 0, 0.3, 1_000_000);
        var plan = MoonTransfer.PlanReturn(_minmus, _kerbin, craft, 1_000_120, 80_000);
        Show(plan);
        Assert.Equal(80_000, plan.Arrival.PeriapsisAltitudeM, 50.0);
        Assert.InRange(plan.Transfer!.DeltaV, 100, 250);
    }
}
