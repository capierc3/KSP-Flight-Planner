using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;
using Xunit.Abstractions;

namespace KspFlightPlanner.Tests;

public sealed class RendezvousTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly CelestialBody _kerbin;

    public RendezvousTests(ITestOutputHelper output)
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

    private OrbitalElements Circular(double altitudeM, double inclinationDeg = 0, double lanDeg = 0, double meanAnomalyDeg = 0, double epoch = 0) =>
        new(_kerbin.EquatorialRadiusM + altitudeM, 0, inclinationDeg, lanDeg, 0, meanAnomalyDeg * Math.PI / 180, epoch);

    private OrbitalElements After(Maneuver burn, OrbitalElements before) => Rendezvous.OrbitAfter(burn, before, _kerbin.Mu);

    private Vector3d PositionAfterBurn(Maneuver burn, OrbitalElements before, double ut) =>
        Kepler.StateAt(After(burn, before), _kerbin.Mu, ut).Position;

    [Theory]
    [InlineData(800_000, 0.1, 30, 40, 50, 1.0)]
    [InlineData(700_000, 0.0, 0, 0, 0, 2.5)]
    [InlineData(900_000, 0.0, 45, 120, 0, 4.0)]
    [InlineData(1_500_000, 0.3, 150, 300, 200, 5.9)]
    [InlineData(2_000_000, 0.05, 0, 0, 80, 0.3)]
    public void Elements_from_state_round_trip(double a, double e, double i, double lan, double argPe, double meanAnomaly)
    {
        var orbit = new OrbitalElements(a, e, i, lan, argPe, meanAnomaly, 1000);
        foreach (var ut in new[] { 1000.0, 5000.0, 12345.6 })
        {
            var state = Kepler.StateAt(orbit, _kerbin.Mu, ut);
            var back = Kepler.ElementsFrom(state, _kerbin.Mu, ut);
            Assert.Equal(a, back.SemiMajorAxisM, 1e-3);
            Assert.Equal(e, back.Eccentricity, 1e-9);
            Assert.Equal(i, back.InclinationDeg, 1e-7);
            foreach (var later in new[] { ut, ut + 777 })
                Assert.True((Kepler.StateAt(orbit, _kerbin.Mu, later).Position - Kepler.StateAt(back, _kerbin.Mu, later).Position).Length < 1e-3);
        }
    }

    [Fact]
    public void Prograde_burn_has_no_normal_or_radial_part()
    {
        var s = Kepler.StateAt(new OrbitalElements(800_000, 0.2, 20, 10, 30, 1), _kerbin.Mu, 0);
        var burn = NodeBurn.From(s, s.Velocity.Normalized * 100);
        Assert.Equal(100, burn.Prograde, 1e-9);
        Assert.Equal(0, burn.Normal, 1e-9);
        Assert.Equal(0, burn.Radial, 1e-9);
    }

    [Fact]
    public void Coplanar_transfer_matches_hohmann_and_meets_the_target()
    {
        var chaser = Circular(100_000);
        var target = new RendezvousTarget("Relay", Circular(1_000_000, meanAnomalyDeg: 200));

        var leg = Rendezvous.PlanLeg(_kerbin, chaser, target, 0, PhasingChoice.None);
        var transfer = leg.Maneuvers.Single(m => m.Kind == ManeuverKind.Transfer);
        var match = leg.Maneuvers.Single(m => m.Kind == ManeuverKind.MatchVelocity);
        _output.WriteLine($"transfer {transfer.DeltaV:0.00} (Hohmann {leg.Guide.TransferDv:0.00}), match {match.DeltaV:0.00} (Hohmann {leg.Guide.MatchDv:0.00}), " +
                          $"lead {transfer.TargetAheadDeg:0.00}° (guide {leg.Guide.RequiredLeadDeg:0.00}°), wait {leg.Guide.WaitS:0} s, depart {transfer.Ut:0}");

        var miss0 = (PositionAfterBurn(transfer, chaser, leg.ArrivalUt) - Kepler.StateAt(target.Orbit, _kerbin.Mu, leg.ArrivalUt).Position).Length;
        _output.WriteLine($"miss {miss0:0.000} m, tof {transfer.FlightTimeS:0} (Hohmann {leg.Guide.TransferTimeS:0})");
        Assert.Equal(leg.Guide.TransferDv, transfer.DeltaV, 2.0);
        Assert.Equal(leg.Guide.MatchDv, match.DeltaV, 2.0);
        Assert.Equal(leg.Guide.RequiredLeadDeg, transfer.TargetAheadDeg, 1.0);
        Assert.Equal(transfer.Ut + transfer.FlightTimeS, leg.ArrivalUt, 1e-6);
        Assert.True(Math.Abs(transfer.Burn.Normal) < 0.01 && transfer.Burn.Prograde > 0);

        var miss = (PositionAfterBurn(transfer, chaser, leg.ArrivalUt) - Kepler.StateAt(target.Orbit, _kerbin.Mu, leg.ArrivalUt).Position).Length;
        Assert.True(miss < 10, $"missed by {miss:0.0} m");
    }

    [Fact]
    public void Plane_change_turns_the_orbit_into_the_targets_plane()
    {
        var chaser = Circular(200_000);
        var target = Circular(500_000, inclinationDeg: 10, lanDeg: 30);

        var (burn, after) = Rendezvous.MatchPlane(_kerbin, chaser, target, 0);
        var v = OrbitMath.CircularVelocity(_kerbin.Mu, _kerbin.EquatorialRadiusM + 200_000);
        Assert.Equal(OrbitMath.PlaneChangeDv(v, 10), burn.DeltaV, 0.01);
        Assert.Equal(10, burn.RelativeInclinationDeg, 1e-6);
        Assert.True(Rendezvous.RelativeInclinationDeg(after, target, _kerbin.Mu) < 1e-6);
        Assert.Equal(200_000, after.SemiMajorAxisM - _kerbin.EquatorialRadiusM, 1e-3);
        // Crossing the target's plane going "up" (north of it) means you burn anti-normal at the AN.
        Assert.Equal(burn.AtAscendingNode, burn.Burn.Normal < 0);
    }

    [Fact]
    public void Tiny_plane_change_is_left_to_the_transfer()
    {
        // 0.1° at ~2,250 m/s is about 3.9 m/s: under SmallPlaneChangeDv, so no Match planes burn.
        var chaser = Circular(100_000);
        var target = new RendezvousTarget("Relay", Circular(1_000_000, inclinationDeg: 0.1, lanDeg: 40, meanAnomalyDeg: 200));

        var leg = Rendezvous.PlanLeg(_kerbin, chaser, target, 0, PhasingChoice.None);
        Assert.DoesNotContain(leg.Maneuvers, m => m.Kind == ManeuverKind.PlaneChange);
        var transfer = leg.Maneuvers.Single(m => m.Kind == ManeuverKind.Transfer);
        var miss = (PositionAfterBurn(transfer, chaser, leg.ArrivalUt) - Kepler.StateAt(target.Orbit, _kerbin.Mu, leg.ArrivalUt).Position).Length;
        Assert.True(miss < 10, $"missed by {miss:0.0} m");
    }

    [Fact]
    public void Burns_leave_room_to_set_up_the_next_one()
    {
        var chaser = Circular(100_000);
        var target = new RendezvousTarget("Relay", Circular(1_000_000, inclinationDeg: 5, lanDeg: 40, meanAnomalyDeg: 200));

        var leg = Rendezvous.PlanLeg(_kerbin, chaser, target, 0, PhasingChoice.None);
        Assert.Equal(ManeuverKind.PlaneChange, leg.Maneuvers[0].Kind);
        foreach (var (first, next) in leg.Maneuvers.Zip(leg.Maneuvers.Skip(1)))
            Assert.True(next.Ut - first.Ut >= Rendezvous.BurnGapS - 1e-6, $"{first.Kind} → {next.Kind}: {next.Ut - first.Ut:0} s");
    }

    [Fact]
    public void Next_plane_crossing_is_in_the_targets_plane_and_no_later_than_the_plane_change()
    {
        var chaser = Circular(200_000);
        var target = Circular(500_000, inclinationDeg: 10, lanDeg: 30);

        var next = Rendezvous.NextPlaneCrossingUt(_kerbin, chaser, target, 100);
        var (burn, _) = Rendezvous.MatchPlane(_kerbin, chaser, target, 100);
        Assert.NotNull(next);
        Assert.True(next > 100 && next <= burn.Ut + 1e-6);
        var r = Kepler.StateAt(chaser, _kerbin.Mu, next!.Value).Position;
        var t0 = Kepler.StateAt(target, _kerbin.Mu, 0);
        var normal = Vector3d.Cross(t0.Position, t0.Velocity).Normalized;
        Assert.True(Math.Abs(Vector3d.Dot(r.Normalized, normal)) < 1e-6);
    }

    [Fact]
    public void Same_orbit_phasing_lap_then_a_short_hop_meets_the_target()
    {
        var chaser = Circular(200_000);
        var target = new RendezvousTarget("Station", Circular(200_000, meanAnomalyDeg: 40));

        var leg = Rendezvous.PlanLeg(_kerbin, chaser, target, 0);
        Assert.True(leg.Guide.SameOrbit);
        Assert.NotNull(leg.Phasing);
        Assert.Equal(ManeuverKind.Phasing, leg.Maneuvers[0].Kind);
        Assert.Equal(ManeuverKind.MatchVelocity, leg.Maneuvers[^1].Kind);
        _output.WriteLine($"{leg.Phasing!.Orbits} laps, {(leg.Phasing.Higher ? "higher" : "lower")}, Pe {leg.Phasing.PeriapsisAltitudeM:0} m, " +
                          $"burn {leg.Phasing.BurnDv:0.00}; {string.Join(", ", leg.Maneuvers.Select(m => $"{m.Kind} {m.DeltaV:0.00}"))}");
        // Target 40° ahead: catch up by flying lower (faster), and the cheapest option is the most laps.
        Assert.False(leg.Phasing.Higher);
        Assert.Equal(Rendezvous.MaxPhasingLaps, leg.Phasing.Orbits);
        Assert.Equal(leg.PhasingOptions.Min(o => o.ExtraDv), leg.Phasing.ExtraDv);
        // Coming back to the start point with the target right there, the hop just undoes the phasing burn.
        Assert.Equal(2 * Math.Abs(leg.Phasing.BurnDv), leg.TotalDv, 0.5);

        // Fly the burns (all but the final match) and check where you end up.
        var orbit = chaser;
        foreach (var burn in leg.Maneuvers.SkipLast(1)) orbit = After(burn, orbit);
        var miss = (Kepler.StateAt(orbit, _kerbin.Mu, leg.ArrivalUt).Position - Kepler.StateAt(target.Orbit, _kerbin.Mu, leg.ArrivalUt).Position).Length;
        Assert.True(miss < 10, $"missed by {miss:0.0} m");
    }

    [Fact]
    public void Nearly_shared_eccentric_orbit_still_ends_at_the_target()
    {
        // Like two KerbLink relays: about the same size, slightly different shape and tilt.
        var chaser = new OrbitalElements(_kerbin.EquatorialRadiusM + 2_875_000, 0.0014, 1.0, 20, 60, 1.0);
        var target = new RendezvousTarget("KerbLink Mk2 A", new OrbitalElements(_kerbin.EquatorialRadiusM + 2_870_500, 0.0027, 0.5, 200, 10, 3.0));

        var leg = Rendezvous.PlanLeg(_kerbin, chaser, target, 0);
        _output.WriteLine(string.Join(", ", leg.Maneuvers.Select(m => $"{m.Kind} {m.DeltaV:0.0}")) + $" → {leg.ArrivalUt / 3600:0.0} h");
        Assert.True(leg.Guide.SameOrbit);
        Assert.NotNull(leg.Phasing);
        Assert.Equal(ManeuverKind.MatchVelocity, leg.Maneuvers[^1].Kind);
        Assert.Equal(0, leg.Maneuvers[^1].MissDistanceM);
        Assert.True(leg.TotalDv < 120);
    }

    [Fact]
    public void Phasing_options_return_with_the_target_at_the_lead_angle()
    {
        var chaser = Circular(100_000);
        var target = Circular(150_000, meanAnomalyDeg: 10);
        var guide = Rendezvous.Guide(chaser, target, _kerbin.Mu, 0);
        var options = Rendezvous.PhasingOptions(_kerbin, chaser, target, guide, 0);
        Assert.NotEmpty(options);
        foreach (var o in options)
        {
            // Back at the start point after k laps (circular target): the target is exactly at the lead angle.
            var lead = guide.CurrentLeadDeg + 360 * o.Orbits * o.PeriodS / guide.TargetPeriodS;
            Assert.Equal(0, Math.IEEERemainder(lead - guide.RequiredLeadDeg, 360), 1e-6);
            Assert.True(o.PeriapsisAltitudeM > _kerbin.AtmosphereHeightM);
            Assert.Equal(o.Higher, o.PeriodS > guide.ChaserPeriodS);
        }
    }

    [Fact]
    public void Ksc_longitude_follows_kerbins_rotation()
    {
        var ksc = LaunchSite.Ksc;
        Assert.Equal(15.4425, ksc.InertialLongitudeDeg(_kerbin, 0), 1e-9);
        Assert.Equal(15.4425, ksc.InertialLongitudeDeg(_kerbin, _kerbin.RotationPeriodS), 1e-6);
        Assert.Equal(105.4425, ksc.InertialLongitudeDeg(_kerbin, _kerbin.RotationPeriodS / 4), 1e-6);
    }

    [Fact]
    public void In_plane_launches_lift_off_under_the_target_orbit()
    {
        var target = Circular(1_000_000, inclinationDeg: 45, lanDeg: 100);
        var options = Rendezvous.LaunchOptions(LaunchSite.Ksc, _kerbin, new AscentProfile(80_000), target, 1000);

        Assert.Equal(5, options.Count); // two ascending, two descending passes, one due east
        foreach (var o in options.Where(o => o.Kind != LaunchKind.DueEast))
        {
            var node = o.Kind == LaunchKind.AscendingPass ? 100 : 280;
            Assert.Equal(0, Math.IEEERemainder(LaunchSite.Ksc.InertialLongitudeDeg(_kerbin, o.LiftoffUt) - node, 360), 1e-6);
            Assert.True(Rendezvous.RelativeInclinationDeg(o.ParkingOrbit, target, _kerbin.Mu) < 1e-6);
            Assert.True(o.LiftoffUt >= 1000);
        }
        // Heading north-east for i = 45°, a little north of 45° on the compass because Kerbin's spin helps eastward.
        var ascending = options.First(o => o.Kind == LaunchKind.AscendingPass);
        _output.WriteLine($"ascending pass heading {ascending.HeadingDeg:0.00}°");
        Assert.InRange(ascending.HeadingDeg, 40, 45);
        var descending = options.First(o => o.Kind == LaunchKind.DescendingPass);
        Assert.InRange(descending.HeadingDeg, 135, 140);
        Assert.Equal(90, options.Single(o => o.Kind == LaunchKind.DueEast).HeadingDeg, 1e-9);
    }

    [Fact]
    public void Deorbit_burn_drops_periapsis_into_the_atmosphere()
    {
        var orbit = Circular(80_000);
        var burn = Rendezvous.Deorbit(_kerbin, orbit, 500, 30_000);
        double ra = _kerbin.EquatorialRadiusM + 80_000, rp = _kerbin.EquatorialRadiusM + 30_000;
        var expected = Math.Sqrt(_kerbin.Mu / ra) - Math.Sqrt(2 * _kerbin.Mu * rp / (ra * (ra + rp)));
        _output.WriteLine($"deorbit {burn.DeltaV:0.00} m/s");
        Assert.Equal(expected, burn.DeltaV, 1e-6);
        Assert.True(burn.Burn.Prograde < 0);
        Assert.Equal(30_000, burn.PeriapsisAltitudeM, 1);
        Assert.Equal(500, burn.Ut);
    }

    [Fact]
    public void Route_visits_targets_in_turn_then_deorbits()
    {
        var request = new RendezvousRequest(_kerbin,
            [new RendezvousTarget("Kerblink A", Circular(1_200_000, 5, 40, 90)), new RendezvousTarget("Kerblink B", Circular(1_200_000, 5, 40, 210))],
            EarliestUt: 10_000, StayS: 3600,
            Chaser: new RendezvousTarget("Tug", Circular(150_000, meanAnomalyDeg: 0)),
            DeorbitPeriapsisAltitudeM: 30_000);

        var plan = Rendezvous.Plan(request);
        Assert.Equal(2, plan.Legs.Count);
        Assert.Equal(ManeuverKind.PlaneChange, plan.Legs[0].Maneuvers[0].Kind);
        Assert.True(plan.Legs[1].StartUt >= plan.Legs[0].ArrivalUt + 3600);
        Assert.True(plan.Legs[1].Guide.SameOrbit); // B shares A's orbit, 120° apart
        Assert.All(plan.Legs[1].Maneuvers, m => Assert.NotEqual(ManeuverKind.PlaneChange, m.Kind));
        Assert.NotNull(plan.Deorbit);
        Assert.True(plan.Deorbit!.Ut >= plan.Legs[1].ArrivalUt);
        foreach (var leg in plan.Legs)
            _output.WriteLine($"{leg.Target.Name}: {string.Join(", ", leg.Maneuvers.Select(m => $"{m.Kind} {m.DeltaV:0.0}"))} → arrive {leg.ArrivalUt:0}");
        _output.WriteLine($"deorbit {plan.Deorbit.DeltaV:0.0}, total {plan.OrbitalDv:0.0}");
    }

    [Fact]
    public void Launch_route_picks_an_option_and_meets_the_target()
    {
        var target = new RendezvousTarget("Relay", Circular(2_000_000, 30, 250, 45));
        var request = new RendezvousRequest(_kerbin, [target], EarliestUt: 0, StayS: 0, Site: LaunchSite.Ksc, Ascent: new AscentProfile(80_000));
        var plan = Rendezvous.Plan(request);
        Assert.NotNull(plan.Launch);
        var leg = plan.Legs[0];
        foreach (var (o, i) in plan.LaunchOptions.Select((o, i) => (o, i)))
            _output.WriteLine($"{(i == plan.LaunchIndex ? "*" : " ")} {o.Kind} lift-off {o.LiftoffUt:0} heading {o.HeadingDeg:0.0}");
        _output.WriteLine(string.Join(", ", leg.Maneuvers.Select(m => $"{m.Kind} {m.DeltaV:0.0}")));
        Assert.NotEqual(LaunchKind.DueEast, plan.Launch!.Kind); // 30° plane change after launch is far too expensive
        Assert.DoesNotContain(leg.Maneuvers, m => m.Kind == ManeuverKind.PlaneChange);
    }
}
