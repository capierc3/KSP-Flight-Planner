using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.Tests;

public sealed class DatabaseTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly PlannerDatabase _db;

    public DatabaseTests()
    {
        _db = new PlannerDatabase(_path);
        _db.Initialize();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public void Seeds_all_builtin_systems()
    {
        var systems = new BodyRepository(_db).GetSystems();
        Assert.Equal(["KSP Stock", "Outer Planets Mod", "Real Solar System", "Galileo’s Planet Pack"], systems.Select(s => s.Name));

        var stock = new BodyRepository(_db).GetBodies(systems[0].Id);
        Assert.Equal(17, stock.Count);
        var kerbin = Assert.Single(stock, b => b.IsDefault);
        Assert.Equal("Kerbin", kerbin.Name);
        Assert.Equal(3.5316e12, kerbin.Mu, 1e3);
        Assert.Equal("#FFEE99", stock[0].Color); // Kerbol "#FE9" expanded

        var kerbol = stock[0];
        Assert.Equal(kerbol.Id, kerbin.ParentId);
        Assert.Null(kerbol.Orbit);
        Assert.Equal(13_599_840_256, kerbin.Orbit!.SemiMajorAxisM);
        Assert.Equal(5600, kerbin.Atmosphere!.ScaleHeightM);
        Assert.True(kerbin.Atmosphere.HasOxygen);
        Assert.Equal(3400, kerbin.SurfaceToLowOrbitDv);

        var duna = stock.Single(b => b.Name == "Duna");
        Assert.Equal(135.5, duna.Orbit!.LongitudeOfAscendingNodeDeg);
        Assert.Equal(50_000, duna.AtmosphereHeightM);
        Assert.Equal(16, stock.Count(b => b.Orbit is not null));
        Assert.Equal(stock.Single(b => b.Name == "Duna").Id, stock.Single(b => b.Name == "Ike").ParentId);
    }

    [Fact]
    public void Non_stock_systems_have_no_orbits_but_keep_atmospheres()
    {
        var repo = new BodyRepository(_db);
        var rss = repo.GetBodies(repo.GetSystems().Single(s => s.Name == "Real Solar System").Id);
        Assert.All(rss, b => Assert.Null(b.Orbit));
        Assert.True(rss.Single(b => b.Name == "Earth").HasAtmosphere);
    }

    [Fact]
    public void Initialize_is_idempotent()
    {
        _db.Initialize();
        var repo = new BodyRepository(_db);
        Assert.Equal(4, repo.GetSystems().Count);
        Assert.Equal(17, repo.GetBodies(repo.GetSystems()[0].Id).Count);
    }

    [Fact]
    public void Outdated_database_is_rebuilt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
        try
        {
            // Simulate a database from an older build: user_version 1 but missing columns, no fingerprint.
            using (var old = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                old.Open();
                using var cmd = old.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE star_systems (id INTEGER PRIMARY KEY, name TEXT, sort_order INTEGER, is_builtin INTEGER);
                    CREATE TABLE celestial_bodies (id INTEGER PRIMARY KEY, system_id INTEGER, name TEXT);
                    INSERT INTO star_systems VALUES (1, 'KSP Stock', 0, 1);
                    PRAGMA user_version = 1;
                    """;
                cmd.ExecuteNonQuery();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            var db = new PlannerDatabase(path);
            db.Initialize();

            var repo = new BodyRepository(db);
            var kerbin = repo.GetBodies(repo.GetSystems()[0].Id).Single(b => b.Name == "Kerbin");
            Assert.NotNull(kerbin.ParentId);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public void Missing_table_with_current_fingerprint_is_rebuilt_keeping_user_data()
    {
        var locations = new CustomLocationRepository(_db);
        locations.Add("Station Alpha", "Kerbin", 150_000);
        using (var c = _db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "DROP TABLE ksp_part_antennas;";
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        _db.Initialize();

        Assert.True(new PartCatalogRepository(_db).Load().IsEmpty); // all ksp tables readable again
        Assert.Equal("Station Alpha", Assert.Single(locations.GetAll()).Name);
    }

    [Fact]
    public void Broken_database_with_current_fingerprint_is_rebuilt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
        try
        {
            using (var bad = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                bad.Open();
                using var cmd = bad.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE app_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                    CREATE TABLE star_systems (id INTEGER PRIMARY KEY, name TEXT, sort_order INTEGER, is_builtin INTEGER);
                    INSERT INTO star_systems VALUES (1, 'KSP Stock', 0, 1);
                    INSERT INTO app_meta VALUES ('fingerprint', $fp);
                    PRAGMA user_version = 1;
                    """;
                cmd.Parameters.AddWithValue("$fp", Migrations.Fingerprint + BodySeeder.Fingerprint + DvMapSeeder.Fingerprint);
                cmd.ExecuteNonQuery();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            var db = new PlannerDatabase(path);
            db.Initialize();

            Assert.NotNull(new DvMapRepository(db).GetMap());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public void Saves_and_deletes_constellations()
    {
        var bodies = new BodyRepository(_db);
        var kerbin = bodies.GetBodies(bodies.GetSystems()[0].Id).Single(b => b.Name == "Kerbin");
        var repo = new ConstellationRepository(_db);

        var id = repo.Add(new SavedConstellation { Name = "KommNet", BodyId = kerbin.Id, SatelliteCount = 3, AltitudeM = 2_863_334 });

        var saved = Assert.Single(repo.GetAll());
        Assert.Equal(id, saved.Id);
        Assert.Equal("Kerbin", saved.BodyName);
        Assert.Equal("KSP Stock", saved.SystemName);

        repo.Delete(id);
        Assert.Empty(repo.GetAll());
    }
}
