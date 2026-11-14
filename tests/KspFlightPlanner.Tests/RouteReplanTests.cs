using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;
using Xunit.Abstractions;

namespace KspFlightPlanner.Tests;

/// <summary>Planning a background trip again from where the ship was found off course (§11.3).</summary>
public sealed class RouteReplanTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly IReadOnlyList<CelestialBody> _bodies;
    private const double Now = 1_000_000;
    private const double Lead = 120;

    public RouteReplanTests(ITestOutputHelper output)
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

    private OrbitalElements Round(string body, double altitudeM, double inclinationDeg = 0, double meanAnomaly = 0.3) =>
        new(Body(body).EquatorialRadiusM + altitudeM, 0, inclinationDeg, 0, 0, meanAnomaly, Now);

    private RendezvousTarget Station => new("Kerb Station", Round("Kerbin", 330_000, 1, 2.0));

    private RouteTrip? Plan(string shipBody, OrbitalElements ship, out string? wait) =>
        RouteReplan.Plan(Body(shipBody), ship, "Kerb Shuttle mk2", Body("Kerbin"), Station, _bodies, Now, Lead, out wait);

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
    public void Arriving_from_the_mun_on_a_stretched_orbit_it_is_captured_first_and_meets_gently()
    {
        // The in-game crash (2026-11-05): replanned on the long orbit back from the Mun, the meeting was an intercept with
        // an 845 m/s match at the station. Periapsis near the station's height, apoapsis out at the Mun.
        var kerbin = Body("Kerbin");
        var rp = kerbin.EquatorialRadiusM + 320_000;
        var ra = 11_400_000.0;
        var stretched = new OrbitalElements((rp + ra) / 2, (ra - rp) / (ra + rp), 0.5, 0, 0, 3.0, Now);

        var trip = Plan("Kerbin", stretched, out var wait);

        Assert.Null(wait);
        Assert.StartsWith("Capture", trip!.Burns[0].Title);
        _output.WriteLine(string.Join("\n", trip.Burns.Select(b => $"{b.Title}: {b.DeltaV:0} m/s at +{(b.Ut - Now) / 60:0} min")));
        var match = trip.Burns[^1];
        Assert.True(match.DeltaV < 100, $"{match.Title}: {match.DeltaV:0} m/s at the station");
    }

    [Fact]
    public void Back_from_the_mun_out_of_the_stations_plane_the_capture_takes_the_plane_change_and_the_phasing_in_one_burn()
    {
        // As in the test of 2026-11-06: arriving 2° out of the station's plane, the replan circularized, then did
        // "Match planes and phase" (228 m/s) at the plane crossing, then the transfer: four burns.
        var kerbin = Body("Kerbin");
        var rp = kerbin.EquatorialRadiusM + 95_000;
        var ra = 11_400_000.0;
        var stretched = new OrbitalElements((rp + ra) / 2, (ra - rp) / (ra + rp), 3.0, 40, 10, 3.0, Now);
        var station = new RendezvousTarget("Kerb Station", new OrbitalElements(kerbin.EquatorialRadiusM + 100_000, 0, 1.0, 10, 0, 2.0, Now));

        var plan = ArrivalMeet.Plan(kerbin, stretched, "Kerb Shuttle mk2", station, Now, Lead);
        _output.WriteLine(string.Join("\n", plan.Burns.Select(b => $"{b.Title}: {b.DeltaV:0} m/s at +{(b.Ut - Now) / 60:0} min")));

        // The old way: a round capture in its own plane, then a rendezvous from there.
        var round = QuickBurns.Circularize(kerbin, stretched, Now, Lead, atApoapsis: false);
        var parked = Rendezvous.OrbitAfter(round.Ut, round.Burn, stretched, kerbin.Mu);
        var old = Rendezvous.Plan(new RendezvousRequest(kerbin, [station], round.Ut + Rendezvous.BurnGapS, 0, new RendezvousTarget("Kerb Shuttle mk2", parked)));
        var oldDv = round.DeltaV + old.OrbitalDv;
        _output.WriteLine($"new {plan.Burns.Sum(b => b.DeltaV):0} m/s in {plan.Burns.Count} burns; old {oldDv:0} m/s in {1 + RendezvousFlights.Burns(old).Count}");

        Assert.StartsWith("Capture", plan.Burns[0].Title);
        Assert.DoesNotContain(plan.Burns.Skip(1), b => b.Title.Contains("planes", StringComparison.OrdinalIgnoreCase));
        Assert.True(plan.Burns.Sum(b => b.DeltaV) < oldDv, $"{plan.Burns.Sum(b => b.DeltaV):0} vs {oldDv:0} m/s");
        Assert.True(plan.Burns[^1].DeltaV < 50, $"match {plan.Burns[^1].DeltaV:0} m/s");
    }

    [Fact]
    public void A_replan_keeps_the_way_picked_when_the_fuel_allows_it()
    {
        var (from, all) = TwoWays();
        var soonest = all.First(w => w.Way.Contains("soonest", StringComparison.OrdinalIgnoreCase));

        var trip = RouteReplan.Plan(Body("Mun"), from, "Kerb Shuttle mk2", Body("Kerbin"), Station, _bodies, Now, Lead, out _,
            way: "Meets soonest", deltaVLeft: 5_000);

        Assert.Equal(soonest.Way, trip!.Way);
        Assert.Null(trip.Note);
    }

    [Fact]
    public void Short_of_fuel_for_the_way_picked_it_takes_the_cheapest_and_says_why()
    {
        var (from, all) = TwoWays();
        var cheapest = all.MinBy(w => w.DeltaV)!;
        var dearest = all.MaxBy(w => w.DeltaV)!;
        var left = (RouteReplan.WithMargin(cheapest.DeltaV) + RouteReplan.WithMargin(dearest.DeltaV)) / 2;

        var trip = RouteReplan.Plan(Body("Mun"), from, "Kerb Shuttle mk2", Body("Kerbin"), Station, _bodies, Now, Lead, out _,
            way: dearest.Way, deltaVLeft: left);

        Assert.Equal(cheapest.Way, trip!.Way);
        Assert.Contains(dearest.Way, trip.Note);
    }

    [Fact]
    public void With_too_little_fuel_for_any_way_it_is_not_planned()
    {
        var ex = Assert.Throws<ArgumentException>(() => RouteReplan.Plan(Body("Mun"), Round("Mun", 30_000), "Kerb Shuttle mk2", Body("Kerbin"), Station,
            _bodies, Now, Lead, out _, deltaVLeft: 200));
        Assert.Contains("200 m/s of fuel", ex.Message);
    }

    /// <summary>
    /// A low Mun orbit from which there's more than one way to the station, and those ways (each picked by name, no fuel
    /// limit): where the shuttle is round the Mun decides whether meeting soonest costs more than the cheapest.
    /// </summary>
    private (OrbitalElements From, List<RouteTrip> Ways) TwoWays()
    {
        for (var inclination = 0.0; inclination <= 30; inclination += 10)
        for (var anomaly = 0.0; anomaly < 6.3; anomaly += 0.7)
        {
            var from = Round("Mun", 30_000, inclination, anomaly);
            var ways = new[] { "Cheapest", "Balanced", "Meets soonest" }
                .Select(w => RouteReplan.Plan(Body("Mun"), from, "Kerb Shuttle mk2", Body("Kerbin"), Station, _bodies, Now, Lead, out _, way: w)!)
                .DistinctBy(t => t.Way).ToList();
            if (ways.Count > 1 && ways.Max(w => w.DeltaV) > ways.Min(w => w.DeltaV) + 1) return (from, ways);
        }
        throw new InvalidOperationException("No Mun orbit tried gives two ways to the station.");
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
