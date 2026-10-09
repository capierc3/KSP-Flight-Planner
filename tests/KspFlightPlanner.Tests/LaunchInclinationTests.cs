using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.Tests;

/// <summary>Launching into an inclined orbit (docs/MATH.md §2.7) and the launch leg tool.</summary>
public sealed class LaunchInclinationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly PlannerDatabase _db;
    private readonly DeltaVMap _map;
    private readonly Dictionary<string, CelestialBody> _bodies;

    public LaunchInclinationTests()
    {
        _db = new PlannerDatabase(_path);
        _db.Initialize();
        _map = new DvMapRepository(_db).GetMap()!;
        var bodies = new BodyRepository(_db);
        _bodies = bodies.GetBodies(bodies.GetSystems().Single(s => s.Name == "KSP Stock").Id).ToDictionary(b => b.Name);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private LaunchPlan Kerbin(double inclinationDeg) => LaunchInclination.Plan(_bodies["Kerbin"], 80_000, inclinationDeg)!;

    [Fact]
    public void Polar_launch_matches_the_worked_example()
    {
        // §2.7: KSC Launch Pad (−0.1027°) into a polar 80 km Kerbin orbit.
        var plan = Kerbin(90);

        Assert.Equal("KSC Launch Pad", plan.SiteName);
        Assert.Equal(-0.1027, plan.SiteLatitudeDeg, 4);
        Assert.Equal(2_278.93, plan.OrbitSpeed, 2);
        Assert.Equal(174.94, plan.SurfaceSpeed, 2);
        Assert.Equal(0, plan.AzimuthDeg, 6);
        Assert.Equal(2_285.64, plan.RelativeSpeed, 2);
        Assert.Equal(181.6, plan.LaunchDv, 1);
        Assert.Equal(0, plan.TrimDv);
        Assert.Equal(355.61, plan.NorthHeadingDeg, 2);
        Assert.Equal(184.39, plan.SouthHeadingDeg, 2);
    }

    [Theory]
    [InlineData(28.5, 23, 1_122)]
    [InlineData(45, 55, 1_744)]
    [InlineData(90, 182, 3_223)]
    [InlineData(180, 350, 4_558)]
    public void Launching_into_the_plane_is_far_cheaper_than_turning_in_orbit(double inclination, double launchDv, double planeChangeDv)
    {
        var plan = Kerbin(inclination);
        Assert.Equal(launchDv, Math.Round(plan.LaunchDv));
        Assert.Equal(planeChangeDv, Math.Round(OrbitMath.PlaneChangeDv(plan.OrbitSpeed, inclination)));
    }

    [Fact]
    public void Headings_cover_prograde_retrograde_and_both_directions()
    {
        // 45°: north-east or south-east, a little north of the inertial azimuth because the spin is taken off (§4.5: 41.7°).
        var inclined = Kerbin(45);
        Assert.Equal(41.7, inclined.NorthHeadingDeg, 1);
        Assert.Equal(138.3, inclined.SouthHeadingDeg, 1);

        // 135° (retrograde): north-west or south-west.
        var retrograde = Kerbin(135);
        Assert.Equal(-45, retrograde.AzimuthDeg - 360, 1);
        Assert.InRange(retrograde.NorthHeadingDeg, 270, 360);
        Assert.InRange(retrograde.SouthHeadingDeg, 180, 270);
        Assert.True(retrograde.LaunchDv > Kerbin(90).LaunchDv);
    }

    [Fact]
    public void Inclinations_below_the_site_latitude_finish_with_a_plane_change()
    {
        // KSC sits 0.1027° south of the equator: due east gives 0.1027°, the last bit is turned in orbit.
        var equatorial = Kerbin(0);
        Assert.False(equatorial.IsReachable);
        Assert.Equal(0.1027, equatorial.FlownInclinationDeg, 4);
        Assert.Equal(0, equatorial.LaunchDv, 6);
        Assert.Equal(90, equatorial.NorthHeadingDeg, 6);
        Assert.Equal(4.08, equatorial.TrimDv, 2);

        var westward = Kerbin(180);
        Assert.Equal(180 - 0.1027, westward.FlownInclinationDeg, 4);
        Assert.Equal(270, westward.NorthHeadingDeg, 6);
        Assert.Equal("270° (due west)", westward.HeadingText);
        Assert.Equal("90° (due east)", equatorial.HeadingText);
        Assert.Equal("355.6° north or 184.4° south", Kerbin(90).HeadingText);
        Assert.Equal(4.08, westward.TrimDv, 2);

        Assert.True(Kerbin(0.2).IsReachable);
    }

    [Fact]
    public void Only_launches_from_a_launch_site_into_orbit_take_the_tool()
    {
        Assert.Equal(0, LegTools.FindLaunch(_map.Route(_map["kerbin.surface"], _map["kerbin.lo"]), _bodies));
        // Leaving Kerbin: a transfer, not an orbit to incline.
        Assert.Equal(-1, LegTools.FindLaunch(_map.Route(_map["kerbin.surface"], _map["mun.lo"]), _bodies));
        // The Mun has no launch site.
        Assert.Equal(-1, LegTools.FindLaunch(_map.Route(_map["mun.surface"], _map["mun.lo"]), _bodies));
        Assert.Null(LaunchInclination.Plan(_bodies["Mun"], 14_000, 90));
        // Coming down isn't a launch.
        Assert.Equal(-1, LegTools.FindLaunch(_map.Route(_map["kerbin.lo"], _map["kerbin.surface"]), _bodies));
    }

    [Fact]
    public void Polar_mission_adds_the_extra_to_the_launch_leg()
    {
        var segments = new[] { new RouteSegment(1, _map.Route(_map["kerbin.surface"], _map["kerbin.lo"])) };
        var stops = new MissionStop[] { new("kerbin.surface"), new("kerbin.lo", Launch: new LaunchLeg(90)) };

        var applied = LegTools.Apply(segments, stops, _bodies);

        var launch = applied[0].Legs[0];
        Assert.Equal(segments[0].Legs[0].DeltaV + 182, launch.DeltaV);
        Assert.Equal(segments[0].Legs[0].MapDeltaV, launch.MapDeltaV);
        Assert.Contains("heading 355.6° north or 184.4° south", launch.ToolNote);
    }

    [Fact]
    public void Plane_changes_after_the_launch_are_measured_from_its_inclination()
    {
        var kerbin = _bodies["Kerbin"];
        var polar = new CustomLocation(CustomLocation.MissionPlaceIdBase + 1, "Polar station", "Kerbin", 150_000, 150_000, 90);
        var tilted = new CustomLocation(CustomLocation.MissionPlaceIdBase + 2, "Tilted relay", "Kerbin", 150_000, 150_000, 100);
        var map = CustomLocations.WithPlaces(_map, [polar, tilted], _bodies);
        var places = new[] { polar, tilted }.ToDictionary(p => p.Id);
        CustomLocation? Location(long id) => places.GetValueOrDefault(id);

        var nodes = new[] { map["kerbin.surface"], map[polar.Key], map[tilted.Key] };
        var segments = MissionPlanner.BuildRoute(map, nodes);
        var mapPlaneChange = segments[0].Legs[^1].PlaneChangeDv;
        Assert.True(mapPlaneChange > 2_000); // the map charges the whole 90° turn
        var stops = new MissionStop[] { new(nodes[0].Key), new(polar.Key, Launch: new LaunchLeg(90)), new(tilted.Key) };

        var applied = LegTools.Apply(segments, stops, _bodies, Location);

        // Launched straight into the polar plane: no turn to reach the station.
        Assert.Equal(0, applied[0].Legs[^1].PlaneChangeDv);
        // On to the relay: only the 10° between the two planes.
        var v = OrbitMath.CircularVelocity(kerbin.Mu, kerbin.EquatorialRadiusM + 150_000);
        Assert.Equal(Math.Round(OrbitMath.PlaneChangeDv(v, 10)), applied[1].Legs[^1].PlaneChangeDv);

        // Launching straight to the station as one leg works the same way.
        var combined = MissionPlanner.CombineClimbs(segments, n => n.IsCustom);
        Assert.Single(combined[0].Legs);
        var appliedCombined = LegTools.Apply(combined, stops, _bodies, Location);
        Assert.Equal(combined[0].Legs[0].PlaneChangeDv - mapPlaneChange, appliedCombined[0].Legs[0].PlaneChangeDv);
        Assert.True(appliedCombined[0].Legs[0].IsFromTool);
    }
}
