using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;
using Xunit.Abstractions;

namespace KspFlightPlanner.Tests;

public sealed class QuickBurnsTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly CelestialBody _kerbin;

    public QuickBurnsTests(ITestOutputHelper output)
    {
        _output = output;
        var db = new PlannerDatabase(_path);
        db.Initialize();
        var repo = new BodyRepository(db);
        _kerbin = repo.GetBodies(repo.GetSystems()[0].Id).Single(b => b.Name == "Kerbin");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private double R => _kerbin.EquatorialRadiusM;

    /// <summary>An orbit with the given periapsis and apoapsis altitudes, at periapsis at its epoch (UT 1000).</summary>
    private OrbitalElements Ellipse(double peM, double apM, double inclinationDeg = 0)
    {
        double rp = R + peM, ra = R + apM;
        return new OrbitalElements((rp + ra) / 2, (ra - rp) / (ra + rp), inclinationDeg, 30, 40, 0, 1000);
    }

    private OrbitalElements After(FlightBurn burn, OrbitalElements before) => Rendezvous.OrbitAfter(burn.Ut, burn.Burn, before, _kerbin.Mu);

    private (double Pe, double Ap) Apsides(OrbitalElements o) =>
        (o.SemiMajorAxisM * (1 - o.Eccentricity) - R, o.SemiMajorAxisM * (1 + o.Eccentricity) - R);

    [Fact]
    public void Circularize_at_apoapsis_leaves_a_circular_orbit_there()
    {
        var orbit = Ellipse(80_000, 250_000);
        var burn = QuickBurns.Circularize(_kerbin, orbit, 1000, 120, atApoapsis: true);
        var after = After(burn, orbit);
        _output.WriteLine($"{burn.Title}: {burn.DeltaV:0.00} m/s at UT {burn.Ut:0}");

        Assert.True(burn.Ut > 1000);
        Assert.True(burn.Burn.Prograde > 0);
        Assert.True(after.Eccentricity < 1e-6);
        Assert.Equal(R + 250_000, after.SemiMajorAxisM, 1);
        Assert.Equal(0, burn.Burn.Normal);
        Assert.Equal(0, burn.Burn.Radial);
    }

    [Fact]
    public void Circularize_at_periapsis_slows_down()
    {
        var orbit = Ellipse(100_000, 2_000_000);
        var burn = QuickBurns.Circularize(_kerbin, orbit, 1500, 120, atApoapsis: false);
        var after = After(burn, orbit);
        Assert.True(burn.Burn.Prograde < 0);
        Assert.Equal(R + 100_000, after.SemiMajorAxisM, 1);
        Assert.True(after.Eccentricity < 1e-6);
    }

    [Fact]
    public void Raising_the_apoapsis_from_low_orbit_is_the_first_hohmann_burn()
    {
        var orbit = Ellipse(80_000, 80_000);
        var burn = QuickBurns.SetApsis(_kerbin, orbit, 1080, 120, 250_000, apoapsis: true);
        var hohmann = OrbitMath.Hohmann(_kerbin.Mu, R + 80_000, R + 250_000);
        var (pe, ap) = Apsides(After(burn, orbit));
        _output.WriteLine($"{burn.Title}: {burn.DeltaV:0.00} m/s → {pe:0} × {ap:0}");

        Assert.Equal(1200, burn.Ut); // circular: the lead from now
        Assert.Equal("Raise apoapsis to 250 km", burn.Title);
        Assert.Equal(hohmann.Dv1, burn.DeltaV, 1e-6);
        Assert.Equal(250_000, ap, 1);
        Assert.Equal(80_000, pe, 1);
    }

    [Fact]
    public void Lowering_the_periapsis_burns_at_apoapsis_and_keeps_it()
    {
        var orbit = Ellipse(90_000, 400_000, inclinationDeg: 20);
        var burn = QuickBurns.SetApsis(_kerbin, orbit, 1000, 120, 30_000, apoapsis: false);
        var after = After(burn, orbit);
        var (pe, ap) = Apsides(after);

        Assert.Equal(QuickBurns.ApsisUt(orbit, _kerbin.Mu, 1000, 120, atApoapsis: true), burn.Ut);
        Assert.StartsWith("Lower periapsis", burn.Title);
        Assert.True(burn.Burn.Prograde < 0);
        Assert.Equal(30_000, pe, 1);
        Assert.Equal(400_000, ap, 1);
        Assert.Equal(20, after.InclinationDeg, 6);
    }

    [Fact]
    public void An_apoapsis_below_the_periapsis_is_refused()
    {
        var orbit = Ellipse(100_000, 300_000);
        var ex = Assert.Throws<ArgumentException>(() => QuickBurns.SetApsis(_kerbin, orbit, 1000, 120, 90_000, apoapsis: true));
        Assert.Contains("set the periapsis instead", ex.Message);
        Assert.Throws<ArgumentException>(() => QuickBurns.SetApsis(_kerbin, orbit, 1000, 120, 350_000, apoapsis: false));
    }

    private double ApoapsisUt(OrbitalElements o) => o.EpochS + Kepler.OrbitalPeriod(o, _kerbin.Mu) / 2;

    [Fact]
    public void After_launch_a_close_apoapsis_is_used_not_the_next_orbits()
    {
        // Coasting up to an 80 km apoapsis a minute away, periapsis deep underground.
        var orbit = Ellipse(-300_000, 80_000);
        var now = ApoapsisUt(orbit) - 60;
        var burn = QuickBurns.Circularize(_kerbin, orbit, now, 120, atApoapsis: true);
        var (pe, ap) = Apsides(After(burn, orbit));
        _output.WriteLine($"{burn.Title}: {burn.DeltaV:0.0} m/s in {burn.Ut - now:0} s → {pe:0} × {ap:0}");

        Assert.Equal(ApoapsisUt(orbit), burn.Ut, 3);
        Assert.Equal(80_000, pe, 1);
        Assert.Equal(80_000, ap, 1);
    }

    [Fact]
    public void Just_past_the_apoapsis_and_falling_it_circularizes_now()
    {
        var orbit = Ellipse(-300_000, 80_000);
        var now = ApoapsisUt(orbit) + 10;
        var burn = QuickBurns.Circularize(_kerbin, orbit, now, 120, atApoapsis: true);
        var (pe, ap) = Apsides(After(burn, orbit));
        _output.WriteLine($"{burn.Title}: {burn.DeltaV:0.0} m/s ({burn.Burn}) → {pe:0} × {ap:0}");

        Assert.StartsWith("Circularize now", burn.Title);
        Assert.Equal(now + QuickBurns.FallingLeadS, burn.Ut);
        Assert.True(burn.Burn.Radial > 0); // cancels the fall
        Assert.Equal(pe, ap, 1);
        Assert.InRange(pe, 75_000, 80_000); // a little lower: it has fallen for 40 s
    }

    [Fact]
    public void A_released_satellite_circularizes_on_this_pass()
    {
        // Just released from a carrier, 40 s before its periapsis (the shared apsis): same pass, not one orbit later.
        var orbit = Ellipse(2_863_334, 4_000_000);
        var now = orbit.EpochS + Kepler.OrbitalPeriod(orbit, _kerbin.Mu) - 40;
        var burn = QuickBurns.Circularize(_kerbin, orbit, now, 120, atApoapsis: false);
        Assert.Equal(now + 40, burn.Ut, 3);
    }

    [Fact]
    public void Circularizing_at_a_periapsis_in_the_atmosphere_is_refused()
    {
        var orbit = Ellipse(20_000, 300_000);
        var ex = Assert.Throws<ArgumentException>(() => QuickBurns.Circularize(_kerbin, orbit, 1000, 120, atApoapsis: false));
        Assert.Contains("circularize at the apoapsis", ex.Message);
    }

    [Fact]
    public void Raising_the_periapsis_while_falling_is_refused()
    {
        var orbit = Ellipse(-300_000, 80_000);
        var ex = Assert.Throws<ArgumentException>(() =>
            QuickBurns.SetApsis(_kerbin, orbit, ApoapsisUt(orbit) + 10, 120, 70_000, apoapsis: false));
        Assert.Contains("circularize now", ex.Message);
    }
}
