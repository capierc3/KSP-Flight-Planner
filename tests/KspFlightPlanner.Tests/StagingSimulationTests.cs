using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.Tests;

/// <summary>Small made-up rockets with round numbers, so each flow rule can be checked by hand.</summary>
public sealed class StagingSimulationTests
{
    private const double G0 = 9.80665;

    // tank: 0.5 t dry, 1 t of LF/Ox when full (90 + 110 units). bigTank: 4 t of fuel.
    // engine: 1 t, 200 kN, Isp 300 s vacuum / 250 s sea level. srb: 1 t dry + 100 SolidFuel (0.75 t), 100 kN, Isp 200 / 180.
    private static readonly PartCatalog Catalog = new(
        [
            Part("tank", 0.5, resources: [new("LiquidFuel", 90, 90), new("Oxidizer", 110, 110)]),
            Part("bigTank", 1, resources: [new("LiquidFuel", 360, 360), new("Oxidizer", 440, 440)]),
            Part("engine", 1, engines: [new PartEngine(null, 200, 300, 250, [new("LiquidFuel", 0.9), new("Oxidizer", 1.1)])]),
            Part("srb", 1, resources: [new("SolidFuel", 100, 100)], engines: [new PartEngine(null, 100, 200, 180, [new("SolidFuel", 1)])]),
            Part("decoupler", 0.05, crossfeed: false),
            Part("clamp", 1),
            Part("fuelLine", 0.05),
        ],
        [
            new KspResource("LiquidFuel", "Liquid Fuel", 0.005, 0.8, "STACK_PRIORITY_SEARCH"),
            new KspResource("Oxidizer", "Oxidizer", 0.005, 0.18, "STACK_PRIORITY_SEARCH"),
            new KspResource("SolidFuel", "Solid Fuel", 0.0075, 0.6, "NO_FLOW"),
        ]);

    private static KspPart Part(string name, double mass, PartResource[]? resources = null, PartEngine[]? engines = null, bool crossfeed = true) =>
        new(name, name, null, null, mass, 0, 0, resources ?? [], engines ?? [], [], crossfeed);

    /// <summary>A craft part with full tanks taken from the catalog.</summary>
    private static CraftPart P(string id, int stage, int sep, string[]? children = null, string? fuelLineTarget = null, bool? crossfeed = null)
    {
        var name = CraftReader.PartName(id);
        var definition = Catalog.Part(name)!;
        return new CraftPart(id, name, stage, sep, 0, definition.Resources, definition.Engines.Select(_ => new SavedEngineState(100, false, false, false)).ToList(),
            children ?? [], fuelLineTarget, crossfeed);
    }

    private static double Dv(double isp, double m0, double m1) => isp * G0 * Math.Log(m0 / m1);

    [Fact]
    public void Part_names_drop_the_instance_id()
    {
        Assert.Equal("liquidEngine3.v2", CraftReader.PartName("liquidEngine3.v2_4294121630"));
        Assert.Equal("Size1p5.Tank.03", CraftReader.PartName("Size1p5.Tank.03_4293993394"));
        Assert.Equal("noSuffix", CraftReader.PartName("noSuffix"));
    }

    [Fact]
    public void Two_stage_stack_each_engine_only_burns_its_own_stage()
    {
        // Upper: tank_1 + engine_2. Decoupler_3 (fires in stage 0) drops tank_4 + engine_5 (fires in stage 1).
        var craft = new CraftFile("Stack", "VAB", null,
        [
            P("tank_1", 0, -1, ["engine_2", "decoupler_3"]),
            P("engine_2", 0, -1),
            P("decoupler_3", 0, 0, ["tank_4"]),
            P("tank_4", 0, 0, ["engine_5"]),
            P("engine_5", 1, 0),
        ]);
        var r = StagingSimulation.Simulate(craft, Catalog);

        Assert.Equal(2, r.Stages.Count);
        var (lower, upper) = (r.Stages[0], r.Stages[1]);
        Assert.Equal(5.05, r.LaunchMassT, 1e-9); // 0.5+1 + 1 + 0.05 + 0.5+1 + 1
        Assert.Equal(1, lower.Stage);
        Assert.Equal(4.05, lower.EndMassT, 1e-9); // only the lower tank's fuel burned (the decoupler blocks crossfeed)
        Assert.Equal(Dv(300, 5.05, 4.05), lower.DeltaVVacuum, 1e-6);
        Assert.Equal(Dv(250, 5.05, 4.05), lower.DeltaVSeaLevel, 1e-6);
        Assert.Equal(200 / (5.05 * 9.81), lower.TwrVacuum, 1e-9);
        Assert.Equal(1 / (200 / (300 * G0)), lower.BurnTimeS, 1e-6); // 1 t at ṁ = F / (Isp·g₀)
        Assert.Equal(0, upper.Stage);
        Assert.Equal(2.5, upper.StartMassT, 1e-9);
        Assert.Equal(Dv(300, 2.5, 1.5), upper.DeltaVVacuum, 1e-6);
        Assert.Equal(lower.DeltaVVacuum + upper.DeltaVVacuum, r.TotalDeltaVVacuum, 1e-9);
    }

    [Fact]
    public void Solid_boosters_are_dropped_when_they_burn_out()
    {
        // Core bigTank_1 + engine_2 from stage 1; two SRBs on radial decouplers (stage 0) burn alongside.
        var craft = new CraftFile("Boosters", "VAB", null,
        [
            P("bigTank_1", 0, -1, ["engine_2", "decoupler_3", "decoupler_5"]),
            P("engine_2", 1, -1),
            P("decoupler_3", 0, 0, ["srb_4"]),
            P("srb_4", 1, 0),
            P("decoupler_5", 0, 0, ["srb_6"]),
            P("srb_6", 1, 0),
        ]);
        var r = StagingSimulation.Simulate(craft, Catalog);

        var srbBurn = 0.75 / (100 / (200 * G0));       // each SRB: its own 0.75 t of solid fuel
        var withBoosters = r.Stages[0];
        Assert.Equal(srbBurn, withBoosters.BurnTimeS, 1e-6);
        var coreBurned = 200 / (300 * G0) * srbBurn;
        Assert.Equal(9.6 - 1.5 - coreBurned, withBoosters.EndMassT, 1e-9); // 5 + 1 + 2 × (0.05 + 1.75) at launch
        Assert.Equal((200 * 250.0 / 300 + 2 * 100 * 180.0 / 200) / (9.6 * 9.81), withBoosters.TwrSeaLevel, 1e-9); // thrust × Isp_ASL / Isp_vac

        var coreOnly = r.Stages[1];                      // boosters and decouplers (2 × 1.05 t) left behind
        Assert.Equal(withBoosters.EndMassT - 2.1, coreOnly.StartMassT, 1e-9);
        Assert.Equal(2, coreOnly.EndMassT, 1e-9);
        Assert.Equal(Dv(300, coreOnly.StartMassT, 2), coreOnly.DeltaVVacuum, 1e-6);
    }

    [Fact]
    public void Fuel_lines_drain_the_booster_tank_before_the_core()
    {
        // Asparagus: booster tank_4 feeds core tank_1 through fuelLine_6; both engines fire in stage 1.
        var craft = new CraftFile("Asparagus", "VAB", null,
        [
            P("tank_1", 0, -1, ["engine_2", "decoupler_3"]),
            P("engine_2", 1, -1),
            P("decoupler_3", 0, 0, ["tank_4"]),
            P("tank_4", 0, 0, ["engine_5", "fuelLine_6"]),
            P("engine_5", 1, 0),
            P("fuelLine_6", 0, 0, fuelLineTarget: "1"),
        ]);
        var r = StagingSimulation.Simulate(craft, Catalog);

        var first = r.Stages[0];
        Assert.Equal(1 / (2 * 200 / (300 * G0)), first.BurnTimeS, 1e-6); // both engines empty the booster tank together
        Assert.Equal(Dv(300, 5.1, 4.1), first.DeltaVVacuum, 1e-6);
        var second = r.Stages[1];
        Assert.Equal(2.5, second.StartMassT, 1e-9);                        // core tank still full when the booster drops
        Assert.Equal(Dv(300, 2.5, 1.5), second.DeltaVVacuum, 1e-6);
    }

    [Fact]
    public void Crossfeed_switched_on_lets_the_lower_engine_drain_the_upper_tank()
    {
        var craft = new CraftFile("Crossfeed", "VAB", null,
        [
            P("tank_1", 0, -1, ["engine_2", "decoupler_3"]),
            P("engine_2", 0, -1),
            P("decoupler_3", 0, 0, ["tank_4"], crossfeed: true),
            P("tank_4", 0, 0, ["engine_5"]),
            P("engine_5", 1, 0),
        ]);
        var r = StagingSimulation.Simulate(craft, Catalog);

        var stage = Assert.Single(r.Stages);                 // the upper engine is left with nothing
        Assert.Equal(3.05, stage.EndMassT, 1e-9);
        Assert.Equal(Dv(300, 5.05, 3.05), stage.DeltaVVacuum, 1e-6);
    }

    [Fact]
    public void Launch_clamps_are_not_part_of_the_launch_mass()
    {
        var craft = new CraftFile("Clamped", "VAB", null,
        [
            P("tank_1", 0, -1, ["engine_2", "clamp_3"]),
            P("engine_2", 1, -1),
            P("clamp_3", 1, 1),
        ]);
        var r = StagingSimulation.Simulate(craft, Catalog);
        Assert.Equal(2.5, r.LaunchMassT, 1e-9);
        Assert.Equal(Dv(300, 2.5, 1.5), Assert.Single(r.Stages).DeltaVVacuum, 1e-6);
    }

    [Fact]
    public void Reads_staging_links_fuel_lines_and_crossfeed_from_a_craft_file()
    {
        const string text = """
            ship = Tiny
            type = VAB
            PART
            {
                part = Decoupler.1_4294107924
                istg = 2
                sepI = 2
                modMass = 0.01
                link = fuelTank_4294129786
                MODULE
                {
                    name = ModuleToggleCrossfeed
                    crossfeedStatus = False
                }
            }
            PART
            {
                part = fuelTank_4294129786
                istg = 2
                sepI = 2
                RESOURCE
                {
                    name = LiquidFuel
                    amount = 45
                    maxAmount = 90
                }
            }
            PART
            {
                part = fuelLine_4293983122
                istg = 5
                sepI = 5
                PARTDATA
                {
                    tgt = 4294082098
                }
                MODULE
                {
                    name = CModuleFuelLine
                }
            }
            PART
            {
                part = liquidEngine3.v2_4294121630
                istg = 3
                sepI = 2
                MODULE
                {
                    name = ModuleEnginesFX
                    thrustPercentage = 80
                }
            }
            """;
        var craft = CraftReader.Parse(text);
        Assert.Equal(("Tiny", "VAB", 5), (craft.Name, craft.Facility, craft.FirstStage));
        var decoupler = craft.Parts[0];
        Assert.Equal(("Decoupler.1", 2, 2, 0.01, false), (decoupler.Name, decoupler.ActivationStage, decoupler.SeparationStage, decoupler.ExtraMassT,
            decoupler.CrossfeedSwitch!.Value));
        Assert.Equal(["fuelTank_4294129786"], decoupler.Children);
        Assert.Equal(new PartResource("LiquidFuel", 45, 90), craft.Parts[1].Resources[0]);
        Assert.Equal("4294082098", craft.Parts[2].FuelLineTarget);
        Assert.Equal(80, craft.Parts[3].Engines[0].ThrustPercent);
    }
}
