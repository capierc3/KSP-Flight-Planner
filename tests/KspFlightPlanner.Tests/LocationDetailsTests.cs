using KspFlightPlanner.Core;
using KspFlightPlanner.Data;
using Microsoft.Data.Sqlite;

namespace KspFlightPlanner.Tests;

public sealed class LocationDetailsTests : IDisposable
{
    private const double KerbinMu = 3.5316e12;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly PlannerDatabase _db;

    public LocationDetailsTests()
    {
        _db = new PlannerDatabase(_path);
        _db.Initialize();
    }

    public void Dispose()
    {
        TestDb.Release(_path);
        File.Delete(_path);
        File.Delete(_path + ".old");
    }

    private CustomLocationAnalysis Analyze(CustomLocation location)
    {
        var repo = new BodyRepository(_db);
        var bodies = repo.GetBodies(repo.GetSystems()[0].Id).ToDictionary(b => b.Name);
        return CustomLocations.Analyze(location, new DvMapRepository(_db).GetMap()!, bodies);
    }

    // ---------------- Math ----------------

    [Fact]
    public void Transfer_to_a_circular_orbit_is_a_hohmann_transfer()
    {
        var h = OrbitMath.Hohmann(KerbinMu, 680_000, 750_000);
        var t = OrbitMath.TransferToEllipse(KerbinMu, 680_000, 750_000, 750_000);
        Assert.Equal(h.Dv1, t.DepartureDv, 1e-9);
        Assert.Equal(h.Dv2, t.ArrivalDv, 1e-9);
    }

    [Fact]
    public void Elliptical_orbit_uses_the_cheaper_burn_order()
    {
        // docs/MATH.md §2.2: 80 km circular → 100 × 2,000 km. Reaching apoapsis first: 590.5 + 8.65 = 599.15 m/s;
        // reaching periapsis first would cost 16.45 + 589.76 = 606.21 m/s.
        var t = OrbitMath.TransferToEllipse(KerbinMu, 680_000, 700_000, 2_600_000);
        Assert.Equal(2_600_000, t.ArrivalRadius);
        Assert.Equal(590.5, t.DepartureDv, 0.1);
        Assert.Equal(8.65, t.ArrivalDv, 0.01);
    }

    [Fact]
    public void Orbit_shape_derives_eccentricity_period_and_speeds()
    {
        var o = new OrbitShape(KerbinMu, 700_000, 2_600_000);
        Assert.Equal(1_650_000, o.SemiMajorAxis);
        Assert.Equal(0.57576, o.Eccentricity, 0.00001);
        Assert.Equal(7_086.0, o.PeriodS, 1.0);
        Assert.Equal(2_819.6, o.PeriapsisSpeed, 0.1);
        Assert.Equal(759.1, o.ApoapsisSpeed, 0.1);
        Assert.False(o.IsCircular);
    }

    [Fact]
    public void Plane_change_is_charged_at_apoapsis_where_it_is_cheapest()
    {
        var a = Analyze(new CustomLocation(1, "Polar relay", "Kerbin", 100_000, 2_000_000, InclinationDeg: 10));
        Assert.Equal(132.3, a.PlaneChangeDv, 0.1);      // 2 × 759.1 × sin 5°
        Assert.Equal(132, a.Node.PlaneChangeDv);
        Assert.Equal(599, a.Node.DvFromParent);
        Assert.Equal("Polar relay (100 × 2,000 km)", a.Node.Label);
    }

    // ---------------- Validation (independent values only; nothing derived can conflict) ----------------

    [Theory]
    [InlineData("Kerbin", 60_000, 60_000, 0, "atmosphere")]
    [InlineData("Kerbin", 500_000, 200_000, 0, "Apoapsis must be at least")]
    [InlineData("Kerbin", 100_000, 90_000_000, 0, "sphere of influence")]
    [InlineData("Mun", 5_000, 5_000, 0, "highest terrain")]
    [InlineData("Kerbin", 100_000, 100_000, 200, "Inclination")]
    public void Invalid_values_are_rejected_with_a_reason(string body, double pe, double ap, double inc, string reason)
    {
        var ex = Assert.Throws<ArgumentException>(() => Analyze(new CustomLocation(1, "Test", body, pe, ap, inc)));
        Assert.Contains(reason, ex.Message);
    }

    // ---------------- Repository ----------------

    [Fact]
    public void Elliptical_location_with_description_round_trips()
    {
        var repo = new CustomLocationRepository(_db);
        var id = repo.Add(new CustomLocation(0, "Molniya relay", "Kerbin", 100_000, 2_000_000, 63.4, "Long dwell over the north."));

        var saved = repo.Get(id)!;
        Assert.Equal(100_000, saved.PeriapsisAltitudeM);
        Assert.Equal(2_000_000, saved.ApoapsisAltitudeM);
        Assert.Equal(63.4, saved.InclinationDeg);
        Assert.Equal("Long dwell over the north.", saved.Description);
        Assert.False(saved.IsCircular);
    }

    [Fact]
    public void Images_are_stored_and_removed_with_the_location()
    {
        var repo = new CustomLocationRepository(_db);
        var id = repo.Add("Station Alpha", "Kerbin", 150_000);
        var imageId = repo.AddImage(id, "station.png", [1, 2, 3, 4]);
        repo.AddImage(id, "docking.png", [5, 6]);

        var images = repo.GetImages(id);
        Assert.Equal(["station.png", "docking.png"], images.Select(i => i.FileName));
        Assert.Equal([1, 2, 3, 4], images[0].Data);

        repo.DeleteImage(imageId);
        Assert.Single(repo.GetImages(id));

        repo.Delete(id);
        Assert.Empty(repo.GetImages(id)); // cascades
    }

    // ---------------- Data survives pre-release database rebuilds ----------------

    [Fact]
    public void User_data_is_carried_over_when_the_database_is_rebuilt()
    {
        var locations = new CustomLocationRepository(_db);
        var id = locations.Add(new CustomLocation(0, "Station Alpha", "Kerbin", 150_000, 400_000, 5, "Crew hub"));
        locations.AddImage(id, "station.png", [9, 9, 9]);
        new DvMapRepository(_db).SetOverride("mun.surface", 700);

        // Pretend the schema changed: a stale fingerprint forces a rebuild on the next start.
        using (var c = _db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "UPDATE app_meta SET value = 'stale' WHERE key = 'fingerprint';";
            cmd.ExecuteNonQuery();
        }
        TestDb.Release(_path);
        _db.Initialize();

        var kept = Assert.Single(locations.GetAll());
        Assert.Equal(id, kept.Id);
        Assert.Equal(400_000, kept.ApoapsisAltitudeM);
        Assert.Equal("Crew hub", kept.Description);
        Assert.Single(locations.GetImages(id));
        Assert.Equal(700, new DvMapRepository(_db).GetMap()!["mun.surface"].DvFromParent);
        Assert.False(File.Exists(_path + ".old"));
    }

    [Fact]
    public void Old_circular_locations_become_periapsis_and_apoapsis()
    {
        // A database from the previous version: custom_locations had altitude_m and note.
        var oldPath = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
        try
        {
            using (var c = new SqliteConnection($"Data Source={oldPath}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE app_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                    INSERT INTO app_meta VALUES ('fingerprint', 'previous-version');
                    CREATE TABLE custom_locations (id INTEGER PRIMARY KEY, name TEXT NOT NULL, body_name TEXT NOT NULL,
                        altitude_m REAL NOT NULL, note TEXT, created_utc TEXT NOT NULL);
                    INSERT INTO custom_locations VALUES (7, 'Eye of Kerbol', 'Kerbol', 10000000000, 'Sun watcher', '2026-10-07T00:00:00Z');
                    PRAGMA user_version = 1;
                    """;
                cmd.ExecuteNonQuery();
            }
            TestDb.Release(oldPath);

            var db = new PlannerDatabase(oldPath);
            db.Initialize();

            var eye = Assert.Single(new CustomLocationRepository(db).GetAll());
            Assert.Equal(7, eye.Id);
            Assert.Equal(10_000_000_000, eye.PeriapsisAltitudeM);
            Assert.Equal(10_000_000_000, eye.ApoapsisAltitudeM);
            Assert.Equal("Sun watcher", eye.Description);
            Assert.NotNull(new DvMapRepository(db).GetMap()!["custom.7"]);
        }
        finally
        {
            TestDb.Release(oldPath);
            File.Delete(oldPath);
            File.Delete(oldPath + ".old");
        }
    }
}
