using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Data;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace KspFlightPlanner.Tests;

public sealed class VesselAnalysisTests : IDisposable
{
    // Trimmed from a real ModuleManager.ConfigCache (stock parts as KSP 1.12.5 loads them).
    private const string Cache = """
        patchedNodeCount = 3
        UrlConfig
        {
            parentUrl = Squad/Resources/ResourcesGeneric.cfg
            RESOURCE_DEFINITION
            {
                name = LiquidFuel
                displayName = Liquid Fuel
                density = 0.005
                unitCost = 0.8
            }
        }
        UrlConfig
        {
            parentUrl = Squad/Resources/ResourcesGeneric.cfg
            RESOURCE_DEFINITION
            {
                name = Oxidizer
                displayName = Oxidizer
                density = 0.005
                unitCost = 0.18
            }
        }
        UrlConfig
        {
            parentUrl = Squad/Parts/Engine/liquidEngineLV-1_v2/liquidEngineLV-1_v2.cfg
            PART
            {
                name = microEngine_v2
                category = Engine
                title = LV-1 "Ant" Liquid Fuel Engine
                mass = 0.02
                cost = 110
                MODULE
                {
                    name = ModuleEnginesFX
                    maxThrust = 2
                    PROPELLANT
                    {
                        name = LiquidFuel
                        ratio = 0.9
                        DrawGauge = True
                    }
                    PROPELLANT
                    {
                        name = Oxidizer
                        ratio = 1.1
                    }
                    atmosphereCurve
                    {
                        key = 0 315
                        key = 1 80
                        key = 3 0.001
                    }
                }
            }
        }
        UrlConfig
        {
            parentUrl = Squad/Parts/FuelTank/miniFuelTank/miniFuelTank.cfg
            PART
            {
                name = miniFuelTank
                title = FL-T50 Fuel Tank
                mass = 0.025
                RESOURCE
                {
                    name = LiquidFuel
                    amount = 18
                    maxAmount = 18
                }
                RESOURCE
                {
                    name = Oxidizer
                    amount = 22
                    maxAmount = 22
                }
            }
        }
        UrlConfig
        {
            parentUrl = Squad/Parts/Utility/RelayAntennas/RA-5.cfg
            PART
            {
                name = RelayAntenna5
                title = RA-2 Relay Antenna
                mass = 0.15
                MODULE
                {
                    name = ModuleDataTransmitter
                    antennaType = RELAY
                    antennaPower = 2000000000
                    antennaCombinable = True
                }
            }
        }
        UrlConfig
        {
            parentUrl = Squad/Parts/Engine/jetEngines/jetEngineBasic.cfg
            PART
            {
                name = JetEngine
                title = J-33 "Wheesley" Turbofan Engine
                mass = 1.5
                MODULE
                {
                    name = ModuleEnginesFX
                    maxThrust = 120
                    PROPELLANT
                    {
                        name = LiquidFuel
                        ratio = 1
                    }
                    PROPELLANT
                    {
                        name = IntakeAir
                        ratio = 22
                    }
                    atmosphereCurve
                    {
                        key = 0 10500
                    }
                }
            }
        }
        """;

    private readonly ITestOutputHelper _output;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly PlannerDatabase _db;
    private readonly PartCatalog _catalog = PartCatalogReader.Parse(Cache);

    public VesselAnalysisTests(ITestOutputHelper output)
    {
        _output = output;
        _db = new PlannerDatabase(_path);
        _db.Initialize();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private static SavedPart Part(string name, double mass, params PartResource[] resources) => new(name, mass, resources, [], []);

    private static SavedPart Ant(double limit) => new("microEngine.v2", 0.02, [], [new SavedEngineState(limit, true, false, false)], []);

    /// <summary>KerbLink Mk2 A from the Protogen save: 14 parts, two part-empty FL-T50s, three Ants limited to 6 %.</summary>
    private static SavedVessel KerbLink() => new("pid", "KerbLink Mk2 A", "Relay", "ORBITING", null)
    {
        Parts =
        [
            Part("probeCoreHex.v2", 0.1, new PartResource("ElectricCharge", 9.58, 10)),
            Part("RelayAntenna5", 0.15),
            Part("batteryBankMini", 0.01, new PartResource("ElectricCharge", 191.66, 200)),
            Part("miniFuelTank", 0.025, new("LiquidFuel", 3.2213725872712158, 18), new("Oxidizer", 3.9372322921897251, 22)),
            Part("miniFuelTank", 0.025, new("LiquidFuel", 3.2213725872712158, 18), new("Oxidizer", 3.9372322921897251, 22)),
            Ant(6), Ant(6), Ant(6),
            Part("solarPanels4", 0.0175), Part("solarPanels4", 0.0175), Part("solarPanels4", 0.0175),
            Part("navLight1", 0.001), Part("navLight1", 0.001), Part("navLight1", 0.001),
        ],
    };

    [Fact]
    public void Reads_parts_engines_antennas_and_resources_from_the_cache()
    {
        Assert.Equal(4, _catalog.Parts.Count);
        var ant = _catalog.Part("microEngine.v2")!; // saves write '.', configs '_'
        Assert.Same(ant, _catalog.Part("microEngine_v2"));
        Assert.Equal("LV-1 \"Ant\" Liquid Fuel Engine", ant.Title);
        var engine = Assert.Single(ant.Engines);
        Assert.Equal((2, 315, 80), (engine.MaxThrustKn, engine.IspVacuumS, engine.IspSeaLevelS));
        Assert.Equal([new Propellant("LiquidFuel", 0.9), new Propellant("Oxidizer", 1.1)], engine.Propellants);
        Assert.Equal(new PartAntenna("RELAY", 2e9, true, 0.75), Assert.Single(_catalog.Part("RelayAntenna5")!.Antennas));
        Assert.Equal(22, _catalog.Part("miniFuelTank")!.Resources[1].MaxAmount);
        Assert.Equal(0.005, _catalog.Density("Oxidizer"));
        Assert.Equal(0.004, _catalog.Density("MonoPropellant")); // not in this cache: stock value
    }

    // Worked example in docs/MATH.md §7.1.
    [Fact]
    public void Relay_delta_v_from_whats_left_in_its_tanks()
    {
        var r = VesselAnalysis.Analyze(KerbLink(), _catalog);
        _output.WriteLine($"dry {r.DryMassT:F4} wet {r.WetMassT:F6} dv {r.DeltaVVacuum:F2} burn {r.BurnTimeS:F1}");

        Assert.Equal(0.4255, r.DryMassT, 1e-9);
        Assert.Equal(0.4255 + (2 * 3.2213725872712158 + 2 * 3.9372322921897251) * 0.005, r.WetMassT, 1e-9);
        Assert.Equal(315, r.EffectiveIspS, 1e-9);
        Assert.Equal(315 * 9.80665 * Math.Log(r.WetMassT / r.DryMassT), r.DeltaVVacuum, 1e-3); // both propellants run out (almost exactly) together
        Assert.Equal(480.35, r.DeltaVVacuum, 0.01);
        Assert.Equal(0.36, r.ThrustKn, 1e-9);   // 3 × 2 kN at 6 %
        Assert.Equal(6, r.FullThrustKn, 1e-9);
        var propellant = r.WetMassT - r.DryMassT;
        Assert.Equal(propellant / (0.36 / (315 * 9.80665)), r.BurnTimeS, 1e-3);
        var ants = Assert.Single(r.Engines);
        Assert.Equal((3, 6.0, true), (ants.Count, ants.ThrustLimitPercent, ants.CountedForDeltaV));
        Assert.Equal(["Oxidizer", "Liquid Fuel", "ElectricCharge"], r.Resources.Select(x => x.DisplayName)); // heaviest first
        Assert.Empty(r.Notes.Where(n => n.Contains("runs out")));
        Assert.Contains("probeCoreHex.v2", r.UnknownParts); // not in this small catalog
    }

    [Fact]
    public void Burn_stops_when_the_first_propellant_runs_out()
    {
        // 10 LF, 2 Ox: the Ant burns LF:Ox at 0.9:1.1, so oxidizer runs out after 2 × 0.9/1.1 = 1.636 LF.
        var vessel = KerbLink() with { };
        vessel = vessel with
        {
            Parts = [Part("miniFuelTank", 0.025, new("LiquidFuel", 10, 18), new("Oxidizer", 2, 22)), Ant(100)],
        };
        var r = VesselAnalysis.Analyze(vessel, _catalog);
        var burned = (2 + 2 * 0.9 / 1.1) * 0.005;
        Assert.Equal(315 * 9.80665 * Math.Log(r.WetMassT / (r.WetMassT - burned)), r.DeltaVVacuum, 1e-6);
        Assert.Contains(r.Notes, n => n.StartsWith("Liquid Fuel runs out last"));
    }

    [Fact]
    public void Jets_are_left_out_in_vacuum()
    {
        var vessel = KerbLink() with
        {
            Parts = [Part("miniFuelTank", 0.025, new("LiquidFuel", 18, 18), new("Oxidizer", 22, 22)),
                new("JetEngine", 1.5, [], [new SavedEngineState(100, true, false, false)], [])],
        };
        var r = VesselAnalysis.Analyze(vessel, _catalog);
        Assert.Equal(0, r.DeltaVVacuum);
        Assert.False(Assert.Single(r.Engines).CountedForDeltaV);
        Assert.Contains(r.Notes, n => n.Contains("Jet engines"));
    }

    [Fact]
    public void Catalog_round_trips_through_the_database_and_survives_a_rebuild()
    {
        var repo = new PartCatalogRepository(_db);
        Assert.True(repo.Load().IsEmpty);
        Assert.Null(repo.GetInfo());

        var written = new DateTime(2026, 10, 4, 12, 52, 0, DateTimeKind.Utc);
        repo.Replace(_catalog, @"E:\KSP\GameData\ModuleManager.ConfigCache", written);
        repo.Replace(_catalog, @"E:\KSP\GameData\ModuleManager.ConfigCache", written); // replaces, doesn't duplicate

        using (var c = _db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "UPDATE app_meta SET value = 'stale' WHERE key = 'fingerprint';";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        _db.Initialize();

        var loaded = repo.Load();
        Assert.Equal(_catalog.Parts.Count, loaded.Parts.Count);
        var ant = loaded.Part("microEngine.v2")!;
        Assert.Equal(_catalog.Part("microEngine.v2")!.Engines[0].Propellants, ant.Engines[0].Propellants);
        Assert.Equal(_catalog.Part("RelayAntenna5")!.Antennas, loaded.Part("RelayAntenna5")!.Antennas);
        Assert.Equal(_catalog.Part("miniFuelTank")!.Resources, loaded.Part("miniFuelTank")!.Resources);
        Assert.Equal(0.005, loaded.Density("LiquidFuel"));
        Assert.Equal(VesselAnalysis.Analyze(KerbLink(), _catalog).DeltaVVacuum, VesselAnalysis.Analyze(KerbLink(), loaded).DeltaVVacuum);

        var info = repo.GetInfo()!;
        Assert.Equal(4, info.PartCount);
        Assert.Equal(written, info.SourceWriteUtc);
    }

    [Fact]
    public void Save_reader_keeps_parts_engine_limits_and_crew()
    {
        const string save = """
            GAME
            {
                Title = Test
                FLIGHTSTATE
                {
                    UT = 100
                    VESSEL
                    {
                        pid = abc
                        name = Lander
                        type = Lander
                        sit = LANDED
                        lct = 40
                        met = 60
                        PART
                        {
                            name = mk1pod.v2
                            mass = 0.8
                            crew = Jebediah Kerman
                            RESOURCE
                            {
                                name = MonoPropellant
                                amount = 7.5
                                maxAmount = 10
                            }
                        }
                        PART
                        {
                            name = microEngine.v2
                            mass = 0.02
                            MODULE
                            {
                                name = ModuleEnginesFX
                                EngineIgnited = True
                                thrustPercentage = 50
                            }
                        }
                    }
                }
            }
            """;
        var vessel = Assert.Single(KspSaveReader.Parse(save).Vessels);
        Assert.Equal((40, 60), (vessel.LaunchUt, vessel.MissionTimeS));
        Assert.Equal(["Jebediah Kerman"], vessel.Crew);
        Assert.Equal(new PartResource("MonoPropellant", 7.5, 10), vessel.Parts[0].Resources[0]);
        Assert.Equal(new SavedEngineState(50, true, false, false), vessel.Parts[1].Engines[0]);
    }
}
