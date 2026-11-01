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
    public void A_trip_to_mun_station_has_no_phasing_burn_of_its_own()
    {
        // The arrival can't quite reach the station's plane, so the meeting starts with a plane change: the phasing rides on it.
        var station = new RendezvousTarget("Mun Station", Orbit("Mun", 45_000, 6, 250));
        var plan = Meet(station);
        Show(plan);
        foreach (var way in plan.TripOptions)
        {
            Assert.DoesNotContain(way.Leg.Maneuvers, m => m.Kind == ManeuverKind.Phasing);
            if (way.Leg.Phasing is not null)
                Assert.Contains(way.Leg.Maneuvers, m => m.Kind == ManeuverKind.PlaneChange && m.Phasing == way.Leg.Phasing);
        }
        AssertNoSeparatePhasing(Body("Mun"), station, plan);
    }

    /// <summary>Each way's phasing orbit, if any, is part of the capture or of the plane change, and costs no more than apart.</summary>
    private void AssertNoSeparatePhasing(CelestialBody body, RendezvousTarget target, RendezvousPlan plan)
    {
        foreach (var way in plan.TripOptions)
        {
            Assert.DoesNotContain(way.Leg.Maneuvers, m => m.Kind == ManeuverKind.Phasing);
            if (!way.CapturesIntoPhasing) continue;

            var capture = way.Trip.Legs[^1].Plan.Capture!;
            Assert.Contains("phasing orbit", capture.Title);
            // The capture leaves the craft on the phasing orbit: its period is the one the meeting planned.
            Assert.Equal(way.Leg.Phasing!.PeriodS, Kepler.OrbitalPeriod(way.Captured, body.Mu), 1.0);
            // One burn costs no more than the plain capture and the phasing burn flown apart.
            var arriving = Kepler.StateAt(way.Trip.Arrival.Path, body.Mu, capture.Ut);
            var plain = (Kepler.StateAt(way.Parked, body.Mu, capture.Ut).Velocity - arriving.Velocity).Length;
            var apart = Rendezvous.PlanLeg(body, way.Parked, target, capture.Ut, way.Leg.Phasing.Choice).Maneuvers[0];
            Assert.Equal(ManeuverKind.Phasing, apart.Kind);
            _output.WriteLine($"{way.Label}: capture {capture.DeltaV:0.0} m/s vs {plain:0.0} + {apart.DeltaV:0.0} apart");
            Assert.True(capture.DeltaV <= plain + apart.DeltaV + 0.01, $"{capture.DeltaV:0.0} vs {plain + apart.DeltaV:0.0}");
        }
        Assert.Same(plan.TripOptions[plan.TripIndex].Leg, plan.Legs[0]);
    }

    [Fact]
    public void A_trip_to_a_low_kerbin_station_never_meets_it_through_the_air()
    {
        var kerbin = Body("Kerbin");
        var station = new RendezvousTarget("Kerb Station", Orbit("Kerbin", 80_000, 0, 0));
        var shuttle = new RendezvousTarget("Mun Shuttle", Orbit("Mun", 40_000, 0, 0));
        var plan = Rendezvous.Plan(new RendezvousRequest(kerbin, [station], Now + 120, 0, shuttle) { From = new TripStart(Body("Mun"), _bodies) });
        Show(plan);

        foreach (var way in plan.TripOptions)
            AssertClearOfTheAir(kerbin, way.Captured, way.Leg);
        // Coming in level with the station, the phasing orbit is flown straight from the capture.
        Assert.Contains(plan.TripOptions, w => w.CapturesIntoPhasing);
        AssertNoSeparatePhasing(kerbin, station, plan);
    }

    [Theory]
    [InlineData(80_000, 80_000, 30)]   // same orbit, the station a little ahead: the short hop used to dive into the air
    [InlineData(80_000, 80_000, 300)]
    [InlineData(76_000, 80_000, 200)]  // just under it, as a trip's capture leaves the craft
    [InlineData(84_000, 80_000, 10)]   // just over it
    public void An_intercept_round_kerbin_stays_above_the_atmosphere(double chaserM, double stationM, double aheadDeg)
    {
        var kerbin = Body("Kerbin");
        var chaser = Orbit("Kerbin", chaserM, 0, 0, 0);
        var station = new RendezvousTarget("Kerb Station", Orbit("Kerbin", stationM, 0, 0, aheadDeg * Math.PI / 180));
        var leg = Rendezvous.PlanLeg(kerbin, chaser, station, Now);
        foreach (var m in leg.Maneuvers) _output.WriteLine($"  {m.Kind}: {m.DeltaV:0.0} m/s, Pe {m.PeriapsisAltitudeM / 1000:0.0} km");
        AssertClearOfTheAir(kerbin, chaser, leg);
    }

    [Fact]
    public void A_path_only_dips_when_it_passes_its_periapsis_on_the_way()
    {
        var kerbin = Body("Kerbin");
        var floor = Rendezvous.LowestPassRadiusM(kerbin);
        Assert.Equal(kerbin.EquatorialRadiusM + 71_000, floor, 1.0);
        // 100 km × 60 km, starting at apoapsis: down to 60 km half an orbit later.
        var r = kerbin.EquatorialRadiusM;
        var dipping = new OrbitalElements((2 * r + 160_000) / 2, 20_000 / ((2 * r + 160_000) / 2), 0, 0, 0, Math.PI, Now);
        var half = Kepler.OrbitalPeriod(dipping, kerbin.Mu) / 2;
        Assert.False(Rendezvous.DipsBelow(dipping, kerbin.Mu, Now, Now + 0.4 * half, floor));
        Assert.True(Rendezvous.DipsBelow(dipping, kerbin.Mu, Now, Now + 1.2 * half, floor));
    }

    /// <summary>Every coast of the leg, from <paramref name="start"/> through each burn to the meeting, stays over the air.</summary>
    private void AssertClearOfTheAir(CelestialBody body, OrbitalElements start, LegPlan leg)
    {
        var floor = body.EquatorialRadiusM + body.AtmosphereHeightM;
        var orbit = start;
        var burns = leg.Maneuvers.ToList();
        for (var i = 0; i < burns.Count - 1; i++)
        {
            orbit = Rendezvous.OrbitAfter(burns[i], orbit, body.Mu);
            var lowest = Kepler.PeriapsisRadius(orbit);
            var passes = Kepler.NextPeriapsisUt(orbit, body.Mu, burns[i].Ut) is { } pe && pe < burns[i + 1].Ut;
            _output.WriteLine($"  after {burns[i].Kind}: Pe {(lowest - body.EquatorialRadiusM) / 1000:0.0} km{(passes ? " (passed)" : "")}");
            Assert.False(passes && lowest < floor, $"after the {burns[i].Kind} burn the path drops to {(lowest - body.EquatorialRadiusM) / 1000:0.0} km");
        }
    }

    [Fact]
    public void A_target_too_low_to_go_under_is_met_from_just_over_it()
    {
        var low = new RendezvousTarget("Low", Orbit("Mun", 10_000, 0, 0));
        Assert.Equal(13_000, Rendezvous.MeetingAltitudeM(Body("Mun"), low.Orbit), 1.0);
    }
}
