using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.Tests;

/// <summary>Stock power parts and bodies, checked by hand (docs/MATH.md §7.8).</summary>
public sealed class PowerBudgetTests : IDisposable
{
    // Trimmed from ModuleManager.ConfigCache: a probe core, a battery, a solar panel, an RTG, a fuel cell, an antenna,
    // a lab and an ion engine.
    private const string Cache = """
        UrlConfig
        {
            PART
            {
                name = probeCoreOcto2_v2
                title = Probodobodyne OKTO2
                mass = 0.04
                MODULE
                {
                    name = ModuleCommand
                    RESOURCE
                    {
                        name = ElectricCharge
                        rate = 0.03
                    }
                }
                MODULE
                {
                    name = ModuleDataTransmitter
                    antennaType = INTERNAL
                    packetInterval = 1.0
                    packetResourceCost = 12.0
                    antennaPower = 5000
                }
                RESOURCE
                {
                    name = ElectricCharge
                    amount = 5
                    maxAmount = 5
                }
            }
        }
        UrlConfig
        {
            PART
            {
                name = batteryPack
                title = Z-100 Rechargeable Battery Pack
                mass = 0.005
                RESOURCE
                {
                    name = ElectricCharge
                    amount = 100
                    maxAmount = 100
                }
            }
        }
        UrlConfig
        {
            PART
            {
                name = solarPanels4
                title = OX-4W 3x2 Photovoltaic Panels
                mass = 0.0175
                MODULE
                {
                    name = ModuleDeployableSolarPanel
                    resourceName = ElectricCharge
                    chargeRate = 1.64
                }
            }
        }
        UrlConfig
        {
            PART
            {
                name = solarPanels5
                title = OX-STAT Photovoltaic Panels
                mass = 0.005
                MODULE
                {
                    name = ModuleDeployableSolarPanel
                    isTracking = false
                    chargeRate = 0.35
                }
            }
        }
        UrlConfig
        {
            PART
            {
                name = rtg
                title = PB-NUK Radioisotope Thermoelectric Generator
                mass = 0.08
                MODULE
                {
                    name = ModuleGenerator
                    isAlwaysActive = true
                    OUTPUT_RESOURCE
                    {
                        name = ElectricCharge
                        rate = 0.75
                    }
                }
            }
        }
        UrlConfig
        {
            PART
            {
                name = FuelCell
                title = Fuel Cell
                mass = 0.05
                MODULE
                {
                    name = ModuleResourceConverter
                    INPUT_RESOURCE
                    {
                        ResourceName = LiquidFuel
                        Ratio = 0.0016875
                    }
                    INPUT_RESOURCE
                    {
                        ResourceName = Oxidizer
                        Ratio = 0.0020625
                    }
                    OUTPUT_RESOURCE
                    {
                        ResourceName = ElectricCharge
                        Ratio = 1.5
                    }
                }
                RESOURCE
                {
                    name = ElectricCharge
                    amount = 50
                    maxAmount = 50
                }
            }
        }
        UrlConfig
        {
            PART
            {
                name = commDish
                title = Communotron 88-88
                mass = 0.025
                MODULE
                {
                    name = ModuleDataTransmitter
                    antennaType = DIRECT
                    packetInterval = 0.10
                    packetResourceCost = 20.0
                    antennaPower = 100000000000
                }
            }
        }
        UrlConfig
        {
            PART
            {
                name = Large_Crewed_Lab
                title = Mobile Processing Lab MPL-LG-2
                mass = 3.5
                MODULE
                {
                    name = ModuleScienceConverter
                    powerRequirement = 5
                }
            }
        }
        UrlConfig
        {
            PART
            {
                name = ionEngine
                title = IX-6315 "Dawn" Electric Propulsion System
                mass = 0.25
                MODULE
                {
                    name = ModuleEnginesFX
                    maxThrust = 2
                    PROPELLANT
                    {
                        name = ElectricCharge
                        ratio = 1.8
                    }
                    PROPELLANT
                    {
                        name = XenonGas
                        ratio = 0.1
                    }
                    atmosphereCurve
                    {
                        key = 0 4200
                        key = 1 100
                    }
                }
            }
        }
        UrlConfig
        {
            RESOURCE_DEFINITION
            {
                name = XenonGas
                density = 0.0001
            }
        }
        UrlConfig
        {
            RESOURCE_DEFINITION
            {
                name = ElectricCharge
                density = 0
            }
        }
        """;

    private readonly PartCatalog _catalog = PartCatalogReader.Parse(Cache);
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        TestDb.Release(_path);
        File.Delete(_path);
    }

    private KspPart P(string name) => _catalog.Part(name)!;

    [Fact]
    public void The_reader_collects_what_each_module_makes_or_uses()
    {
        Assert.Equal([new PartPower(PowerKind.Command, 0.03), new PartPower(PowerKind.Antenna, 12)], P("probeCoreOcto2.v2").PowerModules);
        Assert.Equal(new PartPower(PowerKind.Solar, 1.64, Tracking: true), Assert.Single(P("solarPanels4").PowerModules));
        Assert.False(Assert.Single(P("solarPanels5").PowerModules).Tracking);
        Assert.Equal(new PartPower(PowerKind.Generator, 0.75), Assert.Single(P("rtg").PowerModules));
        var cell = Assert.Single(P("FuelCell").PowerModules);
        Assert.Equal(PowerKind.FuelCell, cell.Kind);
        Assert.Equal(1.5, cell.EcPerSecond);
        Assert.Equal([new Propellant("LiquidFuel", 0.0016875), new Propellant("Oxidizer", 0.0020625)], cell.Inputs);
        Assert.Equal(200, Assert.Single(P("commDish").PowerModules).EcPerSecond, 1e-9); // 20 per packet, a packet every 0.1 s
        Assert.Equal(5, Assert.Single(P("Large.Crewed.Lab").PowerModules).EcPerSecond);
        Assert.Empty(P("batteryPack").PowerModules);
    }

    [Fact]
    public void Power_modules_round_trip_through_the_database()
    {
        var db = new PlannerDatabase(_path);
        db.Initialize();
        var repo = new PartCatalogRepository(db);
        repo.Replace(_catalog, "test", DateTime.UtcNow);
        var loaded = repo.Load();

        foreach (var part in _catalog.Parts.Values)
        {
            var back = loaded.Part(part.Name)!.PowerModules;
            Assert.Equal(part.PowerModules.Count, back.Count);
            for (var i = 0; i < back.Count; i++)
            {
                Assert.Equal(part.PowerModules[i] with { Inputs = null }, back[i] with { Inputs = null });
                Assert.Equal(part.PowerModules[i].Inputs ?? [], back[i].Inputs ?? []);
            }
        }
    }

    [Fact]
    public void Ion_engines_burn_charge_as_a_propellant()
    {
        // ṁ = 2 kN / (4200 s · g₀) = 4.856e-5 t/s; charge 1.8 : xenon 0.1 at 0.0001 t per unit of xenon → 8.74 EC/s.
        Assert.Equal(2 / (4200 * VesselAnalysis.G0) * 1.8 / (0.1 * 0.0001), PowerBudget.EngineEcPerSecond(P("ionEngine").Engines[0], _catalog), 1e-9);
        Assert.Equal(8.7404, PowerBudget.EngineEcPerSecond(P("ionEngine").Engines[0], _catalog), 1e-4);
    }

    // Kerbol, Kerbin and the Mun with stock figures.
    private static readonly CelestialBody Kerbol = new() { Id = 1, Name = "Kerbol", Mu = 1.1723328e18, EquatorialRadiusM = 261_600_000 };
    private static readonly CelestialBody Kerbin = new()
    {
        Id = 2, Name = "Kerbin", ParentId = 1, IsDefault = true, Mu = 3.5316e12, EquatorialRadiusM = 600_000, RotationPeriodS = 21_549.425,
        Orbit = new OrbitalElements(13_599_840_256, 0, 0, 0, 0, 0),
    };
    private static readonly CelestialBody Mun = new()
    {
        Id = 3, Name = "Mun", ParentId = 2, Mu = 6.5138398e10, EquatorialRadiusM = 200_000, RotationPeriodS = 138_984.38,
        Orbit = new OrbitalElements(12_000_000, 0, 0, 0, 0, 0),
    };
    private static readonly CelestialBody Duna = new()
    {
        Id = 4, Name = "Duna", ParentId = 1, Mu = 3.0136321e11, EquatorialRadiusM = 320_000, RotationPeriodS = 65_517.859,
        Orbit = new OrbitalElements(20_726_155_264, 0.051, 0, 0, 0, 0),
    };
    private static readonly CelestialBody[] System = [Kerbol, Kerbin, Mun, Duna];

    [Fact]
    public void Nights_in_orbit_and_on_the_surface()
    {
        // 80 km over Kerbin: T = 2π √(680,000³ / μ) = 1,874.8 s; shadow T · asin(600 / 680) / π = 645.0 s.
        var orbit = PowerBudget.InOrbit(Kerbin, 80_000, System);
        Assert.Equal(645.013, orbit.NightS, 1e-3);
        Assert.Equal(1229.798, orbit.DayS, 1e-3);
        Assert.Equal(13_599_840_256, orbit.SunDistanceM);

        // Kerbin's solar day: 21,549.4 s against a 9,203,544.6 s year is exactly KSP's 6-hour day; night is half of it.
        Assert.Equal(10_800, PowerBudget.OnSurface(Kerbin, System).NightS, 1e-3);
        // The Mun turns once per orbit; its sunlit day is set by Kerbin's year: 141,115.4 s, so 70,557.7 s of night.
        var mun = PowerBudget.OnSurface(Mun, System);
        Assert.Equal(70_557.694, mun.NightS, 1e-3);
        Assert.Equal(13_599_840_256, mun.SunDistanceM); // a moon is as far from the Sun as its planet
        Assert.Equal(0.43056, PowerBudget.SolarScale(PowerBudget.SunDistance(Duna, System), 13_599_840_256), 1e-5);
    }

    [Fact]
    public void A_probe_on_solar_power_through_an_orbital_night()
    {
        // OKTO2 + 2 Z-100s + 2 OX-4Ws at 80 km over Kerbin: uses 0.03 EC/s, makes 3.28 in the sun, nothing in the dark.
        var parts = new (KspPart, int)[] { (P("probeCoreOcto2.v2"), 1), (P("batteryPack"), 2), (P("solarPanels4"), 2), (P("commDish"), 1) };
        var r = PowerBudget.Work(parts, _catalog, PowerBudget.InOrbit(Kerbin, 80_000, System), 13_599_840_256, new HashSet<string> { "Command" }, false);

        Assert.Equal(0.03, r.UsedEcS, 1e-12);
        Assert.Equal(3.28, r.MadeInSunEcS, 1e-12);
        Assert.Equal(0, r.MadeInDarkEcS);
        Assert.Equal(205, r.BatteryEc);
        Assert.Equal(0.03 * 645.013, r.BatteryNeededEc, 1e-3); // 19.35 EC over the night
        Assert.True(r.LastsTheNight);
        Assert.Equal(205 / 0.03, r.BatteryLastsS!.Value, 1e-6);
        Assert.Equal(19.350 / 3.25, r.RechargeS!.Value, 1e-3);
        Assert.True(r.RechargesInTheDay);

        // Transmitting through the 88-88 (200 EC/s, the hungriest antenna aboard) drains 205 EC in about a second.
        var sending = PowerBudget.Work(parts, _catalog, PowerBudget.InOrbit(Kerbin, 80_000, System), 13_599_840_256,
            new HashSet<string> { "Command", "Antenna" }, false);
        Assert.Equal(200.03, sending.UsedEcS, 1e-9);
        Assert.False(sending.LastsTheNight);
        Assert.Null(sending.RechargeS); // the panels can't keep up even in the sun
    }

    [Fact]
    public void Panels_lose_output_with_distance_and_generators_and_fuel_cells_cover_the_dark()
    {
        // A lab (5 EC/s) on the Mun's surface with 4 RTGs (3 EC/s) and a fuel cell (1.5 EC/s): 0.5 EC/s short at night.
        var parts = new (KspPart, int)[] { (P("Large.Crewed.Lab"), 1), (P("rtg"), 4), (P("FuelCell"), 1), (P("solarPanels4"), 4) };
        var mun = PowerBudget.OnSurface(Mun, System);
        var on = new HashSet<string> { "Lab" };

        var noCells = PowerBudget.Work(parts, _catalog, mun, 13_599_840_256, on, false);
        Assert.Equal(3, noCells.MadeInDarkEcS, 1e-12);
        Assert.Equal(2 * 70_557.694, noCells.BatteryNeededEc, 1e-2); // 2 EC/s short for the whole night
        Assert.False(noCells.LastsTheNight); // the fuel cell's own 50 EC don't come close

        var cells = PowerBudget.Work(parts, _catalog, mun, 13_599_840_256, on, true);
        Assert.Equal(4.5, cells.MadeInDarkEcS, 1e-12);
        Assert.Equal(0.5 * 70_557.694, cells.BatteryNeededEc, 1e-2);
        // The cell runs flat out all night: 0.0016875 LF/s × 70,557.7 s = 119.07 units of liquid fuel.
        Assert.Equal(0.0016875 * 70_557.694, cells.FuelPerNight.Single(f => f.Name == "LiquidFuel").Ratio, 1e-3);

        // At Duna the same panels make 43% of what they make at Kerbin.
        var duna = PowerBudget.InOrbit(Duna, 100_000, System);
        var atDuna = PowerBudget.Work([(P("solarPanels4"), 1)], _catalog, duna, 13_599_840_256, on, false);
        Assert.Equal(1.64 * 0.4305565, atDuna.MadeInSunEcS, 1e-5);
    }
}
