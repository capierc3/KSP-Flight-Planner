using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.Tests;

/// <summary>Landing at a spot from the craft's real orbit (docs/MATH.md §2.9).</summary>
public sealed class LandingFlightsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly PlannerDatabase _db;
    private readonly Dictionary<string, CelestialBody> _bodies;

    public LandingFlightsTests()
    {
        _db = new PlannerDatabase(_path);
        _db.Initialize();
        var bodies = new BodyRepository(_db);
        _bodies = bodies.GetBodies(bodies.GetSystems().Single(s => s.Name == "KSP Stock").Id).ToDictionary(b => b.Name);
    }

    public void Dispose()
    {
        TestDb.Release(_path);
        File.Delete(_path);
    }

    private static readonly LandingTarget KerbolArch = new("Kerbol arch", "Landmark", 12.4508, 39.1959, 2_000);
    private const double Now = 5_000_000;
    private const double Theta0 = 37;

    private OrbitalElements Circular(string body, double altitudeM, double inclinationDeg, double lanDeg = 20) =>
        new(_bodies[body].EquatorialRadiusM + altitudeM, 0, inclinationDeg, lanDeg, 0, 1.0, Now);

    /// <summary>The descent orbit: every burn but the braking flown.</summary>
    private OrbitalElements Descent(CelestialBody body, OrbitalElements start, LandingOption option) =>
        option.Burns.Where(b => !b.Title.StartsWith("Brake", StringComparison.Ordinal))
            .Aggregate(start, (o, b) => Rendezvous.OrbitAfter(b.Ut, b.Burn, o, body.Mu));

    [Fact]
    public void Mun_landing_comes_down_on_the_spot()
    {
        var mun = _bodies["Mun"];
        var leg = new LandingLeg(KerbolArch, LanderTwr: 5 / mun.SurfaceGravity);
        var start = Circular("Mun", 30_000, 15);
        var options = LandingFlights.Options(mun, leg, start, Now, 120, Theta0);
        var best = options[0];
        Assert.Equal("Least Δv", best.Label);

        // Deorbit and braking as in §2.6's worked example (same orbit and lander).
        var deorbit = best.Burns.Single(b => b.Title.StartsWith("Deorbit", StringComparison.Ordinal));
        var brake = best.Burns.Single(b => b.Title.StartsWith("Brake", StringComparison.Ordinal));
        Assert.Equal(16.86, deorbit.DeltaV, 1);
        Assert.InRange(brake.DeltaV, 590, 625);
        Assert.True(deorbit.Ut >= Now + 120);
        Assert.True(best.LandingUt > deorbit.Ut);

        // The descent's plane passes over the spot when it lands there…
        var descent = Descent(mun, start, best);
        var site = LandingFlights.SiteDirection(mun, KerbolArch, Theta0, best.LandingUt);
        var state = Kepler.StateAt(descent, mun.Mu, best.LandingUt);
        var normal = Vector3d.Cross(state.Position, state.Velocity).Normalized;
        Assert.True(Math.Abs(Vector3d.Dot(normal, site)) * mun.EquatorialRadiusM < 100);

        // …and its low point is a braking distance short of it, at 1 km over the ground.
        var lowUt = deorbit.Ut + Kepler.OrbitalPeriod(descent, mun.Mu) / 2;
        var low = Kepler.StateAt(descent, mun.Mu, lowUt).Position;
        Assert.InRange(low.Length - mun.EquatorialRadiusM, 2_990, 3_010);
        var gap = Vector3d.Angle(low, site) * (mun.EquatorialRadiusM + 2_000);
        Assert.InRange(gap, 30_000, 40_000);
    }

    [Fact]
    public void Sooner_option_lands_first_and_costs_more()
    {
        var mun = _bodies["Mun"];
        // From an equatorial orbit (tilted first), a pass that needs a bigger plane fix comes hours earlier.
        var options = LandingFlights.Options(mun, new LandingLeg(KerbolArch), Circular("Mun", 30_000, 0, 0), Now, 120, Theta0);
        Assert.Equal(2, options.Count);
        Assert.Equal("Sooner", options[1].Label);
        Assert.True(options[1].LandingUt < options[0].LandingUt);
        Assert.True(options[1].DeltaV >= options[0].DeltaV);
    }

    [Fact]
    public void An_equatorial_orbit_is_tilted_first_to_reach_the_latitude()
    {
        var mun = _bodies["Mun"];
        var spot = new LandingTarget("North", "Entered", 40, 10, 0);
        var start = Circular("Mun", 30_000, 0, 0);
        var option = LandingFlights.Options(mun, new LandingLeg(spot), start, Now, 120, Theta0)[0];
        var tilt = option.Burns[0];
        Assert.StartsWith("Tilt the orbit to 41", tilt.Title);
        var after = Rendezvous.OrbitAfter(tilt.Ut, tilt.Burn, start, mun.Mu);
        Assert.Equal(41, after.InclinationDeg, 1);
        // 2·v·sin(i/2) at 30 km (§2.3).
        Assert.Equal(2 * OrbitMath.CircularVelocity(mun.Mu, start.SemiMajorAxisM) * Math.Sin(41 * Math.PI / 360), tilt.DeltaV, 0);
        Assert.Contains(option.Warnings, w => w.Contains("doesn't reach"));
    }

    [Fact]
    public void Bodies_with_air_get_no_braking_burn()
    {
        var duna = _bodies["Duna"];
        var spot = new LandingTarget("Plain", "Entered", 5, 30, 0);
        var option = LandingFlights.Options(duna, new LandingLeg(spot), Circular("Duna", 60_000, 10), Now, 120, Theta0)[0];
        Assert.DoesNotContain(option.Burns, b => b.Title.StartsWith("Brake", StringComparison.Ordinal));
        Assert.Contains(option.Warnings, w => w.Contains("rough estimate"));
    }

    [Fact]
    public void An_escaping_path_is_refused()
    {
        var mun = _bodies["Mun"];
        var path = new OrbitalElements(-500_000, 1.5, 10, 0, 0, -0.5, Now);
        Assert.Throws<ArgumentException>(() => LandingFlights.Options(mun, new LandingLeg(KerbolArch), path, Now, 120, Theta0));
    }

    [Fact]
    public void A_landing_flight_plans_from_the_orbit()
    {
        var mun = _bodies["Mun"];
        var flight = new Flight { Name = "Land at Kerbol arch", Kind = FlightKind.Landing, BodyName = "Mun", Landing = new LandingLeg(KerbolArch) };
        var planned = LandingFlights.Plan(flight, mun, Circular("Mun", 30_000, 15), Now, Theta0);
        Assert.NotEmpty(planned.Burns);
        Assert.Equal(Now, planned.PlannedUt);
        Assert.NotNull(planned.StartOrbit);
    }
}
