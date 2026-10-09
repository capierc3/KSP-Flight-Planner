using KspFlightPlanner.Core;
using KspFlightPlanner.Data;
using Xunit.Abstractions;

namespace KspFlightPlanner.Tests;

public sealed class CustomLocationTests : IDisposable
{
    private const double KerbinMu = 3.5316e12;
    private readonly ITestOutputHelper _output;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly PlannerDatabase _db;

    public CustomLocationTests(ITestOutputHelper output)
    {
        _output = output;
        _db = new PlannerDatabase(_path);
        _db.Initialize();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    // Worked examples used in docs/MATH.md §2.1.
    [Theory]
    [InlineData(150, 55.1, 53.8)]
    [InlineData(500, 254.6, 225.6)]
    public void Hohmann_from_80km_kerbin_orbit(double stationKm, double dv1, double dv2)
    {
        var h = OrbitMath.Hohmann(KerbinMu, 680_000, 600_000 + stationKm * 1000);
        _output.WriteLine($"{stationKm} km: dv1 {h.Dv1:F2}  dv2 {h.Dv2:F2}  total {h.Total:F2}  time {h.TransferTimeS:F0} s");

        Assert.Equal(dv1, h.Dv1, 0.1);
        Assert.Equal(dv2, h.Dv2, 0.1);
    }

    [Fact]
    public void Hohmann_is_symmetric()
    {
        var up = OrbitMath.Hohmann(KerbinMu, 680_000, 1_100_000);
        var down = OrbitMath.Hohmann(KerbinMu, 1_100_000, 680_000);
        Assert.Equal(up.Total, down.Total, 1e-9);
        Assert.Equal(up.Dv1, down.Dv2, 1e-9);
    }

    [Fact]
    public void Hohmann_to_keostationary_is_close_to_the_map()
    {
        // The Δv map lists 1115 m/s from 80 km to keostationary (2,868 km); a pure Hohmann gives ≈ 1,099.
        var h = OrbitMath.Hohmann(KerbinMu, 680_000, 600_000 + 2_863_334);
        Assert.InRange(h.Total, 1_090, 1_120);
    }

    [Fact]
    public void Station_appears_on_the_map_under_kerbin_low_orbit()
    {
        new CustomLocationRepository(_db).Add("Station Alpha", "Kerbin", 150_000, "Crew hub");
        var map = new DvMapRepository(_db).GetMap()!;
        var station = map.Nodes.Single(n => n.IsCustom);

        Assert.Equal("Kerbin — Station Alpha (150 km)", station.DisplayName);
        Assert.Equal("kerbin.lo", map.ParentOf(station)!.Key);
        Assert.Equal(109, station.DvFromParent);       // 55 + 54
        Assert.Equal(54, station.AerobrakeResidualDv); // deorbit burn when re-entering
        Assert.True(station.AerobrakeUp);
    }

    [Fact]
    public void Mission_via_station_puts_the_rendezvous_in_the_launch_phase()
    {
        var id = new CustomLocationRepository(_db).Add("Station Alpha", "Kerbin", 150_000);
        var map = new DvMapRepository(_db).GetMap()!;
        var stops = new[] { map["kerbin.surface"], map[$"custom.{id}"], map["mun.surface"], map["kerbin.surface"] };
        var segments = MissionPlanner.BuildRoute(map, stops);
        var (phases, settings) = MissionPlanner.DefaultPhases(segments, stops);
        var summary = MissionPlanner.Summarize(phases, MissionPlanner.AllLegs(segments), settings);

        Assert.Equal(["Launch", "Transfer", "Lander"], phases.Select(p => p.Name));
        Assert.Equal(3400 + 109, summary[0].RequiredDv);        // launch + raise to station
        Assert.Equal(109 + 860 + 310 + 580, summary[1].RequiredDv); // station → LKO → Mun surface
        Assert.Equal(580 + 310, summary[2].RequiredDv);         // home by aerobraking
    }

    [Fact]
    public void Returning_from_a_station_by_aerobraking_still_needs_the_deorbit_burn()
    {
        var id = new CustomLocationRepository(_db).Add("High station", "Kerbin", 500_000);
        var map = new DvMapRepository(_db).GetMap()!;
        var legs = map.Route(map[$"custom.{id}"], map["kerbin.surface"]);
        var aero = legs.Select(_ => new LegSetting(0, true, false)).ToList();

        var total = MissionPlanner.Summarize([new("Home", 0)], legs, aero)[0].RequiredDv;
        Assert.Equal(226, total); // deorbit burn only; the atmosphere does the rest
    }

    [Fact]
    public void Locations_inside_the_atmosphere_are_left_off_the_map()
    {
        new CustomLocationRepository(_db).Add("Too low", "Kerbin", 50_000);
        Assert.DoesNotContain(new DvMapRepository(_db).GetMap()!.Nodes, n => n.IsCustom);
    }

    [Fact]
    public void Custom_edges_can_be_edited_and_deleting_cleans_up()
    {
        var locations = new CustomLocationRepository(_db);
        var maps = new DvMapRepository(_db);
        var id = locations.Add("Station Alpha", "Kerbin", 150_000);

        maps.SetOverride($"custom.{id}", 150);
        Assert.Equal(150, maps.GetMap()![$"custom.{id}"].DvFromParent);
        Assert.True(maps.GetMap()![$"custom.{id}"].IsOverridden);

        locations.Delete(id);
        Assert.DoesNotContain(maps.GetMap()!.Nodes, n => n.IsCustom);
        Assert.Empty(locations.GetAll());
    }

    [Fact]
    public void Sun_orbit_branches_from_kerbin_escape_not_low_kerbol_orbit()
    {
        var id = new CustomLocationRepository(_db).Add("Eye of Kerbol", "Kerbol", 10_000_000_000);
        var map = new DvMapRepository(_db).GetMap()!;
        var node = map[$"custom.{id}"];

        // Sibling of the Duna/Eve/Moho intercepts, not under Kerbol's 610 km low orbit.
        Assert.Equal("kerbin.soi", map.ParentOf(node)!.Key);
        Assert.Equal("Kerbol — Eye of Kerbol (10,000,000 km)", node.DisplayName);
        Assert.Equal("Eye of Kerbol", node.CustomName);           // map title is the location, not the body
        Assert.Equal("10,000,000 km", node.CustomOrbitText);
        Assert.Equal(787, node.DvFromParent);          // 64 extra ejection + 723 circularization (docs/MATH.md §3.5)
        Assert.Equal(723, node.AerobrakeResidualDv);   // coming home, Kerbin's atmosphere does the capture
        Assert.Equal("175 days", node.FlightTime);

        var route = map.Route(map["kerbin.surface"], node);
        Assert.Equal(["kerbin.lo", "kerbin.soi", node.Key], route.Select(l => l.To.Key));
        Assert.Equal(3400 + 950 + 787, route.Sum(l => l.DeltaV));
    }

    [Fact]
    public void Sun_orbit_at_dunas_distance_matches_the_maps_duna_intercept()
    {
        // At Duna's orbital radius the extra ejection beyond Kerbin escape should match the map's 130 m/s Duna edge.
        var map = new DvMapRepository(_db).GetMap()!;
        var bodies = new BodyRepository(_db).GetBodies(new BodyRepository(_db).GetSystems()[0].Id).ToDictionary(b => b.Name);
        var kerbol = bodies["Kerbol"];
        var atDuna = CustomLocation.Circular(1, "Duna-ish", "Kerbol", bodies["Duna"].Orbit!.SemiMajorAxisM - kerbol.EquatorialRadiusM);

        var node = CustomLocations.Place(atDuna, map, bodies);
        var extraEjection = node.DvFromParent - node.AerobrakeResidualDv;

        Assert.InRange(extraEjection, 115, 135);
    }

    [Fact]
    public void Interplanetary_branches_order_by_distance_from_kerbol()
    {
        new CustomLocationRepository(_db).Add("Eye of Kerbol", "Kerbol", 10_000_000_000);
        var map = new DvMapRepository(_db).GetMap()!;

        var order = map.ChildrenOf(map["kerbin.soi"])
            .OrderBy(n => n.OrbitRadiusM)
            .Select(n => n.IsCustom ? "custom" : n.BodyName);

        // Kerbol (sun-diving) first, then nearest to farthest; the 10.26 Gm orbit sits between Eve and Duna.
        Assert.Equal(["Kerbol", "Moho", "Eve", "custom", "Duna", "Dres", "Jool", "Eeloo"], order);
        Assert.Equal(5_263_138_304, map["moho.intercept"].BodyOrbitRadiusM);
    }

    [Fact]
    public void Low_orbit_and_stations_come_before_moons_and_escape_last()
    {
        new CustomLocationRepository(_db).Add("Station Alpha", "Kerbin", 150_000);
        var map = new DvMapRepository(_db).GetMap()!;

        var kerbin = map.ChildrenOf(map["kerbin.lo"]).OrderBy(n => n.OrbitRadiusM).Select(n => n.IsCustom ? "custom" : n.Key);
        Assert.Equal(["custom", "kerbin.keo", "mun.intercept", "minmus.intercept", "kerbin.soi"], kerbin);

        var jool = map.ChildrenOf(map["jool.elliptical"]).OrderBy(n => n.OrbitRadiusM).Select(n => n.Key);
        Assert.Equal(["jool.lo", "laythe.intercept", "vall.intercept", "tylo.intercept", "bop.intercept", "pol.intercept"], jool);
    }

    [Fact]
    public void Editing_a_location_keeps_its_key_and_clears_manual_edits()
    {
        var locations = new CustomLocationRepository(_db);
        var maps = new DvMapRepository(_db);
        var id = locations.Add("Eye of Kerbin", "Kerbol", 10_000_000_000);
        maps.SetOverride($"custom.{id}", 900);

        locations.Update(CustomLocation.Circular(id, "Station Alpha", "Kerbin", 500_000, "Crew hub"));

        var node = maps.GetMap()![$"custom.{id}"];
        Assert.Equal("Kerbin — Station Alpha (500 km)", node.DisplayName);
        Assert.Equal(480, node.DvFromParent); // recomputed Hohmann, the 900 edit is gone
        Assert.False(node.IsOverridden);
        Assert.Equal("Crew hub", Assert.Single(locations.GetAll()).Description);
    }

    [Fact]
    public void Bodies_for_custom_locations_include_kerbin_and_mun()
    {
        var bodies = new DvMapRepository(_db).GetCustomLocationBodies();
        Assert.Contains(bodies, b => b.Name == "Kerbin" && b.LowOrbit.AltitudeM == 80_000);
        Assert.Contains(bodies, b => b.Name == "Mun" && b.LowOrbit.AltitudeM == 14_000);
        Assert.Equal(17, bodies.Count);
    }
}
