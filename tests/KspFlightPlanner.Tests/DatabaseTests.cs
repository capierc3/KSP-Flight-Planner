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
        Assert.Equal(3.5316e12, kerbin.Mu, 1e8);
        Assert.Equal("#FFEE99", stock[0].Color); // Kerbol "#FE9" expanded
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
