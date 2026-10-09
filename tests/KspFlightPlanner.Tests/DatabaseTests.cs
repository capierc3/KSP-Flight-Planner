using KspFlightPlanner.Core;
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
    public void Missing_column_with_current_fingerprint_is_rebuilt_keeping_user_data()
    {
        new SettingsRepository(_db).Save(new AppSettings { KspSaveName = "Protogen" });
        using (var c = _db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "ALTER TABLE ksp_resources DROP COLUMN flow_mode;";
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        _db.Initialize();

        Assert.True(new PartCatalogRepository(_db).Load().IsEmpty); // reads flow_mode again
        Assert.Equal("Protogen", new SettingsRepository(_db).Load().KspSaveName);
    }

    [Fact]
    public void Flights_round_trip_and_survive_a_rebuild()
    {
        var flights = new FlightRepository(_db);
        var flight = new Flight
        {
            Name = "Repair KerbLink",
            Kind = FlightKind.Rendezvous,
            BodyName = "Kerbin",
            Craft = new FlightCraft("Fixer 1", "abc123"),
            StartOrbit = new OrbitalElements(700_000, 0.001, 0.5, 30, 40, 1.2, 816_000),
            Burns = [new FlightBurn("Transfer burn", 817_000, new NodeBurn(120.5, -3.25, 1.5)), new FlightBurn("Circularize satellite 1", 818_000, new NodeBurn(-84.6, 0, 0)) { ByReleasedCraft = true, Note = "Release first" }],
            Targets = [new FlightTarget("KerbLink Mk2 A", "def456", new OrbitalElements(3_470_000, 0.0027, 0.56, 281.7, 160.8, 4.01, 816_532), 820_000)],
            StayS = 3600,
            DeorbitPeriapsisAltitudeM = 30_000,
        };
        var id = flights.Add(flight);
        flights.Update(flights.GetAll().Single() with { Name = "Repair KerbLink Mk2 A" });

        using (var c = _db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "DROP TABLE ksp_part_antennas;"; // forces a rebuild that carries user tables over
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _db.Initialize();

        var loaded = Assert.Single(new FlightRepository(_db).GetAll());
        Assert.Equal(id, loaded.Id);
        Assert.Equal("Repair KerbLink Mk2 A", loaded.Name);
        Assert.Equal(flight.Craft, loaded.Craft);
        Assert.Equal(flight.StartOrbit, loaded.StartOrbit);
        Assert.Equal(flight.Burns, loaded.Burns);
        Assert.Equal(flight.Targets, loaded.Targets);
        Assert.Equal((FlightKind.Rendezvous, 3600.0, (double?)30_000), (loaded.Kind, loaded.StayS, loaded.DeorbitPeriapsisAltitudeM));
    }

    [Fact]
    public void Missions_save_over_and_list_most_recently_saved_first()
    {
        var repo = new MissionRepository(_db);
        var duna = new MissionDefinition(
            [new("kerbin.surface"), new("duna.lo", "Relays at 1,000 km")], [new("Launch"), new("Transfer", 15)], [new(0, true, false), new(1, false, true)],
            UseAerobraking: false, IncludePlaneChanges: true, DesignPath: @"C:\KSP\saves\Protogen\Ships\VAB\Relay Carrier.craft",
            Notes: "Bring HG-5s");
        var dunaId = repo.Add("Duna relays", duna);
        Thread.Sleep(5); // updated_utc has sub-millisecond resolution, but keep the order unambiguous
        var munId = repo.Add("Mun base", new MissionDefinition([new("kerbin.surface"), new("mun.surface")], [new("Launch")], []));
        Assert.Equal([munId, dunaId], repo.GetAll().Select(m => m.Id));

        Thread.Sleep(5);
        Assert.True(repo.Update(dunaId, "Duna relay network", duna with { Notes = "Bring HG-5s, not 16s" }));
        Assert.Equal([dunaId, munId], repo.GetAll().Select(m => m.Id));

        var saved = repo.Get(dunaId)!;
        Assert.Equal("Duna relay network", saved.Name);
        Assert.Equal("Bring HG-5s, not 16s", saved.Definition.Notes);
        Assert.Equal((false, true), (saved.Definition.UseAerobraking, saved.Definition.IncludePlaneChanges));
        Assert.Equal(duna.DesignPath, saved.Definition.DesignPath);
        Assert.Equal(15, saved.Definition.Phases[1].MarginPercent);
        Assert.True(saved.UpdatedUtc > saved.CreatedUtc);

        repo.Delete(dunaId);
        Assert.False(repo.Update(dunaId, "Gone", duna));
        Assert.Null(repo.Get(dunaId));
    }

    [Fact]
    public void Missions_saved_before_options_and_notes_still_open_with_defaults()
    {
        using (var c = _db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO saved_missions (name, definition_json, created_utc, updated_utc) VALUES ('Old', $json, $now, $now);
                """;
            cmd.Parameters.AddWithValue("$json", """{"stops":[{"nodeKey":"kerbin.surface"},{"nodeKey":"mun.surface"}],"phases":[{"name":"Launch","marginPercent":10,"extraDv":0}],"legs":[]}""");
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        var old = Assert.Single(new MissionRepository(_db).GetAll()).Definition;
        Assert.Equal((true, false, (string?)null, (string?)null), (old.UseAerobraking, old.IncludePlaneChanges, old.DesignPath, old.Notes));
    }

    [Fact]
    public void Missions_and_the_last_open_one_survive_a_rebuild_from_before_updated_utc()
    {
        var repo = new MissionRepository(_db);
        var id = repo.Add("Duna relays", new MissionDefinition([new("kerbin.surface"), new("duna.lo")], [new("Launch")], [], Notes: "Bring HG-5s"));
        new SettingsRepository(_db).SaveLastMissionId(id);
        using (var c = _db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "ALTER TABLE saved_missions DROP COLUMN updated_utc;"; // as saved by an older version
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        _db.Initialize();

        var saved = Assert.Single(new MissionRepository(_db).GetAll());
        Assert.Equal(("Duna relays", "Bring HG-5s"), (saved.Name, saved.Definition.Notes));
        Assert.Equal(saved.CreatedUtc, saved.UpdatedUtc); // filled from created_utc
        Assert.Equal(id, new SettingsRepository(_db).LoadLastMissionId());
    }

    [Fact]
    public void Live_connection_settings_default_to_stock_krpc_and_round_trip()
    {
        var repository = new SettingsRepository(_db);
        var defaults = repository.Load();
        Assert.Equal(("127.0.0.1", 50000, 50001, false),
            (defaults.KrpcAddress, defaults.KrpcRpcPort, defaults.KrpcStreamPort, defaults.KrpcConnectOnStartup));

        repository.Save(defaults with { KrpcAddress = "gaming-pc", KrpcRpcPort = 51000, KrpcStreamPort = 51001, KrpcConnectOnStartup = true });

        var loaded = repository.Load();
        Assert.Equal(("gaming-pc", 51000, 51001, true),
            (loaded.KrpcAddress, loaded.KrpcRpcPort, loaded.KrpcStreamPort, loaded.KrpcConnectOnStartup));
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

    [Fact]
    public void Planner_data_belongs_to_the_save_it_was_made_in()
    {
        var bodies = new BodyRepository(_db);
        var kerbin = bodies.GetBodies(bodies.GetSystems()[0].Id).Single(b => b.Name == "Kerbin");
        var locations = new CustomLocationRepository(_db);
        var missions = new MissionRepository(_db);
        var flights = new FlightRepository(_db);
        var plans = new ConstellationRepository(_db);
        var settings = new SettingsRepository(_db);
        var route = new MissionDefinition([new("kerbin.surface"), new("mun.lo")], [new("Launch")], []);

        _db.ActiveSave = "Protogen";
        var station = locations.Add("Protogen station", "Kerbin", 250_000);
        var relay = locations.Add(CustomLocation.Circular(0, "relay-1", "Kerbin", 800_000) with { KspSave = "Protogen", KspVesselId = "4021" });
        var missionId = missions.Add("Mun base", route);
        settings.SaveLastMissionId(missionId);
        flights.Add(new Flight { Name = "Refuel", Kind = FlightKind.Rendezvous, BodyName = "Kerbin" });
        plans.Add(new SavedConstellation { Name = "KommNet", BodyId = kerbin.Id, SatelliteCount = 3, AltitudeM = 2_863_334 });
        var shared = locations.Add("Mun 100 km", "Mun", 100_000);
        locations.SetShared(shared, true);
        locations.SetShared(relay, true); // a save's own vessel can't be shared

        _db.ActiveSave = "SandBox";
        Assert.Equal(["Mun 100 km"], locations.GetAll().Select(l => l.Name));
        Assert.Null(locations.Get(station));
        Assert.Empty(missions.GetAll());
        Assert.Null(missions.Get(missionId));
        Assert.Null(settings.LoadLastMissionId());
        Assert.Empty(flights.GetAll());
        Assert.Empty(plans.GetAll());
        Assert.Equal("SandBox", locations.Get(locations.Add("Minmus 50 km", "Minmus", 50_000))!.KspSave);

        _db.ActiveSave = "Protogen";
        Assert.Equal(["Mun 100 km", "Protogen station", "relay-1"], locations.GetAll().Select(l => l.Name).Order());
        Assert.Equal("Protogen", locations.Get(relay)!.KspSave);
        Assert.Equal(missionId, settings.LoadLastMissionId());
        Assert.Single(missions.GetAll());
        Assert.Single(flights.GetAll());
        Assert.Single(plans.GetAll());

        // Unlinking keeps it in its save.
        locations.Unlink(relay);
        Assert.Equal(("Protogen", (string?)null), (locations.Get(relay)!.KspSave, locations.Get(relay)!.KspVesselId));

        // With no save chosen, only shared locations show, and new ones are shared.
        _db.ActiveSave = null;
        Assert.Equal(["Mun 100 km"], locations.GetAll().Select(l => l.Name));
        Assert.Null(locations.Get(locations.Add("Eve 100 km", "Eve", 100_000))!.KspSave);
    }
}
