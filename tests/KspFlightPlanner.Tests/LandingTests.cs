using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.Tests;

/// <summary>Landing at a chosen spot (docs/MATH.md §2.6) and where the spots come from.</summary>
public sealed class LandingTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly PlannerDatabase _db;
    private readonly DeltaVMap _map;
    private readonly Dictionary<string, CelestialBody> _bodies;

    public LandingTests()
    {
        _db = new PlannerDatabase(_path);
        _db.Initialize();
        _map = new DvMapRepository(_db).GetMap()!;
        var bodies = new BodyRepository(_db);
        _bodies = bodies.GetBodies(bodies.GetSystems().Single(s => s.Name == "KSP Stock").Id).ToDictionary(b => b.Name);
    }

    public void Dispose()
    {
        TestDb.Release(_path);
        File.Delete(_path);
    }

    private static readonly LandingTarget KerbolArch = new("Kerbol arch", "Landmark", 12.4508, 39.1959);

    [Fact]
    public void Mun_landing_matches_the_worked_example()
    {
        // §2.6: 30 km orbit inclined 15°, periapsis 3 km above the datum (ground 2 km + 1 km clearance), a_T = 5 m/s².
        var mun = _bodies["Mun"];
        var leg = new LandingLeg(KerbolArch with { GroundHeightM = 2_000 }, LanderTwr: 5 / mun.SurfaceGravity);
        var plan = Landing.Plan(mun, 30_000, leg, inclinationDeg: 15);

        Assert.Equal(56.41, plan.ArgumentOfLatitudeDeg, 2);
        Assert.Equal(2_715.5, plan.OrbitPeriodS, 1);
        Assert.Equal(7.03, plan.TrackShiftPerOrbitDeg, 2);
        Assert.Equal(3_000, plan.PeriapsisAltitudeM);
        Assert.Equal(16.86, plan.DeorbitDv, 2);
        Assert.Equal(1_240, plan.DescentTimeS, 0);
        Assert.Equal(3.21, plan.DriftDeg, 2);
        Assert.Equal(574.99, plan.RelativeSpeed, 2);
        Assert.Equal(121.2, plan.BrakingTimeS, 1);
        Assert.Equal(34_849, plan.BrakingDistanceM, 0);
        Assert.Equal(606.08, plan.BrakingDv, 2);
        Assert.Equal(623, plan.TotalDv, 0);
        Assert.False(plan.IsRoughEstimate);
        // 3 km periapsis is below the Mun's 7,061 m peak: the plan says to check the approach.
        Assert.Contains(plan.Warnings, w => w.Contains("highest peak"));
    }

    [Fact]
    public void Default_plan_inclines_one_degree_past_the_latitude_and_flags_unknown_ground()
    {
        var plan = Landing.Plan(_bodies["Mun"], 14_000, new LandingLeg(KerbolArch));
        Assert.Equal(12.4508, plan.MinInclinationDeg);
        Assert.Equal(13.4508, plan.InclinationDeg, 9);
        Assert.Equal(1_000, plan.PeriapsisAltitudeM);
        Assert.True(plan.PlaneChangeDv > 0);
        Assert.Contains(plan.Warnings, w => w.Contains("ground height"));
    }

    [Fact]
    public void Landing_on_a_body_with_air_is_a_rough_estimate()
    {
        var plan = Landing.Plan(_bodies["Duna"], 60_000, new LandingLeg(new LandingTarget("Face", "Landmark", 17.05, -85.4667)));
        Assert.True(plan.IsRoughEstimate);
        Assert.StartsWith("Duna has air", plan.Warnings[0]);
    }

    [Fact]
    public void Landing_tool_replaces_the_mun_landing_value_but_keeps_dunas()
    {
        var mun = _map.Route(_map["mun.lo"], _map["mun.surface"]);
        Assert.Equal(0, LegTools.FindLanding(mun));
        var plan = LegTools.EvaluateLanding(mun, new LandingLeg(KerbolArch), _bodies)!;
        Assert.Equal(14_000, plan.OrbitAltitudeM);
        var applied = LegTools.ApplyLanding(mun, plan);
        Assert.Equal(Math.Round(plan.TotalDv), applied[0].DeltaV);
        Assert.Equal(580, applied[0].MapDeltaV);
        Assert.StartsWith("Landing at Kerbol arch", applied[0].ToolNote);

        var duna = _map.Route(_map["duna.lo"], _map["duna.surface"]);
        var dunaPlan = LegTools.EvaluateLanding(duna, new LandingLeg(new LandingTarget("Face", "Landmark", 17.05, -85.4667)), _bodies)!;
        var dunaApplied = LegTools.ApplyLanding(duna, dunaPlan);
        Assert.Equal(duna[0].DeltaV, dunaApplied[0].DeltaV);
        Assert.Contains("rough estimate", dunaApplied[0].ToolNote);

        // Going up from the surface isn't a landing.
        Assert.Equal(-1, LegTools.FindLanding(_map.Route(_map["mun.surface"], _map["mun.lo"])));
    }

    [Fact]
    public void Mission_stop_keeps_its_landing_through_the_route()
    {
        var segments = new[] { new RouteSegment(1, _map.Route(_map["mun.lo"], _map["mun.surface"])) };
        var stops = new MissionStop[] { new("mun.lo"), new("mun.surface", Landing: new LandingLeg(KerbolArch)) };
        var applied = LegTools.Apply(segments, stops, _bodies);
        Assert.True(applied[0].Legs[0].IsFromTool);
    }

    [Fact]
    public void Targets_come_from_landmarks_flags_landed_craft_and_waypoints()
    {
        var save = KspSaveReader.Parse(string.Join("\n",
            "GAME", "{", "Title = Test", "FLIGHTSTATE", "{", "UT = 100",
            "VESSEL", "{", "pid = f1", "name = New Horizons 1", "type = Flag", "sit = LANDED",
            "lat = 20.106061381680327", "lon = 45.202082192956489", "alt = 2605.3179262414051",
            "ORBIT", "{", "SMA = NaN", "REF = 2", "}", "}",
            "VESSEL", "{", "pid = r1", "name = Rover", "type = Rover", "sit = LANDED", "lat = -1", "lon = 2", "alt = 300",
            "ORBIT", "{", "REF = 3", "}", "}",
            "VESSEL", "{", "pid = s1", "name = Orbiter", "type = Probe", "sit = ORBITING", "lat = 5", "lon = 6", "alt = 30000",
            "ORBIT", "{", "SMA = 230000", "REF = 2", "}", "}",
            "}",
            "SCENARIO", "{", "name = ScenarioCustomWaypoints",
            "WAYPOINT", "{", "name = Twin Craters LZ", "celestialName = Mun", "latitude = 8.5", "longitude = 79.25", "altitude = 0", "}",
            "WAYPOINT", "{", "name = Minmus flats", "celestialName = Minmus", "latitude = 1", "longitude = 2", "}",
            "}", "}"));

        var targets = Landing.Targets(_bodies["Mun"], save);

        Assert.Equal(_bodies["Mun"].Landmarks.Count, targets.Count(t => t.Source == "Landmark"));
        var flag = Assert.Single(targets, t => t.Source == "Flag");
        Assert.Equal(("New Horizons 1", 20.106061381680327, 45.202082192956489), (flag.Name, flag.LatitudeDeg, flag.LongitudeDeg));
        Assert.Equal(2605.3179262414051, flag.GroundHeightM);
        var waypoint = Assert.Single(targets, t => t.Source == "KerbNet waypoint");
        Assert.Equal(("Twin Craters LZ", 8.5, 79.25), (waypoint.Name, waypoint.LatitudeDeg, waypoint.LongitudeDeg));
        Assert.DoesNotContain(targets, t => t.Name is "Orbiter" or "Rover" or "Minmus flats");
        Assert.Single(Landing.Targets(_bodies["Minmus"], save), t => t.Name == "Rover" && t.Source == "Landed craft");
    }

    [Fact]
    public void Surface_distance_matches_the_haversine_example()
    {
        // §1.9: Armstrong memorial → Kerbol arch on the Mun.
        Assert.Equal(70_169, Landing.SurfaceDistance(200_000, 0.7017, 22.7460, 12.4508, 39.1959), 0);
    }
}
