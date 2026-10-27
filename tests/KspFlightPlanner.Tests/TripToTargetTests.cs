using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;
using Xunit.Abstractions;

namespace KspFlightPlanner.Tests;

/// <summary>Meeting a craft around another body: Kerb Station (Kerbin) to a station or relay around the Mun (§4.6).</summary>
public sealed class TripToTargetTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly IReadOnlyList<CelestialBody> _bodies;
    private const double Now = 1_000_000;

    public TripToTargetTests(ITestOutputHelper output)
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

    private OrbitalElements Orbit(string body, double altitudeM, double inclinationDeg, double lanDeg, double meanAnomaly = 0.3) =>
        new(Body(body).EquatorialRadiusM + altitudeM, 0, inclinationDeg, lanDeg, 0, meanAnomaly, Now);

    /// <summary>The station's shuttle at 100 km, tilted a little, as a docked shuttle at Kerb Station would be.</summary>
    private RendezvousTarget Shuttle => new("Kerb Shuttle", Orbit("Kerbin", 100_000, 1.5, 30));

    private RendezvousPlan Meet(RendezvousTarget target) =>
        Rendezvous.Plan(new RendezvousRequest(Body("Mun"), [target], Now + 120, 0, Shuttle) { From = new TripStart(Body("Kerbin"), _bodies) });

    /// <summary>The old way: Go to the Mun (low orbit, however it arrives), then a rendezvous from there.</summary>
    private double TwoStep(RendezvousTarget target)
    {
        var trip = TripPlanner.Options(Body("Kerbin"), Body("Mun"), Shuttle.Orbit, Now + 120, Body("Mun").DefaultParkingAltitudeM, _bodies)
            .MinBy(o => o.DeltaV)!;
        var captured = Hops.Captured(trip.Legs[^1].Plan, Body("Mun"))!;
        var meet = Rendezvous.Plan(new RendezvousRequest(Body("Mun"), [target], trip.Legs[^1].Plan.Capture!.Ut + Rendezvous.BurnGapS, 0,
            new RendezvousTarget("Kerb Shuttle", captured)));
        _output.WriteLine($"two steps: trip {trip.DeltaV:0} + meet {meet.OrbitalDv:0} = {trip.DeltaV + meet.OrbitalDv:0} m/s");
        return trip.DeltaV + meet.OrbitalDv;
    }

    private void Show(RendezvousPlan plan)
    {
        foreach (var t in plan.TripOptions)
            _output.WriteLine($"{t.Label}: trip {t.Trip.DeltaV:0} + meet {t.MeetDv:0} = {t.TotalDv:0} m/s, meets after {(t.MeetUt - Now) / 3600:0.0} h");
        foreach (var b in plan.Trip!.Burns)
            _output.WriteLine($"  {b.Title}: {b.DeltaV:0.0} m/s at +{(b.Ut - Now) / 3600:0.0} h");
        foreach (var m in plan.Legs[0].Maneuvers)
            _output.WriteLine($"  {m.Kind}: {m.DeltaV:0.0} m/s at +{(m.Ut - Now) / 3600:0.0} h");
    }

    [Fact]
    public void A_polar_relay_round_the_mun_is_met_in_one_plan_for_far_less_than_going_there_then_turning()
    {
        var relay = new RendezvousTarget("MunLink", Orbit("Mun", 200_000, 88, 140));
        var plan = Meet(relay);
        Show(plan);

        // Arrives near the relay's plane and height: what's left of the turn is small.
        var captured = plan.TripOptions[plan.TripIndex].Captured;
        var off = Rendezvous.RelativeInclinationDeg(captured, relay.Orbit, Body("Mun").Mu);
        _output.WriteLine($"arrives {off:0.0}° off the relay's plane");
        Assert.True(off < 15, $"{off:0.0}°");
        Assert.Equal(Rendezvous.MeetingAltitudeM(Body("Mun"), relay.Orbit), Kepler.PeriapsisRadius(captured) - Body("Mun").EquatorialRadiusM, 2_000.0);
        Assert.StartsWith("Cheapest", plan.TripOptions[0].Label);
        Assert.Contains(plan.Trip!.Burns, b => b.Title.Contains("MunLink's orbit"));

        // Turning a Mun orbit most of 88° costs hundreds of m/s; aiming on the way costs tens.
        var twoStep = TwoStep(relay);
        Assert.True(plan.OrbitalDv < twoStep - 300, $"{plan.OrbitalDv:0} vs {twoStep:0}");
    }

    [Fact]
    public void A_station_in_a_low_slightly_tilted_mun_orbit_is_met_from_just_under_it()
    {
        var station = new RendezvousTarget("Mun Station", Orbit("Mun", 45_000, 6, 250));
        var plan = Meet(station);
        Show(plan);

        // 45 km less 3 km is above the Mun's 9 km parking height: arrive under it, in its plane, and catch up.
        Assert.Equal(42_000, Rendezvous.MeetingAltitudeM(Body("Mun"), station.Orbit), 1.0);
        var twoStep = TwoStep(station);
        Assert.True(plan.OrbitalDv < twoStep, $"{plan.OrbitalDv:0} vs {twoStep:0}");
        Assert.True(plan.OrbitalDv < 1_350, $"{plan.OrbitalDv:0}"); // ~860 transfer + ~300 capture + a little to meet
    }

    [Fact]
    public void Picking_the_other_trip_option_plans_from_its_capture()
    {
        var relay = new RendezvousTarget("MunLink", Orbit("Mun", 200_000, 88, 140));
        var first = Meet(relay);
        if (first.TripOptions.Count < 2) return; // only one way worth offering here
        var other = Rendezvous.Plan(new RendezvousRequest(Body("Mun"), [relay], Now + 120, 0, Shuttle) { From = new TripStart(Body("Kerbin"), _bodies) },
            new RendezvousChoices { Trip = 1 });
        Assert.Equal(1, other.TripIndex);
        Assert.Equal(first.TripOptions[1].Trip.DeltaV, other.Trip!.DeltaV, 3);
    }

    [Fact]
    public void A_target_too_low_to_go_under_is_met_from_just_over_it()
    {
        var low = new RendezvousTarget("Low", Orbit("Mun", 10_000, 0, 0));
        Assert.Equal(13_000, Rendezvous.MeetingAltitudeM(Body("Mun"), low.Orbit), 1.0);
    }
}
