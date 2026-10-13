using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;
using Xunit.Abstractions;

namespace KspFlightPlanner.Tests;

public sealed class TripPlannerTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly IReadOnlyList<CelestialBody> _bodies;

    public TripPlannerTests(ITestOutputHelper output)
    {
        _output = output;
        var db = new PlannerDatabase(_path);
        db.Initialize();
        var repo = new BodyRepository(db);
        _bodies = repo.GetBodies(repo.GetSystems()[0].Id);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private CelestialBody Body(string name) => _bodies.Single(b => b.Name == name);

    /// <summary>A circular equatorial orbit around <paramref name="body"/> at <paramref name="altitudeM"/>, stated at UT 1,000,000.</summary>
    private OrbitalElements Circular(string body, double altitudeM) => new(Body(body).EquatorialRadiusM + altitudeM, 0, 0, 0, 0, 0.3, 1_000_000);

    private string Route(string from, string to) => string.Join(", ", TripPlanner.Route(Body(from), Body(to), _bodies));

    private void Show(TripOption option, double now)
    {
        _output.WriteLine($"— {option.Label}: {option.DeltaV:0} m/s, leave in {(option.DepartureUt - now) / 3600:0.0} h, " +
                          $"arrive after {(option.ArrivalUt - option.DepartureUt) / 86_400:0.0} days");
        foreach (var leg in option.Legs)
        {
            _output.WriteLine($"  {leg.Hop}: periapsis {leg.Plan.Arrival.PeriapsisAltitudeM / 1000:0.000} km (asked {leg.AltitudeM / 1000:0} km), prograde {leg.Plan.Arrival.Prograde}");
            foreach (var b in leg.Plan.Burns)
                _output.WriteLine($"    {b.Title}: {b.DeltaV:0.0} m/s (pro {b.Burn.Prograde:0.0}, nrm {b.Burn.Normal:0.0}, rad {b.Burn.Radial:0.0}) at +{(b.Ut - now) / 3600:0.0} h");
        }
        foreach (var w in option.Warnings) _output.WriteLine("  ! " + w);
    }

    [Fact]
    public void Routes_go_up_to_the_common_parent_across_and_down()
    {
        Assert.Equal("Down Kerbin → Mun", Route("Kerbin", "Mun"));
        Assert.Equal("Up Mun → Kerbin", Route("Mun", "Kerbin"));
        Assert.Equal("Across Mun → Minmus", Route("Mun", "Minmus"));
        Assert.Equal("Across Kerbin → Duna", Route("Kerbin", "Duna"));
        Assert.Equal("Up Mun → Kerbin, Across Kerbin → Duna", Route("Mun", "Duna"));
        Assert.Equal("Across Kerbin → Duna, Down Duna → Ike", Route("Kerbin", "Ike"));
        Assert.Equal("Up Mun → Kerbin, Across Kerbin → Duna, Down Duna → Ike", Route("Mun", "Ike"));
        Assert.Equal("Down Kerbol → Duna", Route("Kerbol", "Duna"));
        Assert.Throws<ArgumentException>(() => TripPlanner.Route(Body("Kerbin"), Body("Kerbin"), _bodies));
        Assert.Throws<ArgumentException>(() => TripPlanner.Route(Body("Kerbin"), Body("Kerbol"), _bodies));
    }

    [Fact]
    public void Kerbin_to_duna_offers_windows_and_each_arrives_at_the_asked_height()
    {
        var now = 1_000_000.0;
        var options = TripPlanner.Options(Body("Kerbin"), Body("Duna"), Circular("Kerbin", 100_000), now + 120, 100_000, _bodies);
        foreach (var o in options) Show(o, now);

        Assert.StartsWith("Cheapest", options[0].Label);
        Assert.True(options.Count >= 2);
        foreach (var o in options)
        {
            Assert.Equal(100_000, o.Arrival.PeriapsisAltitudeM, 100.0);
            Assert.True(o.Arrival.Prograde);
            var leave = o.Legs[0].Plan.Departure[0];
            Assert.StartsWith("Leave Kerbin", leave.Title);
        }
        var cheapest = options[0];
        Assert.InRange(cheapest.Legs[0].Plan.Departure[0].DeltaV, 1_000, 1_150); // the Δv map: 950 + 130 from low Kerbin orbit
        Assert.True(cheapest.Legs[0].Plan.Departure.Skip(1).Sum(b => b.DeltaV) < 100, "the course correction should be small");
        Assert.All(options.Skip(1), o => Assert.True(o.DeltaV > cheapest.DeltaV - 20));
    }

    [Fact]
    public void Mun_to_minmus_goes_across_around_kerbin()
    {
        var now = 1_000_000.0;
        var options = TripPlanner.Options(Body("Mun"), Body("Minmus"), Circular("Mun", 30_000), now + 120, 20_000, _bodies);
        foreach (var o in options) Show(o, now);
        Assert.All(options, o => Assert.Equal(20_000, o.Arrival.PeriapsisAltitudeM, 100.0));
        Assert.InRange(options[0].DeltaV, 300, 900);
    }

    [Fact]
    public void Mun_to_duna_stops_in_kerbin_orbit_first()
    {
        var now = 1_000_000.0;
        var options = TripPlanner.Options(Body("Mun"), Body("Duna"), Circular("Mun", 30_000), now + 120, 100_000, _bodies);
        foreach (var o in options) Show(o, now);
        var cheapest = options[0];
        Assert.Equal(2, cheapest.Legs.Count);
        Assert.Equal(Body("Kerbin").DefaultParkingAltitudeM, cheapest.Legs[0].Plan.Arrival.PeriapsisAltitudeM, 100.0);
        Assert.Equal(100_000, cheapest.Arrival.PeriapsisAltitudeM, 100.0);
        Assert.All(cheapest.Legs[1].Plan.Departure, b => Assert.Contains("once there", b.Note));
    }

    [Fact]
    public void Planned_again_around_the_sun_after_a_sloppy_ejection_it_is_a_small_correction()
    {
        var now = 1_000_000.0;
        var craft = Circular("Kerbin", 100_000);
        var cheapest = TripPlanner.Options(Body("Kerbin"), Body("Duna"), craft, now + 120, 100_000, _bodies)[0];
        var leave = cheapest.Legs[0].Plan.Departure[0];
        // 2 m/s too hard: out of Kerbin's sphere onto the path around the Sun.
        var flown = Rendezvous.OrbitAfter(leave.Ut, leave.Burn with { Prograde = leave.Burn.Prograde + 2 }, craft, Body("Kerbin").Mu);
        var exit = Hops.LeaveSoi(Body("Kerbin"), Body("Kerbol"), flown, leave.Ut, leave.Ut + 1e7)!;

        var fix = TripPlanner.Options(Body("Kerbol"), Body("Duna"), exit.Path, exit.CrossingUt + 86_400, 100_000, _bodies);
        Show(fix[0], now);
        Assert.Single(fix);
        Assert.True(fix[0].IsCorrection);
        Assert.InRange(fix[0].Legs[0].Plan.Departure.Sum(b => b.DeltaV), 0.01, 40);
        Assert.Equal(100_000, fix[0].Arrival.PeriapsisAltitudeM, 100.0);
    }
}
