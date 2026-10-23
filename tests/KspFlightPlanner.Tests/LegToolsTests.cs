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
        TestDb.Release(_path);
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
        Assert.Equal(["Launch", "Transfer"], phases.Select(p => p.Name));
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

    /// <summary>A placed station (150 km, 5°) and relay (400 km) around Kerbin, and a planned base, to meet.</summary>
    private (DeltaVMap Map, CustomLocation Station, CustomLocation Relay, CustomLocation Planned) KerbinCraft()
    {
        var repo = new CustomLocationRepository(_db);
        var station = repo.Get(repo.Add(new CustomLocation(0, "Station", "Kerbin", 150_000, 150_000, 5,
            IsPlaced: true, MeanAnomalyDeg: 90)))!;
        var relay = repo.Get(repo.Add(new CustomLocation(0, "Relay", "Kerbin", 400_000, 400_000, IsPlaced: true, MeanAnomalyDeg: 200)))!;
        var planned = repo.Get(repo.Add("Planned", "Kerbin", 250_000))!;
        return (new DvMapRepository(_db).GetMap()!, station, relay, planned);
    }

    [Fact]
    public void Launching_to_a_placed_station_is_a_rendezvous_after_the_climb_to_orbit()
    {
        var (map, station, relay, planned) = KerbinCraft();
        var placed = new CustomLocationRepository(_db).GetAll().Where(l => l.IsPlaced).Select(l => l.Id).ToHashSet();
        RendezvousSpan? Find(string from, string to) => LegTools.FindRendezvous(map.Route(map[from], map[to]), map[from], map[to], placed.Contains);

        var launch = Find("kerbin.surface", station.Key)!;
        Assert.True(launch.IsLaunch);
        Assert.Equal((station.Id, 1), (launch.TargetLocationId, launch.Start)); // surface → LKO stays the map's

        var hop = Find(station.Key, relay.Key)!;
        Assert.Equal((relay.Id, station.Id, 0), (hop.TargetLocationId, hop.ChaserLocationId!.Value, hop.Start));

        Assert.Null(Find("kerbin.surface", planned.Key)); // not placed: nothing to meet yet
    }

    [Fact]
    public void From_any_other_orbit_the_rendezvous_starts_in_the_target_bodys_low_orbit()
    {
        var (map, station, _, _) = KerbinCraft();
        RendezvousSpan? Find(string from) =>
            LegTools.FindRendezvous(map.Route(map[from], map[station.Key]), map[from], map[station.Key], _ => true);

        // Already in LKO: the whole leg is the rendezvous, from anywhere on the 80 km orbit.
        var fromLko = Find("kerbin.lo")!;
        Assert.True(fromLko.FromParking && !fromLko.IsLaunch);
        Assert.Equal((0, (long?)null), (fromLko.Start, fromLko.ChaserLocationId));

        // Coming home from the Mun: the map flies Mun orbit → Kerbin orbit, then the rendezvous takes over.
        var legs = map.Route(map["mun.lo"], map[station.Key]);
        var fromMun = Find("mun.lo")!;
        Assert.True(fromMun.FromParking);
        Assert.Equal(legs.Count - 1, fromMun.Start);
        Assert.Equal(("Kerbin", "LowOrbit"), (legs[fromMun.Start].From.BodyName, legs[fromMun.Start].From.Kind));
    }

    [Fact]
    public void A_mission_keeps_its_own_places_off_the_locations_tab()
    {
        var kerbin = _bodies["Kerbin"];
        var orbit = new OrbitalElements(kerbin.EquatorialRadiusM + 2_870_000, 0.002, 1.2, 350, 20, 4.0, 12_345);
        var relay = CustomLocationOrbits.FromElements(CustomLocation.MissionPlaceIdBase + 1, "KerbLink Mk2 A", kerbin, orbit) with
        {
            KspSave = "Protogen", KspVesselId = "abc",
        };
        var back = CustomLocationOrbits.ToElements(relay, kerbin);
        Assert.Equal(orbit.SemiMajorAxisM, back.SemiMajorAxisM, 3);
        Assert.Equal(orbit.Eccentricity, back.Eccentricity, 9);
        Assert.Equal(orbit.MeanAnomalyAtEpochRad, back.MeanAnomalyAtEpochRad, 9);
        Assert.Equal(orbit.EpochS, back.EpochS);

        Assert.False(_map.TryGet(relay.Key, out _));
        var map = CustomLocations.WithPlaces(_map, [relay], _bodies);
        Assert.Equal("kerbin.lo", map.ParentOf(map[relay.Key])!.Key);
        Assert.NotNull(LegTools.FindRendezvous(map.Route(map["kerbin.surface"], map[relay.Key]), map["kerbin.surface"], map[relay.Key],
            id => id == relay.Id));

        var definition = new MissionDefinition([new("kerbin.surface"), new(relay.Key)], [new("Launch")], [], Places: [relay]);
        var repo = new MissionRepository(_db);
        Assert.Equal(relay, repo.Get(repo.Add("Relay repair", definition))!.Definition.Places!.Single());
        Assert.DoesNotContain(new CustomLocationRepository(_db).GetAll(), l => l.Name == relay.Name);
    }

    [Fact]
    public void Rendezvous_leg_carries_the_planned_burns_in_place_of_the_map()
    {
        var (map, station, _, _) = KerbinCraft();
        var kerbin = _bodies["Kerbin"];
        var target = new RendezvousTarget("Station", CustomLocationOrbits.ToElements(station, kerbin, kerbin, _bodies["Kerbol"]));
        var plan = Rendezvous.Plan(new RendezvousRequest(kerbin, [target], 0, 0, Site: LaunchSite.Ksc, Ascent: new AscentProfile(80_000)));
        var leg = new RendezvousLeg(station.Id, 0, plan.Legs[0].ArrivalUt, plan.Legs[0].TotalDv, plan.LaunchIndex);

        var legs = map.Route(map["kerbin.surface"], map[station.Key]);
        var span = LegTools.FindRendezvous(legs, map["kerbin.surface"], map[station.Key], _ => true)!;
        var applied = LegTools.ApplyRendezvous(legs, span, leg);

        Assert.Equal(legs[0], applied[0]);
        Assert.Equal(Math.Round(plan.Legs[0].TotalDv), applied[1].DeltaV);
        Assert.True(applied[1].IsFromTool && applied[1].PlaneChangeDv == 0);
        // Launching straight into the station's plane: a few hundred m/s at most to catch it, close to the map's Hohmann.
        Assert.InRange(applied[1].DeltaV, 50, 400);
    }

    [Fact]
    public void Back_to_back_rendezvous_legs_add_up_to_the_plan_that_meets_both()
    {
        var (map, station, relay, _) = KerbinCraft();
        var kerbin = _bodies["Kerbin"];
        RendezvousTarget Target(CustomLocation l) => new(l.Name, CustomLocationOrbits.ToElements(l, kerbin, kerbin, _bodies["Kerbol"]));
        var both = Rendezvous.Plan(new RendezvousRequest(kerbin, [Target(station), Target(relay)], 0, 0, Site: LaunchSite.Ksc,
            Ascent: new AscentProfile(80_000)));

        // The second leg planned on its own, from the station once the mission gets there, is the same leg.
        var second = Rendezvous.Plan(new RendezvousRequest(kerbin, [Target(relay)], both.Legs[0].ArrivalUt + Rendezvous.BurnGapS, 0,
            Chaser: Target(station))).Legs[0];
        Assert.Equal(both.Legs[1].TotalDv, second.TotalDv, 6);
        Assert.Equal(both.Legs[1].ArrivalUt, second.ArrivalUt, 6);

        // In the mission: station → relay is one leg (the map's way through the low orbit isn't a stop), all rendezvous.
        var placed = new[] { station.Id, relay.Id }.ToHashSet();
        var stops = new[] { map["kerbin.surface"], map[station.Key], map[relay.Key] };
        var segments = MissionPlanner.CombineCraftHops(MissionPlanner.BuildRoute(map, stops), n => n.CustomLocationId is { } id && placed.Contains(id));
        Assert.Equal(2, segments[0].Legs.Count); // the launch, then the rendezvous
        var hop = Assert.Single(segments[1].Legs);
        Assert.Equal((station.Key, relay.Key), (hop.From.Key, hop.To.Key));
        Assert.Equal([station.Key, relay.Key], hop.EdgeKeys); // both map edges highlighted
        Assert.Equal(MissionPlanner.BuildRoute(map, stops)[1].Legs.Sum(l => l.DeltaV), hop.DeltaV);

        var span = LegTools.FindRendezvous(segments[1].Legs, map[station.Key], map[relay.Key], placed.Contains)!;
        var leg = new RendezvousLeg(relay.Id, second.StartUt, second.ArrivalUt, second.TotalDv);
        var applied = Assert.Single(LegTools.ApplyRendezvous(segments[1].Legs, span, leg, both.Legs[0].ArrivalUt));
        Assert.Equal(Math.Round(second.TotalDv), applied.DeltaV);
        Assert.StartsWith("Rendezvous burns as planned", applied.ToolNote);
    }

    [Fact]
    public void A_rendezvous_planned_to_start_before_the_mission_gets_there_says_so()
    {
        var (map, station, relay, _) = KerbinCraft();
        var legs = map.Route(map[station.Key], map[relay.Key]);
        var span = LegTools.FindRendezvous(legs, map[station.Key], map[relay.Key], _ => true)!;
        var leg = new RendezvousLeg(relay.Id, StartUt: 1_000, ArrivalUt: 9_000, OrbitalDv: 55);

        Assert.False(leg.StartsBefore(null));
        Assert.False(leg.StartsBefore(1_000));
        Assert.True(leg.StartsBefore(5_000)); // the leg before now arrives later (planned again since)
        Assert.Contains("before the mission gets here", LegTools.ApplyRendezvous(legs, span, leg, 5_000)[0].ToolNote);
        Assert.Equal(55, LegTools.ApplyRendezvous(legs, span, leg, 5_000)[0].DeltaV);
    }

    [Fact]
    public void Rendezvous_picks_survive_a_save()
    {
        var leg = new RendezvousLeg(7, 1_000, 9_000, 123.4, 2, new PhasingChoice(3, true), new InterceptPoint(5_000, 1_800));
        var definition = new MissionDefinition([new("kerbin.surface"), new("custom.7", Rendezvous: leg)], [new("Launch")], []);
        var repo = new MissionRepository(_db);
        Assert.Equal(leg, repo.Get(repo.Add("Station visit", definition))!.Definition.Stops[1].Rendezvous);
    }

    [Fact]
    public void Craft_around_the_mun_reaches_a_kerbin_rendezvous_by_the_map_steps_home()
    {
        var approach = LegTools.FindApproach(_map, "Mun", "Kerbin", _bodies)!;

        Assert.Equal(["mun.lo", "mun.intercept", "kerbin.lo"], approach.Steps.Select(s => s.From.Key).Append(approach.Steps[^1].To.Key));
        Assert.Equal(310 + 860, approach.DeltaV);
        Assert.Equal((10_000, 80_000), (approach.FromAltitudeM, approach.ParkingAltitudeM));
        // Mun → Kerbin: a Hohmann half-orbit from the Mun's 12,000 km orbit down to 680 km (docs/MATH.md §4.6).
        Assert.Equal(26_686.9, approach.TripTimeS!.Value, 0.1);
    }

    [Fact]
    public void Trip_time_counts_each_moon_step_and_leaves_out_interplanetary_ones()
    {
        // Mun → Minmus goes through Kerbin's low orbit: two half-orbits, out from the Mun and up to Minmus.
        var mun = OrbitMath.Hohmann(_bodies["Kerbin"].Mu, 12_000_000, 680_000).TransferTimeS;
        var minmus = OrbitMath.Hohmann(_bodies["Kerbin"].Mu, 47_000_000, 680_000).TransferTimeS;
        Assert.Equal(mun + minmus, LegTools.FindApproach(_map, "Mun", "Minmus", _bodies)!.TripTimeS!.Value, 1e-6);
        // Kerbin → Duna waits for a window.
        var duna = LegTools.FindApproach(_map, "Kerbin", "Duna", _bodies)!;
        Assert.Null(duna.TripTimeS);
        Assert.Equal(60_000, duna.ParkingAltitudeM);
        Assert.Null(LegTools.FindApproach(_map, "Kerbin", "Kerbin", _bodies));
    }

    [Fact]
    public void Missions_saved_before_leg_tools_open_without_them()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize<MissionDefinition>(
            """{"stops":[{"nodeKey":"kerbin.surface","note":null},{"nodeKey":"duna.lo","note":"Relays"}],"phases":[],"legs":[]}""",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.All(old.Stops, s => Assert.True(s.Transfer is null && s.Deploy is null && s.Rendezvous is null));
    }

    [Fact]
    public void Arriving_at_the_mun_in_a_polar_orbit_adds_a_small_burn_on_the_way_and_survives_a_save()
    {
        var legs = Route("kerbin.lo", "mun.lo");
        var index = LegTools.FindArrival(legs, _bodies);
        Assert.Equal("Kerbin", legs[index].From.BodyName);
        Assert.Equal("Mun", legs[index].To.BodyName);

        var plan = LegTools.EvaluateArrival(legs, new ArrivalLeg(90), _bodies)!;
        Assert.False(plan.IsEstimate);
        // Planned with Hops: about 37 m/s, where turning a low Mun orbit to polar costs about 770.
        Assert.InRange(plan.AimDv!.Value, 15, 80);
        var applied = LegTools.ApplyArrival(legs, plan);
        Assert.Equal(legs[index].DeltaV + Math.Round(plan.AimDv.Value), applied[index].DeltaV);
        Assert.Contains("inclined 90°", applied[index].ToolNote);

        var stops = new[] { "kerbin.surface", "kerbin.lo", "mun.lo" }.Select(k => _map[k]).ToList();
        var segments = MissionPlanner.BuildRoute(_map, stops);
        var (phases, settings) = MissionPlanner.DefaultPhases(segments, stops);
        var definition = new MissionDefinition([new("kerbin.surface"), new("kerbin.lo"), new("mun.lo", Arrival: new ArrivalLeg(90))], phases, settings);
        var repo = new MissionRepository(_db);
        var reopened = repo.Get(repo.Add("Polar Mun", definition))!.Definition;
        Assert.Equal(new ArrivalLeg(90), reopened.Stops[2].Arrival);
        var total = LegTools.Apply(segments, reopened.Stops, _bodies)[1].Legs.Sum(l => l.DeltaV);
        Assert.Equal(segments[1].Legs.Sum(l => l.DeltaV) + Math.Round(plan.AimDv.Value), total);
    }

    [Fact]
    public void A_leg_that_stays_around_one_body_or_lands_has_no_arrival_tool()
    {
        Assert.Equal(-1, LegTools.FindArrival(Route("kerbin.surface", "kerbin.lo"), _bodies));
        Assert.Equal(-1, LegTools.FindArrival(Route("kerbin.lo", "mun.surface"), _bodies));
        Assert.Equal(-1, LegTools.FindArrival(Route("kerbin.lo", "mun.intercept"), _bodies));
    }

    [Fact]
    public void Arriving_at_duna_in_a_polar_orbit_is_a_few_m_s_by_the_rule_of_thumb()
    {
        var legs = Route("kerbin.lo", "duna.lo");
        var plan = LegTools.EvaluateArrival(legs, new ArrivalLeg(90), _bodies)!;
        Assert.True(plan.IsEstimate);
        Assert.Equal("Duna", legs[plan.Leg].To.BodyName);
        Assert.InRange(plan.AimDv!.Value, 0.1, 5);
    }

    [Fact]
    public void From_the_mun_to_a_polar_kerbin_orbit_is_planned_with_hops()
    {
        var legs = Route("mun.lo", "kerbin.lo");
        var plan = LegTools.EvaluateArrival(legs, new ArrivalLeg(90), _bodies)!;
        Assert.Equal("Mun", legs[plan.Leg].From.BodyName);
        Assert.InRange(plan.AimDv!.Value, 100, 400);
    }
}
