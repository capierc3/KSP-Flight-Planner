using KspFlightPlanner.Core;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.Tests;

public sealed class MissionPlannerTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly PlannerDatabase _db;
    private readonly DvMapRepository _maps;
    private readonly DeltaVMap _map;

    public MissionPlannerTests()
    {
        _db = new PlannerDatabase(_path);
        _db.Initialize();
        _maps = new DvMapRepository(_db);
        _map = _maps.GetMap()!;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private DvMapNode[] Stops(params string[] keys) => keys.Select(k => _map[k]).ToArray();

    private (IReadOnlyList<PhaseDefinition> Phases, IReadOnlyList<PhaseSummary> Summary) Plan(DeltaVMap map, params string[] keys)
    {
        var stops = keys.Select(k => map[k]).ToArray();
        var segments = MissionPlanner.BuildRoute(map, stops);
        var (phases, settings) = MissionPlanner.DefaultPhases(segments, stops);
        return (phases, MissionPlanner.Summarize(phases, MissionPlanner.AllLegs(segments), settings));
    }

    // Totals printed on the Δv map under each destination (Kerbin surface → destination, no plane changes).
    [Theory]
    [InlineData("mun.surface", 5150)]
    [InlineData("minmus.surface", 4670)]
    [InlineData("kerbin.keo", 4515)]
    [InlineData("kerbol.surface", 91050)]
    [InlineData("moho.surface", 8390)]
    [InlineData("eve.surface", 15850)]
    [InlineData("gilly.surface", 5020)]
    [InlineData("duna.surface", 6540)]
    [InlineData("ike.surface", 5330)]
    [InlineData("dres.surface", 6680)]
    [InlineData("jool.surface", 22300)]
    [InlineData("laythe.surface", 10390)]
    [InlineData("vall.surface", 7880)]
    [InlineData("tylo.surface", 9260)]
    [InlineData("bop.surface", 6830)]
    [InlineData("pol.surface", 6600)]
    [InlineData("eeloo.surface", 7480)]
    public void Route_from_kerbin_matches_map_totals(string destination, double expected)
    {
        var legs = _map.Route(_map["kerbin.surface"], _map[destination]);
        Assert.Equal(expected, legs.Sum(l => l.DeltaV));
    }

    [Fact]
    public void Mun_lander_mission_splits_into_launch_transfer_and_lander()
    {
        var (phases, summary) = Plan(_map, "kerbin.surface", "mun.surface", "kerbin.surface");

        Assert.Equal(["Launch", "Transfer", "Lander"], phases.Select(p => p.Name));
        Assert.Equal(3400, summary[0].RequiredDv);            // Kerbin surface → LKO
        Assert.Equal(860 + 310 + 580, summary[1].RequiredDv);  // intercept, Mun orbit, landing
        Assert.Equal(580 + 310, summary[2].RequiredDv);        // ascent + escape; 860 and 3400 home are aerobraked
    }

    [Fact]
    public void Station_stop_before_mun_base_keeps_the_same_phases()
    {
        // Launch, dock at a station in LKO, fly to the Mun base, come home.
        var stops = Stops("kerbin.surface", "kerbin.lo", "mun.surface", "kerbin.surface");
        var segments = MissionPlanner.BuildRoute(_map, stops);
        var (phases, settings) = MissionPlanner.DefaultPhases(segments, stops);
        var summary = MissionPlanner.Summarize(phases, MissionPlanner.AllLegs(segments), settings);

        Assert.Equal([1, 3, 4], segments.Select(s => s.Legs.Count));
        Assert.Equal(["Launch", "Transfer", "Lander"], phases.Select(p => p.Name));
        Assert.Equal([3400, 1750, 890], summary.Select(s => s.RequiredDv));
    }

    [Fact]
    public void Reordering_stops_changes_the_route()
    {
        // Station visit moved after the Mun instead of before.
        var stops = Stops("kerbin.surface", "mun.surface", "kerbin.lo", "kerbin.surface");
        var segments = MissionPlanner.BuildRoute(_map, stops);

        Assert.Equal([4, 3, 1], segments.Select(s => s.Legs.Count));
        Assert.Equal("kerbin.lo", segments[1].Legs[^1].To.Key);
        Assert.Equal("kerbin.surface", segments[2].Legs[^1].To.Key);
    }

    [Fact]
    public void Orbit_and_return_gets_a_return_phase()
    {
        var (phases, summary) = Plan(_map, "kerbin.surface", "mun.lo", "kerbin.surface");
        Assert.Equal(["Launch", "Transfer", "Return"], phases.Select(p => p.Name));
        Assert.Equal(310, summary[2].RequiredDv); // leave Mun orbit; the rest is aerobraked
    }

    [Fact]
    public void Ending_in_orbit_of_the_start_body_still_counts_as_a_return()
    {
        var (phases, _) = Plan(_map, "kerbin.surface", "mun.lo", "kerbin.lo");
        Assert.Equal(["Launch", "Transfer", "Return"], phases.Select(p => p.Name));
    }

    [Theory]
    [InlineData("kerbin.lo")]   // park in low orbit first
    [InlineData("kerbin.keo")]  // stop at a station higher up
    public void One_way_trip_through_a_stop_is_launch_and_transfer(string stop)
    {
        var (phases, summary) = Plan(_map, "kerbin.surface", stop, "duna.lo");
        Assert.Equal(["Launch", "Transfer"], phases.Select(p => p.Name));
        Assert.All(summary, s => Assert.True(s.RequiredDv > 0));
    }

    [Fact]
    public void One_way_landing_through_a_stop_keeps_the_descent_in_the_lander()
    {
        var stops = Stops("kerbin.surface", "kerbin.lo", "mun.surface");
        var segments = MissionPlanner.BuildRoute(_map, stops);
        var (phases, settings) = MissionPlanner.DefaultPhases(segments, stops);
        var summary = MissionPlanner.Summarize(phases, MissionPlanner.AllLegs(segments), settings);

        Assert.Equal(["Launch", "Transfer", "Lander"], phases.Select(p => p.Name));
        Assert.Equal([3400, 860 + 310, 580], summary.Select(s => s.RequiredDv));
    }

    [Fact]
    public void One_way_landing_gets_its_own_lander_phase()
    {
        var (phases, summary) = Plan(_map, "kerbin.surface", "duna.surface");
        Assert.Equal(["Launch", "Transfer", "Lander"], phases.Select(p => p.Name));
        Assert.Equal(0, summary[2].RequiredDv); // Duna landing is aerobraked by default
    }

    [Fact]
    public void Editing_the_mun_landing_changes_both_descent_and_ascent()
    {
        _maps.SetOverride("mun.surface", 700);
        var edited = _maps.GetMap()!;

        Assert.True(edited["mun.surface"].IsOverridden);
        Assert.Equal(580, edited["mun.surface"].MapDvFromParent);

        var (_, summary) = Plan(edited, "kerbin.surface", "mun.surface", "kerbin.surface");
        Assert.Equal(860 + 310 + 700, summary[1].RequiredDv);
        Assert.Equal(700 + 310, summary[2].RequiredDv);

        _maps.SetOverride("mun.surface", null);
        Assert.False(_maps.GetMap()!["mun.surface"].IsOverridden);
    }

    [Fact]
    public void WithOverrides_applies_in_memory()
    {
        var edited = _map.WithOverrides(new Dictionary<string, double> { ["kerbin.lo"] = 3600 });
        Assert.Equal(3600, edited.Route(edited["kerbin.surface"], edited["kerbin.lo"]).Single().DeltaV);
        Assert.Equal(3400, _map["kerbin.lo"].DvFromParent);
    }

    [Fact]
    public void Disabling_aerobraking_and_adding_plane_changes_costs_more()
    {
        var legs = _map.Route(_map["minmus.surface"], _map["kerbin.surface"]);
        var aero = legs.Select(_ => new LegSetting(0, UseAerobrake: true, IncludePlaneChange: false)).ToList();
        var noAero = legs.Select(_ => new LegSetting(0, UseAerobrake: false, IncludePlaneChange: true)).ToList();
        PhaseDefinition[] one = [new("All", MarginPercent: 0)];

        Assert.Equal(180 + 160, MissionPlanner.Summarize(one, legs, aero)[0].RequiredDv);
        Assert.Equal(180 + 160 + 930 + 340 + 3400, MissionPlanner.Summarize(one, legs, noAero)[0].RequiredDv);
    }

    [Fact]
    public void Margins_add_percentage_and_extra()
    {
        var legs = _map.Route(_map["mun.lo"], _map["mun.surface"]);
        PhaseDefinition[] phases = [new("Lander", MarginPercent: 10, ExtraDv: 100)];
        var s = MissionPlanner.Summarize(phases, legs, [new LegSetting(0, true, false)])[0];

        Assert.Equal(580, s.RequiredDv);
        Assert.Equal(58 + 100, s.MarginDv);
        Assert.Equal(738, s.TotalDv);
    }

    [Fact]
    public void Route_between_moons_goes_through_common_parent()
    {
        var legs = _map.Route(_map["ike.lo"], _map["duna.lo"]);
        Assert.Equal(["ike.intercept", "duna.elliptical", "duna.lo"], legs.Select(l => l.To.Key));
        Assert.Equal(180 + 30 + 360, legs.Sum(l => l.DeltaV));
        Assert.True(legs[2].CanAerobrake); // Duna elliptical → low orbit
    }

    [Fact]
    public void Missions_round_trip_through_the_database()
    {
        var repo = new MissionRepository(_db);
        var definition = new MissionDefinition(
            [new("kerbin.surface"), new("kerbin.lo", "Dock at station, pick up Bob"), new("mun.surface", "Moon base"), new("kerbin.surface")],
            [new("Launch"), new("Transfer", 5), new("Lander", 10, 100)],
            [new(0, true, false), new(1, true, true)]);

        var id = repo.Add("Crew rotation", definition);
        var saved = Assert.Single(repo.GetAll());

        Assert.Equal(id, saved.Id);
        Assert.Equal(4, saved.Definition.Stops.Count);
        Assert.Equal("Dock at station, pick up Bob", saved.Definition.Stops[1].Note);
        Assert.Equal(100, saved.Definition.Phases[2].ExtraDv);
        Assert.True(saved.Definition.Legs[1].IncludePlaneChange);

        repo.Delete(id);
        Assert.Empty(repo.GetAll());
    }
}
