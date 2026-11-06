using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.Tests;

/// <summary>Planning a background trip again from where the ship was found off course (§11.3).</summary>
public sealed class HaulerReplanTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly IReadOnlyList<CelestialBody> _bodies;
    private const double Now = 1_000_000;
    private const double Lead = 120;

    public HaulerReplanTests()
    {
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

    private OrbitalElements Round(string body, double altitudeM, double inclinationDeg = 0, double meanAnomaly = 0.3) =>
        new(Body(body).EquatorialRadiusM + altitudeM, 0, inclinationDeg, 0, 0, meanAnomaly, Now);

    private RendezvousTarget Station => new("Kerb Station", Round("Kerbin", 330_000, 1, 2.0));

    private HaulerTrip? Plan(string shipBody, OrbitalElements ship, out string? wait) =>
        HaulerReplan.Plan(Body(shipBody), ship, "Kerb Shuttle mk2", Body("Kerbin"), Station, _bodies, Now, Lead, out wait);

    [Fact]
    public void Knocked_off_its_phasing_orbit_round_kerbin_it_gets_a_fresh_meeting()
    {
        var trip = Plan("Kerbin", Round("Kerbin", 200_000, 0.5), out var wait);

        Assert.Null(wait);
        Assert.NotNull(trip);
        Assert.NotEmpty(trip!.Burns);
        Assert.True(trip.Burns[0].Ut >= Now + Lead - 1);
        Assert.True(trip.MeetUt >= trip.Burns[^1].Ut - 1);
        Assert.All(trip.Burns.Zip(trip.Burns.Skip(1)), p => Assert.True(p.Second.Ut >= p.First.Ut));
    }

    [Fact]
    public void Still_round_the_mun_it_plans_the_trip_to_kerbin_then_the_meeting()
    {
        var trip = Plan("Mun", Round("Mun", 30_000), out var wait);

        Assert.Null(wait);
        Assert.NotNull(trip);
        Assert.True(trip!.Burns.Count >= 3, string.Join(", ", trip.Burns.Select(b => b.Title)));
        Assert.Contains(trip.Burns, b => b.Title.Contains("Kerb Station"));
        Assert.True(trip.Burns.Sum(b => b.DeltaV) < 2_000, $"{trip.Burns.Sum(b => b.DeltaV):0} m/s");
    }

    [Fact]
    public void Leaving_the_mun_it_waits_until_it_is_out()
    {
        // A hyperbola round the Mun: 1.2 eccentricity, periapsis 30 km.
        var mun = Body("Mun");
        var rp = mun.EquatorialRadiusM + 30_000;
        var escaping = new OrbitalElements(-rp / 0.2, 1.2, 0, 0, 0, 0.1, Now);

        var trip = Plan("Mun", escaping, out var wait);

        Assert.Null(trip);
        Assert.Contains("Mun", wait);
    }

    [Fact]
    public void Coming_in_on_an_escape_path_round_kerbin_it_is_captured_first()
    {
        var kerbin = Body("Kerbin");
        var rp = kerbin.EquatorialRadiusM + 150_000;
        var incoming = new OrbitalElements(-rp / 0.1, 1.1, 0.5, 0, 0, -0.5, Now);

        var trip = Plan("Kerbin", incoming, out var wait);

        Assert.Null(wait);
        Assert.StartsWith("Capture", trip!.Burns[0].Title);
        Assert.True(trip.Burns.Count >= 2);
    }

    [Fact]
    public void A_pass_through_the_air_cannot_be_planned_from()
    {
        var kerbin = Body("Kerbin");
        var rp = kerbin.EquatorialRadiusM + 40_000;
        var diving = new OrbitalElements(-rp / 0.1, 1.1, 0, 0, 0, -0.5, Now);

        Assert.Throws<ArgumentException>(() => Plan("Kerbin", diving, out _));
    }
}
