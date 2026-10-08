using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.Tests;

/// <summary>Tools on a mission's legs: the Duna relay mission (Kerbin surface → LKO → Duna 1,000 km).</summary>
public sealed class LegToolsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly PlannerDatabase _db;
    private readonly DeltaVMap _map;
    private readonly Dictionary<string, CelestialBody> _bodies;
    private readonly string _relayOrbit;

    public LegToolsTests()
    {
        _db = new PlannerDatabase(_path);
        _db.Initialize();
        _relayOrbit = $"custom.{new CustomLocationRepository(_db).Add("Duna relays", "Duna", 1_000_000)}";
        _map = new DvMapRepository(_db).GetMap()!;
        var bodies = new BodyRepository(_db);
        _bodies = bodies.GetBodies(bodies.GetSystems().Single(s => s.Name == "KSP Stock").Id).ToDictionary(b => b.Name);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private IReadOnlyList<RouteLeg> Route(string from, string to) => _map.Route(_map[from], _map[to]);

    /// <summary>The best window from Year 1, Day 1, as the Transfer Planner would pick it.</summary>
    private TransferLeg BestWindow(TransferSpan span)
    {
        var best = TransferPlanner.Plan(span.Request(0), 60, 40).BestWindow;
        return new TransferLeg(0, best.DepartureUt, best.FlightTimeS);
    }

    [Fact]
    public void Lko_to_duna_relay_orbit_is_a_transfer_from_kerbin_to_duna()
    {
        var legs = Route("kerbin.lo", _relayOrbit);
        var span = LegTools.FindTransfer(legs, _bodies);

        Assert.NotNull(span);
        Assert.Equal(("Kerbin", "Duna", "Kerbol"), (span.Origin.Name, span.Destination.Name, span.Parent.Name));
        Assert.Equal((80_000, 60_000), (span.ParkingAltitudeM, span.ArrivalAltitudeM!.Value));
        // LKO → escape → Duna intercept (ejection), → elliptical → Duna LO (capture), then up to 1,000 km on the map.
        Assert.Equal(5, legs.Count);
        Assert.Equal((0, 1, 2, 3), (span.EjectionStart, span.EjectionEnd, span.CaptureStart, span.CaptureEnd));
    }

    [Fact]
    public void Transfer_window_replaces_the_map_values_it_covers()
    {
        var legs = Route("kerbin.lo", _relayOrbit);
        var choice = BestWindow(LegTools.FindTransfer(legs, _bodies)!);
        var result = LegTools.EvaluateTransfer(legs, choice, _bodies)!;
        var applied = LegTools.ApplyTransfer(legs, result);

        Assert.Equal(Math.Round(result.Window.EjectionDv), applied[0].DeltaV);
        Assert.Equal([0.0, 0.0, 0.0], new[] { applied[1].DeltaV, applied[3].DeltaV, applied[1].PlaneChangeDv });
        Assert.Equal(Math.Round(result.Window.CaptureDv), applied[2].DeltaV);
        Assert.Equal(legs[4], applied[4]); // Duna LO → 1,000 km stays the map's
        Assert.All(applied.Take(4), l => Assert.True(l.IsFromTool && !l.IsOverridden));

        // A real Kerbin → Duna window: ≈ 1 km/s to leave LKO, ≈ 0.6 km/s to stop in low Duna orbit.
        Assert.InRange(applied[0].DeltaV, 1000, 1150);
        Assert.InRange(applied[2].DeltaV, 500, 800);
        // Duna has air: the capture can still be aerobraked instead.
        Assert.True(applied[2].CanAerobrake);
    }

    [Fact]
    public void Trip_home_from_duna_captures_at_kerbin_and_can_aerobrake()
    {
        var legs = Route("duna.lo", "kerbin.lo");
        var span = LegTools.FindTransfer(legs, _bodies)!;

        Assert.Equal(("Duna", "Kerbin"), (span.Origin.Name, span.Destination.Name));
        Assert.Equal((0, 2, 3, 3), (span.EjectionStart, span.EjectionEnd, span.CaptureStart, span.CaptureEnd));
        var applied = LegTools.ApplyTransfer(legs, LegTools.EvaluateTransfer(legs, BestWindow(span), _bodies)!);
        Assert.False(applied[2].CanAerobrake); // part of the ejection now
        Assert.True(applied[3].CanAerobrake);
    }

    [Theory]
    [InlineData("kerbin.lo", "mun.lo")]     // a moon of the planet you're at: the map's Hohmann
    [InlineData("kerbin.surface", "kerbin.keo")]
    [InlineData("mun.lo", "minmus.lo")]
    public void Legs_without_an_interplanetary_crossing_have_no_transfer(string from, string to) =>
        Assert.Null(LegTools.FindTransfer(Route(from, to), _bodies));

    [Fact]
    public void Leg_to_a_flyby_keeps_the_map_capture()
    {
        // Kerbin → Ike passes Duna: the window replaces the ejection; the capture around Duna stays on the map.
        var legs = Route("kerbin.lo", "ike.lo");
        var span = LegTools.FindTransfer(legs, _bodies)!;
        Assert.Equal("Duna", span.Destination.Name);
        Assert.False(span.Captures);
        var applied = LegTools.ApplyTransfer(legs, LegTools.EvaluateTransfer(legs, BestWindow(span), _bodies)!);
        Assert.Equal(legs.Skip(2), applied.Skip(2));
    }

    [Fact]
    public void Duna_relay_mission_totals_include_the_window_and_survive_a_save()
    {
        var stops = new[] { "kerbin.surface", "kerbin.lo", _relayOrbit }.Select(k => _map[k]).ToList();
        var segments = MissionPlanner.BuildRoute(_map, stops);
        var choice = BestWindow(LegTools.FindTransfer(segments[1].Legs, _bodies)!);
        var (phases, settings) = MissionPlanner.DefaultPhases(segments, stops);
        var definition = new MissionDefinition(
            [new("kerbin.surface"), new("kerbin.lo"), new(_relayOrbit, "Deploy relays", choice)], phases, settings);

        var repo = new MissionRepository(_db);
        var reopened = repo.Get(repo.Add("Duna relays", definition))!.Definition;
        Assert.Equal(MissionRepository.Serialize(definition), MissionRepository.Serialize(reopened));
        Assert.Equal(choice, reopened.Stops[2].Transfer);

        var applied = LegTools.Apply(segments, reopened.Stops, _bodies);
        var window = LegTools.EvaluateTransfer(segments[1].Legs, choice, _bodies)!.Window;
        var expected = 3400 + Math.Round(window.EjectionDv) + Math.Round(window.CaptureDv) + segments[1].Legs[4].DeltaV;
        var summary = MissionPlanner.Summarize(reopened.Phases, MissionPlanner.AllLegs(applied),
            reopened.Legs.Select(s => s with { UseAerobrake = false }).ToList());
        Assert.Equal(expected, summary.Sum(s => s.RequiredDv));
    }

    [Fact]
    public void A_transfer_left_on_a_stop_that_no_longer_fits_is_ignored()
    {
        var stops = new[] { "kerbin.surface", "mun.lo" }.Select(k => _map[k]).ToList();
        var segments = MissionPlanner.BuildRoute(_map, stops);
        var applied = LegTools.Apply(segments, [new("kerbin.surface"), new("mun.lo", Transfer: new TransferLeg(0, 1000, 1000))], _bodies);
        Assert.Equal(segments[0].Legs, applied[0].Legs);
    }

    [Fact]
    public void Three_relays_at_duna_1000_km_need_one_carrier_burn_at_the_end_of_the_mission()
    {
        var stop = _map[_relayOrbit];
        var last = LegTools.EvaluateDeploy(stop, new DeployStep(3), _bodies, continues: false)!;
        var direct = ResonantOrbitCalculator.Calculate(new ResonantOrbitRequest(_bodies["Duna"], 3, 1_000_000));

        Assert.Equal(1_000_000, last.AltitudeM);
        Assert.Equal(Math.Round(direct.InjectionDeltaV, 1), last.EnterDv);
        Assert.Equal(0, last.ReturnDv);
        Assert.Equal(direct.InjectionDeltaV, last.SatelliteDv);

        // With more stops to come the carrier goes back to the circular orbit too.
        var midway = LegTools.EvaluateDeploy(stop, new DeployStep(3), _bodies, continues: true)!;
        Assert.Equal(2 * last.EnterDv, midway.CarrierDv);
    }

    [Theory]
    [InlineData("kerbin.surface")]
    [InlineData("duna.intercept")]
    [InlineData("kerbin.soi")]
    public void Constellations_are_only_released_in_orbit(string key) =>
        Assert.False(LegTools.CanDeploy(_map[key], _bodies));

    [Fact]
    public void Deployment_counts_in_the_phase_it_is_charged_to()
    {
        var stops = new[] { "kerbin.surface", "kerbin.lo", _relayOrbit }.Select(k => _map[k]).ToList();
        var segments = MissionPlanner.BuildRoute(_map, stops);
        var (phases, settings) = MissionPlanner.DefaultPhases(segments, stops);
        var deploy = LegTools.EvaluateDeploy(_map[_relayOrbit], new DeployStep(3), _bodies, continues: false)!;

        var without = MissionPlanner.Summarize(phases, MissionPlanner.AllLegs(segments), settings);
        var with = MissionPlanner.Summarize(phases, MissionPlanner.AllLegs(segments), settings, [(1, deploy.CarrierDv)]);
        Assert.Equal(without[1].RequiredDv + deploy.CarrierDv, with[1].RequiredDv);
        Assert.Equal(without[0].RequiredDv, with[0].RequiredDv);
    }

    [Fact]
    public void Mission_with_a_window_and_a_deployment_reopens_with_both()
    {
        var definition = new MissionDefinition(
            [new("kerbin.surface"), new("kerbin.lo"), new(_relayOrbit, null, new TransferLeg(0, 4_987_091.8, 5_830_272), new DeployStep(3, PhaseIndex: 1))],
            [new("Launch"), new("Transfer")], []);
        var repo = new MissionRepository(_db);
        var reopened = repo.Get(repo.Add("Duna relays", definition))!.Definition;
        Assert.Equal(new DeployStep(3, PhaseIndex: 1), reopened.Stops[2].Deploy);
        Assert.Equal(definition.Stops[2].Transfer, reopened.Stops[2].Transfer);
    }

    [Fact]
    public void Missions_saved_before_leg_tools_open_without_them()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize<MissionDefinition>(
            """{"stops":[{"nodeKey":"kerbin.surface","note":null},{"nodeKey":"duna.lo","note":"Relays"}],"phases":[],"legs":[]}""",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.All(old.Stops, s => Assert.True(s.Transfer is null && s.Deploy is null));
    }
}
