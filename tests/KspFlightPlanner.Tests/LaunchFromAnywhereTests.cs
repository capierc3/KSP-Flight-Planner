using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;
using Xunit.Abstractions;

namespace KspFlightPlanner.Tests;

/// <summary>Lifting off to meet a craft from anywhere: the KSC to Kerb Station, a lander on the Mun to Mun Station (§4.5).</summary>
public sealed class LaunchFromAnywhereTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly IReadOnlyList<CelestialBody> _bodies;
    private const double Now = 1_000_000;

    public LaunchFromAnywhereTests(ITestOutputHelper output)
    {
        _output = output;
        var db = new PlannerDatabase(_path);
        db.Initialize();
        var repo = new BodyRepository(db);
        _bodies = repo.GetBodies(repo.GetSystems()[0].Id);
    }

    public void Dispose()
    {
        TestDb.Release(_path);
        File.Delete(_path);
    }

    private CelestialBody Body(string name) => _bodies.Single(b => b.Name == name);

    private OrbitalElements Orbit(string body, double altitudeM, double inclinationDeg, double lanDeg) =>
        new(Body(body).EquatorialRadiusM + altitudeM, 0, inclinationDeg, lanDeg, 0, 0.3, Now);

    private void Show(RendezvousPlan plan)
    {
        var l = plan.Launch!;
        _output.WriteLine($"{l.Kind}: lift off +{(l.LiftoffUt - Now) / 3600:0.0} h, heading {l.HeadingDeg:0.0}°, parking {l.ParkingOrbit.InclinationDeg:0.0}°");
        foreach (var m in plan.Legs[0].Maneuvers) _output.WriteLine($"  {m.Kind}: {m.DeltaV:0.0} m/s at +{(m.Ut - Now) / 3600:0.0} h");
        _output.WriteLine($"  orbital {plan.OrbitalDv:0} m/s");
    }

    [Fact]
    public void A_climb_from_the_mun_starts_from_its_low_orbit_in_two_minutes()
    {
        Assert.Equal(new AscentProfile(80_000), AscentProfile.Typical(Body("Kerbin")));

        // Mun: 10 km parking (its peaks are 7 km); v = √(6.514×10¹⁰ / 210,000) = 557 m/s, so v / 6.33 = 88 s: two minutes at least.
        var mun = AscentProfile.Typical(Body("Mun"));
        Assert.Equal(10_000, mun.ParkingAltitudeM);
        Assert.Equal(120, mun.AscentTimeS);
        Assert.Equal(0.38 * 557.0 * 120 / 210_000 * 180 / Math.PI, mun.DownrangeDeg, 0.1); // about 6.9°
    }

    [Fact]
    public void A_lander_on_the_mun_lifts_off_into_mun_stations_plane_and_meets_it()
    {
        var site = new LaunchSite("Grand Lander", "Mun", 3.2, -40.5, 0);
        var station = new RendezvousTarget("Mun Station", Orbit("Mun", 53_000, 3, 120));
        var plan = Rendezvous.Plan(new RendezvousRequest(Body("Mun"), [station], Now, 0, Site: site, Ascent: AscentProfile.Typical(Body("Mun"))));
        Show(plan);

        Assert.NotNull(plan.Launch);
        Assert.Equal("Grand Lander", site.Name);
        // Straight into the station's plane (or due east with the plane fixed in orbit): little left to match.
        var planes = plan.Legs[0].Maneuvers.Where(m => m.Kind == ManeuverKind.PlaneChange).Sum(m => m.DeltaV);
        Assert.True(planes < 30, $"{planes:0} m/s of plane change");
        // From 10 km up to just under 53 km: a small Hohmann, tens of m/s.
        Assert.True(plan.OrbitalDv < 150, $"{plan.OrbitalDv:0} m/s");
        Assert.All(plan.LaunchOptions, o => Assert.True(o.LiftoffUt >= Now));
    }

    [Fact]
    public void The_ksc_still_launches_to_a_station_round_kerbin()
    {
        var station = new RendezvousTarget("Kerb Station", Orbit("Kerbin", 100_000, 1, 30));
        var plan = Rendezvous.Plan(new RendezvousRequest(Body("Kerbin"), [station], Now, 0, Site: LaunchSite.Ksc, Ascent: AscentProfile.Typical(Body("Kerbin"))));
        Show(plan);
        Assert.True(plan.OrbitalDv < 150, $"{plan.OrbitalDv:0} m/s");
    }
}
