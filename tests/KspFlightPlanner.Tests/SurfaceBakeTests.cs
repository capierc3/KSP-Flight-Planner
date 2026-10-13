using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.Tests;

/// <summary>Surface bakes: the grid, reading heights between points, storage, and their use in landings.</summary>
public sealed class SurfaceBakeTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly PlannerDatabase _db;
    private readonly Dictionary<string, CelestialBody> _bodies;

    public SurfaceBakeTests()
    {
        _db = new PlannerDatabase(_path);
        _db.Initialize();
        var bodies = new BodyRepository(_db);
        _bodies = bodies.GetBodies(bodies.GetSystems().Single(s => s.Name == "KSP Stock").Id).ToDictionary(b => b.Name);
    }

    public void Dispose()
    {
        TestDb.Release(_path);
        File.Delete(_path);
        File.Delete(_path + ".old");
    }

    /// <summary>A bake whose height at each grid point is <paramref name="height"/>(lat, lon); biome "North" or "South".</summary>
    private static SurfaceBake Bake(double step, Func<double, double, double> height, string body = "Mun")
    {
        var rows = SurfaceBake.GridRows(step);
        var columns = SurfaceBake.GridColumns(step);
        var heights = new float[rows * columns];
        var biomes = new byte[rows * columns];
        for (var r = 0; r < rows; r++)
        for (var c = 0; c < columns; c++)
        {
            var lat = SurfaceBake.RowLatitude(step, r);
            heights[r * columns + c] = (float)height(lat, SurfaceBake.ColumnLongitude(step, c));
            biomes[r * columns + c] = (byte)(lat >= 0 ? 0 : 1);
        }
        return new SurfaceBake(body, step, 230.5, 138_984.376574476, 200_000, 6.5138398e10,
            new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc), heights, biomes, ["North", "South"],
            [new AtmospherePoint(0, 0, null)]);
    }

    [Fact]
    public void Grid_has_a_row_per_step_from_pole_to_pole_and_wraps_in_longitude()
    {
        Assert.Equal((181, 360), (SurfaceBake.GridRows(1), SurfaceBake.GridColumns(1)));
        Assert.Equal((37, 72), (SurfaceBake.GridRows(5), SurfaceBake.GridColumns(5)));
        Assert.Equal((-90, 90), (SurfaceBake.RowLatitude(1, 0), SurfaceBake.RowLatitude(1, 180)));
        Assert.Equal((-180, 179), (SurfaceBake.ColumnLongitude(1, 0), SurfaceBake.ColumnLongitude(1, 359)));
        Assert.True(Bake(5, (_, _) => 0).IsComplete);
    }

    [Fact]
    public void Height_between_grid_points_is_bilinear()
    {
        // docs/MATH.md §1.9: a height that changes linearly with latitude and longitude is read back exactly.
        var bake = Bake(1, (lat, lon) => 100 * lat + lon);
        Assert.Equal(1_289.25, bake.HeightAt(12.5, 39.25)!.Value, 3);
        Assert.Equal(100 * -45 + 10, bake.HeightAt(-45, 10)!.Value, 3);
        // Across the 180° seam: halfway between 179° E (100·lat + 179) and 180° W (100·lat − 180).
        Assert.Equal(100 * 10 - 0.5, bake.HeightAt(10, 179.5)!.Value, 3);
        Assert.Equal(bake.HeightAt(10, -179)!.Value, bake.HeightAt(10, 181)!.Value, 6);
    }

    [Fact]
    public void Worked_example_reads_between_four_points()
    {
        // §1.9: points 1° apart with heights 1,000 / 1,200 (south row) and 1,100 / 1,500 (north row); a spot 0.25° east
        // and 0.6° north of the south-west point: south 1,050, north 1,200, so 1,050 + 0.6 × 150 = 1,140 m.
        var bake = Bake(1, (lat, lon) => (lat, lon) switch
        {
            (10, 20) => 1_000, (10, 21) => 1_200, (11, 20) => 1_100, (11, 21) => 1_500, _ => 0,
        });
        Assert.Equal(1_140, bake.HeightAt(10.6, 20.25)!.Value, 6);
    }

    [Fact]
    public void Biome_is_the_nearest_grid_point_and_extremes_are_found()
    {
        var bake = Bake(1, (lat, lon) => lat == 30 && lon == 40 ? 7_000 : lat == -20 && lon == -60 ? -500 : 0);
        Assert.Equal("North", bake.BiomeAt(0.4, 10));
        Assert.Equal("South", bake.BiomeAt(-0.6, 10));
        var x = bake.Extremes()!.Value;
        Assert.Equal((7_000.0, 30.0, 40.0), x.Highest);
        Assert.Equal((-500.0, -20.0, -60.0), x.Lowest);
    }

    [Fact]
    public void A_body_without_a_surface_has_no_heights()
    {
        var jool = new SurfaceBake("Jool", 1, 0, 36_000, 6_000_000, 2.8e14, DateTime.UtcNow, [], [], [], []);
        Assert.Null(jool.HeightAt(0, 0));
        Assert.Null(jool.BiomeAt(0, 0));
        Assert.Null(jool.Extremes());
    }

    [Fact]
    public void Bakes_round_trip_and_survive_a_rebuild()
    {
        var bake = Bake(5, (lat, lon) => lat + lon / 1000) with
        {
            Atmosphere = [new AtmospherePoint(0, 6_755, 0.0145), new AtmospherePoint(1_000, 6_100, null)],
        };
        new SurfaceBakeRepository(_db).Save(bake);

        using (var c = _db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "DROP TABLE ksp_part_antennas;"; // forces a rebuild that carries install tables over
            cmd.ExecuteNonQuery();
        }
        TestDb.Release(_path);
        _db.Initialize();

        var repo = new SurfaceBakeRepository(_db);
        var loaded = repo.Get("Mun")!;
        Assert.Equal(bake.Heights, loaded.Heights);
        Assert.Equal(bake.Biomes, loaded.Biomes);
        Assert.Equal(bake.BiomeNames, loaded.BiomeNames);
        Assert.Equal(bake.Atmosphere, loaded.Atmosphere);
        Assert.Equal((bake.StepDeg, bake.InitialRotationDeg, bake.BakedUtc), (loaded.StepDeg, loaded.InitialRotationDeg, loaded.BakedUtc));
        Assert.Equal("Mun", Assert.Single(repo.List()).Body);

        repo.Delete("Mun");
        Assert.Null(repo.Get("Mun"));
    }

    [Fact]
    public void Destination_goes_along_a_great_circle()
    {
        var quarter = Math.PI / 2 * 200_000;
        var east = Landing.Destination(0, 0, 90, quarter, 200_000);
        Assert.Equal(0, east.LatitudeDeg, 9);
        Assert.Equal(90, east.LongitudeDeg, 9);
        var north = Landing.Destination(10, 170, 0, quarter / 9, 200_000);
        Assert.Equal(20, north.LatitudeDeg, 9);
        Assert.Equal(170, north.LongitudeDeg, 9);
        // Wraps past 180°.
        Assert.Equal(-170, Landing.Destination(0, 170, 90, quarter / 4.5, 200_000).LongitudeDeg, 9);
    }

    [Fact]
    public void Landing_takes_ground_height_and_approach_terrain_from_the_bake()
    {
        var mun = _bodies["Mun"];
        var site = new LandingTarget("Crater floor", "Landmark", 0, 40);
        // Flat 1,500 m ground with a 6,000 m ridge just west of the site, where an eastward approach passes over.
        var bake = Bake(0.5, (lat, lon) => lon is >= 38 and <= 39 && Math.Abs(lat) <= 3 ? 6_000 : 1_500);

        var plan = Landing.Plan(mun, 14_000, new LandingLeg(site), bake: bake);

        Assert.Equal(1_500, plan.GroundHeightM!.Value, 3);
        Assert.True(plan.GroundFromBake);
        Assert.Equal(2_500, plan.PeriapsisAltitudeM, 3);
        Assert.Equal(6_000, plan.ApproachPeakM!.Value, 3);
        Assert.Contains(plan.Warnings, w => w.Contains("under the braking burn"));
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("ground height") || w.Contains("highest peak"));

        // A spot's own height (a flag's) wins over the bake.
        var flag = Landing.Plan(mun, 14_000, new LandingLeg(site with { GroundHeightM = 2_000 }), bake: bake);
        Assert.Equal(2_000, flag.GroundHeightM);
        Assert.False(flag.GroundFromBake);

        // Flat ground everywhere: nothing under the approach to warn about.
        var flat = Landing.Plan(mun, 14_000, new LandingLeg(site), bake: Bake(0.5, (_, _) => 1_500));
        Assert.Equal(1_500, flat.ApproachPeakM!.Value, 3);
        Assert.DoesNotContain(flat.Warnings, w => w.Contains("under the braking burn"));
    }

    // ---------------- Wiki baseline vs the game (BakeMerge) ----------------

    private static BakeSummary Summary(CelestialBody wiki, double? highest = null, double? seaLevelPa = null, double radiusFactor = 1) =>
        new(wiki.Name, new DateTime(2026, 10, 9, 20, 0, 0, DateTimeKind.Utc), Math.Abs(wiki.RotationPeriodS), wiki.EquatorialRadiusM * radiusFactor,
            wiki.Mu, highest, seaLevelPa is { } p ? [new AtmospherePoint(0, p, 1), new AtmospherePoint(1_000, p / 2, 0.5)] : []);

    [Fact]
    public void Game_values_within_the_threshold_replace_the_wiki()
    {
        // Your bake: Jool reads 5,066.25 kPa at 0 m. The wiki's pressure table says the same; its /Param page says 15 atm.
        var jool = BakeMerge.Apply(_bodies["Jool"], Summary(_bodies["Jool"], seaLevelPa: 5_066_250));
        Assert.Equal(50, jool.Atmosphere!.SeaLevelPressureAtm!.Value, 6);
        Assert.Equal(2, jool.Atmosphere.PressureTable.Count);
        var check = Assert.Single(jool.DataChecks, c => c.Value == "Pressure at 0 m");
        Assert.True(check.UsesGame);
        Assert.False(check.Flagged);
        Assert.All(jool.DataChecks, c => Assert.False(c.Flagged));

        // Laythe: the wiki has no highest point, so the game's is used.
        var laythe = BakeMerge.Apply(_bodies["Laythe"], Summary(_bodies["Laythe"], highest: 5_736));
        Assert.Equal(5_736, laythe.HighestElevationM);
    }

    [Fact]
    public void Highest_point_takes_the_higher_value_within_the_threshold()
    {
        // Mun: the 1° grid tops out at 6,852 m, under the wiki's 7,061 m (−3%): the wiki's higher value stays, unflagged.
        var mun = BakeMerge.Apply(_bodies["Mun"], Summary(_bodies["Mun"], highest: 6_852));
        Assert.Equal(7_061.1416, mun.HighestElevationM);
        var check = Assert.Single(mun.DataChecks, c => c.Value == "Highest point");
        Assert.False(check.UsesGame);
        Assert.False(check.Flagged);
    }

    [Fact]
    public void Values_beyond_the_threshold_keep_the_wiki_and_are_flagged()
    {
        // Tylo: 12,695 m in the game vs 11,290 m on the wiki (+12.4%) is within 15%: the higher, game value is used.
        var tylo = BakeMerge.Apply(_bodies["Tylo"], Summary(_bodies["Tylo"], highest: 12_695));
        Assert.Equal(12_695, tylo.HighestElevationM);
        Assert.Equal(0.1244, Assert.Single(tylo.DataChecks, c => c.Value == "Highest point").Difference!.Value, 4);

        // Eeloo: 1,245 m vs 3,874 m (−68%): wiki kept, flagged.
        var eeloo = BakeMerge.Apply(_bodies["Eeloo"], Summary(_bodies["Eeloo"], highest: 1_245));
        Assert.Equal(3_874, eeloo.HighestElevationM);
        Assert.True(Assert.Single(eeloo.DataChecks, c => c.Value == "Highest point").Flagged);

        // A radius 10% off (a planet mod) is beyond the 5% for constants: wiki kept, flagged.
        var big = BakeMerge.Apply(_bodies["Mun"], Summary(_bodies["Mun"], radiusFactor: 1.1));
        Assert.Equal(200_000, big.EquatorialRadiusM);
        Assert.True(Assert.Single(big.DataChecks, c => c.Value == "Radius").Flagged);
    }

    [Fact]
    public void Stock_bodies_read_from_the_database_carry_the_bake()
    {
        var bodies = new BodyRepository(_db);
        var stockId = bodies.GetSystems().Single(s => s.Name == "KSP Stock").Id;
        Assert.Empty(bodies.GetBodies(stockId).Single(b => b.Name == "Mun").DataChecks);

        new SurfaceBakeRepository(_db).Save(Bake(5, (lat, lon) => lat == 85 && lon == 30 ? 7_500 : 100));

        var mun = bodies.GetBodies(stockId).Single(b => b.Name == "Mun");
        Assert.Equal(7_500, mun.HighestElevationM); // +6% over the wiki: within 15%, higher wins
        Assert.NotEmpty(mun.DataChecks);
        Assert.NotNull(mun.BakedUtc);
        Assert.NotEmpty(bodies.GetBody(mun.Id)!.DataChecks);
        // Other systems' Mun keeps its shipped values.
        var opm = bodies.GetBodies(bodies.GetSystems().Single(s => s.Name == "Outer Planets Mod").Id).Single(b => b.Name == "Mun");
        Assert.Empty(opm.DataChecks);
    }
}
