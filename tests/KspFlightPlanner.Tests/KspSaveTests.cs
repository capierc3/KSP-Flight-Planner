using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace KspFlightPlanner.Tests;

public sealed class KspSaveTests : IDisposable
{
    // Trimmed from a real 1.12.5 save (CRLF line endings and tabs, as KSP writes them).
    private static readonly string SampleSave = string.Join("\r\n",
        "GAME", "{", "\tversion = 1.12.5", "\tTitle = Protogen (SCIENCE_SANDBOX)", "\tMode = SCIENCE_SANDBOX",
        "\tPARAMETERS", "\t{", "\t\tFLIGHT", "\t\t{", "\t\t\tCanQuickSave = True", "\t\t}", "\t}",
        "\tFLIGHTSTATE", "\t{", "\t\tversion = 1.12.5", "\t\tUT = 816684.80839576013",
        "\t\tVESSEL", "\t\t{", "\t\t\tpid = 367274e0d3264a928e3255c4501d6257", "\t\t\tpersistentId = 1908142703",
        "\t\t\tname = KerbLink Mk2 A", "\t\t\ttype = Relay", "\t\t\tsit = ORBITING",
        "\t\t\tORBIT", "\t\t\t{", "\t\t\t\tSMA = 3470794.9654074414", "\t\t\t\tECC = 0.0027129943455218826",
        "\t\t\t\tINC = 0.56034551942139865", "\t\t\t\tLPE = 160.82777624654261", "\t\t\t\tLAN = 281.71147295126679",
        "\t\t\t\tMNA = 4.0125184630055015", "\t\t\t\tEPH = 816532.32839561813", "\t\t\t\tREF = 1", "\t\t\t\tIDENT = Squad/Kerbin",
        "\t\t\t}", "\t\t\tPART", "\t\t\t{", "\t\t\t\tname = probeCoreOcto", "\t\t\t}", "\t\t}",
        "\t\tVESSEL", "\t\t{", "\t\t\tpid = aaaa", "\t\t\tname = New Horizons 1", "\t\t\ttype = Flag", "\t\t\tsit = LANDED",
        "\t\t\tORBIT", "\t\t\t{", "\t\t\t\tSMA = NaN", "\t\t\t\tECC = NaN", "\t\t\t\tREF = 2", "\t\t\t}", "\t\t}",
        "\t\tVESSEL", "\t\t{", "\t\t\tpid = bbbb", "\t\t\tname = Ast. ILU-882", "\t\t\ttype = SpaceObject", "\t\t\tsit = ORBITING",
        "\t\t\tORBIT", "\t\t\t{", "\t\t\t\tSMA = 14369178701.625093", "\t\t\t\tECC = 0.05", "\t\t\t\tINC = 1",
        "\t\t\t\tLPE = -10", "\t\t\t\tLAN = 20", "\t\t\t\tMNA = -1", "\t\t\t\tEPH = 100", "\t\t\t\tREF = 0", "\t\t\t}", "\t\t}",
        "\t}", "}", "");

    private readonly ITestOutputHelper _output;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly PlannerDatabase _db;
    private readonly Dictionary<string, CelestialBody> _bodies;

    public KspSaveTests(ITestOutputHelper output)
    {
        _output = output;
        _db = new PlannerDatabase(_path);
        _db.Initialize();
        var repo = new BodyRepository(_db);
        _bodies = repo.GetBodies(repo.GetSystems()[0].Id).ToDictionary(b => b.Name);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public void ConfigNode_reads_nested_nodes_both_brace_styles_and_comments()
    {
        var root = ConfigNode.Parse("A\n{\n  x = 1 // note\n  B {\n    y = a = b\n  }\n  B\n  {\n  }\n}\n");
        var a = Assert.Single(root.Nodes);
        Assert.Equal("A", a.Name);
        Assert.Equal("1", a.GetValue("x"));
        Assert.Equal(2, a.GetNodes("B").Count());
        Assert.Equal("a = b", a.GetNode("B")!.GetValue("y"));
    }

    [Fact]
    public void Reads_game_time_and_vessels()
    {
        var save = KspSaveReader.Parse(SampleSave);
        Assert.Equal("Protogen (SCIENCE_SANDBOX)", save.Title);
        Assert.Equal(816684.80839576013, save.Ut);
        Assert.Equal(["KerbLink Mk2 A", "New Horizons 1", "Ast. ILU-882"], save.Vessels.Select(v => v.Name));

        var relay = save.Vessels[0];
        Assert.Equal("367274e0d3264a928e3255c4501d6257", relay.Id);
        Assert.Equal("Relay", relay.Type);
        Assert.True(relay.IsOrbiting);
        Assert.Equal(3470794.9654074414, relay.Orbit!.SemiMajorAxisM);
        Assert.Equal("Kerbin", relay.Orbit.BodyName);
        Assert.Equal("Kerbol", save.Vessels[2].Orbit!.BodyName); // REF 0, no IDENT
    }

    // Worked example in docs/MATH.md §8.1.
    [Fact]
    public void Converts_an_orbiting_vessel_to_a_placed_location()
    {
        var relay = KspSaveReader.Parse(SampleSave).Vessels[0];
        var import = SaveImport.Convert(relay, "Protogen", _bodies);

        var l = Assert.IsType<CustomLocation>(import.Location);
        _output.WriteLine($"pe {l.PeriapsisAltitudeM:F1}  ap {l.ApoapsisAltitudeM:F1}  M {l.MeanAnomalyDeg:F3}");
        Assert.Equal("Kerbin", l.BodyName);
        Assert.Equal(3470794.9654 * (1 - 0.0027129943) - 600_000, l.PeriapsisAltitudeM, 0.1);
        Assert.Equal(3470794.9654 * (1 + 0.0027129943) - 600_000, l.ApoapsisAltitudeM, 0.1);
        Assert.Equal(2_861_378.7, l.PeriapsisAltitudeM, 0.1);
        Assert.Equal(2_880_211.2, l.ApoapsisAltitudeM, 0.1);
        Assert.Equal(229.900, l.MeanAnomalyDeg, 0.001);
        Assert.Equal(281.71147295126679, l.LanDeg);
        Assert.Equal(160.82777624654261, l.ArgumentOfPeriapsisDeg);
        Assert.Equal(816532.32839561813, l.EpochUt);
        Assert.True(l.IsPlaced);
        Assert.True(l.IsLinked);
        Assert.Equal("367274e0d3264a928e3255c4501d6257", l.KspVesselId);

        // The location sits where the vessel is: same position as KSP's elements give directly.
        var kerbin = _bodies["Kerbin"];
        var elements = CustomLocationOrbits.ToElements(l, kerbin);
        var direct = new OrbitalElements(3470794.9654074414, 0.0027129943455218826, 0.56034551942139865, 281.71147295126679,
            160.82777624654261, 4.0125184630055015, 816532.32839561813);
        var a = Kepler.StateAt(elements, kerbin.Mu, 820_000).Position;
        var b = Kepler.StateAt(direct, kerbin.Mu, 820_000).Position;
        Assert.True((a - b).Length < 1, $"positions differ by {(a - b).Length} m");
    }

    [Fact]
    public void Wraps_negative_angles_and_explains_what_cannot_be_imported()
    {
        var vessels = KspSaveReader.Parse(SampleSave).Vessels;

        var flag = SaveImport.Convert(vessels[1], "Protogen", _bodies);
        Assert.False(flag.CanImport);
        Assert.Contains("surface", flag.Problem);

        var asteroid = SaveImport.Convert(vessels[2], "Protogen", _bodies);
        Assert.True(asteroid.CanImport, asteroid.Problem);
        Assert.Equal(350, asteroid.Location!.ArgumentOfPeriapsisDeg, 1e-9);
        Assert.Equal(360 - 180 / Math.PI, asteroid.Location.MeanAnomalyDeg, 1e-9);
        Assert.False(SaveImport.IsUsuallyWanted(vessels[2]));
        Assert.True(SaveImport.IsUsuallyWanted(vessels[0]));

        var lowOrbit = vessels[0] with { Orbit = vessels[0].Orbit! with { SemiMajorAxisM = 650_000 } };
        Assert.Contains("atmosphere", SaveImport.Convert(lowOrbit, "Protogen", _bodies).Problem);
    }

    [Fact]
    public void Linked_locations_keep_their_link_and_refresh_only_the_orbit()
    {
        _db.ActiveSave = "Protogen";
        var locations = new CustomLocationRepository(_db);
        var imported = SaveImport.Convert(KspSaveReader.Parse(SampleSave).Vessels[0], "Protogen", _bodies).Location!;
        var id = locations.Add(imported);
        var stored = locations.Get(id)!;
        Assert.Equal("Protogen", stored.KspSave);
        Assert.Equal(imported.KspVesselId, stored.KspVesselId);

        // User renames it; the vessel then moves in the game.
        locations.Update(stored with { Name = "Relay A" });
        var moved = imported with { PeriapsisAltitudeM = 2_900_000, ApoapsisAltitudeM = 2_950_000, EpochUt = 900_000 };
        locations.Update(SaveImport.Refresh(locations.Get(id)!, moved));

        var refreshed = locations.Get(id)!;
        Assert.Equal("Relay A", refreshed.Name);
        Assert.Equal(2_950_000, refreshed.ApoapsisAltitudeM);
        Assert.Equal(900_000, refreshed.EpochUt);
        Assert.True(refreshed.IsLinked);

        locations.Unlink(id);
        Assert.False(locations.Get(id)!.IsLinked);
    }

    [Fact]
    public void Settings_round_trip_and_survive_a_rebuild()
    {
        var settings = new SettingsRepository(_db);
        Assert.True(settings.Load().WatchSave); // default on
        Assert.Null(settings.Load().KspInstallPath);

        settings.Save(new AppSettings { KspInstallPath = @"E:\KSP", KspSaveName = "Protogen", WatchSave = false });
        _db.ActiveSave = "Protogen";
        var locations = new CustomLocationRepository(_db);
        var id = locations.Add(SaveImport.Convert(KspSaveReader.Parse(SampleSave).Vessels[0], "Protogen", _bodies).Location!);

        using (var c = _db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "UPDATE app_meta SET value = 'stale' WHERE key = 'fingerprint';";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        _db.Initialize();

        Assert.Equal(new AppSettings { KspInstallPath = @"E:\KSP", KspSaveName = "Protogen", WatchSave = false }, settings.Load());
        Assert.True(locations.Get(id)!.IsLinked);

        settings.Save(new AppSettings());
        Assert.Equal(new AppSettings(), settings.Load());
    }

    [Fact]
    public void Finds_saves_but_not_the_built_in_scenarios()
    {
        var install = Path.Combine(Path.GetTempPath(), $"kspfp-install-{Guid.NewGuid():N}");
        try
        {
            Assert.NotNull(KspSaveReader.CheckInstallFolder(install));
            foreach (var name in new[] { "Protogen", "scenarios", "training", "Empty" })
                Directory.CreateDirectory(Path.Combine(install, "saves", name));
            File.WriteAllText(KspSaveReader.PersistentPath(install, "Protogen"), SampleSave);
            File.WriteAllText(KspSaveReader.PersistentPath(install, "training"), SampleSave);

            Assert.Null(KspSaveReader.CheckInstallFolder(install));
            var save = Assert.Single(KspSaveReader.FindSaves(install));
            Assert.Equal("Protogen", save.Name);
            Assert.Equal("Protogen (SCIENCE_SANDBOX)", save.Title);
            Assert.Equal(3, KspSaveReader.Read(save.PersistentPath).Vessels.Count);
        }
        finally
        {
            Directory.Delete(install, recursive: true);
        }
    }
}
