using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.Tests;

/// <summary>A small two-stage rocket with round masses and costs, so every figure can be checked by hand (docs/MATH.md §7.5).</summary>
public sealed class CraftBreakdownTests
{
    // pod: 0.8 t, √600, 1 seat, 14 m/s. tank: 0.5 t, √500 full (90 LF + 110 Ox = √91.8 of fuel), 6 m/s.
    // engine: 1 t, √1,000, 200 kN, Isp 300 / 250 s. decoupler: 0.05 t, √200, no crossfeed.
    private static readonly PartCatalog Catalog = new(
        [
            new KspPart("pod", "Pod", "Pods", null, 0.8, 600, 1, [], [], [], CrashToleranceMs: 14),
            new KspPart("tank", "Tank", "FuelTank", null, 0.5, 500, 0, [new("LiquidFuel", 90, 90), new("Oxidizer", 110, 110)], [], [],
                CrashToleranceMs: 6),
            new KspPart("engine", "Engine", "Engine", null, 1, 1000, 0, [],
                [new PartEngine(null, 200, 300, 250, [new("LiquidFuel", 0.9), new("Oxidizer", 1.1)])], [], CrashToleranceMs: 7),
            new KspPart("decoupler", "Decoupler", "Coupling", null, 0.05, 200, 0, [], [], [], FuelCrossfeed: false),
        ],
        [
            new KspResource("LiquidFuel", "Liquid Fuel", 0.005, 0.8),
            new KspResource("Oxidizer", "Oxidizer", 0.005, 0.18),
        ]);

    private static CraftPart P(string id, int stage, int sep, string[]? children = null, PartResource[]? resources = null)
    {
        var definition = Catalog.Part(CraftReader.PartName(id))!;
        return new CraftPart(id, definition.Name, stage, sep, 0, resources ?? definition.Resources,
            definition.Engines.Select(_ => new SavedEngineState(100, false, false, false)).ToList(), children ?? [], null, null);
    }

    private static (DesignReport Design, CraftBreakdown Breakdown) Build(CraftFile craft)
    {
        var design = StagingSimulation.Simulate(craft, Catalog);
        return (design, CraftBreakdown.Build(craft, Catalog, design));
    }

    // Pod on a full tank and engine (kept); a decoupler drops a half-full tank and its engine when stage 0 fires.
    private static readonly CraftFile TwoStage = new("Two stage", "VAB", null,
    [
        P("pod_1", -1, -1, ["tank_2"]),
        P("tank_2", -1, -1, ["engine_3"]),
        P("engine_3", 0, -1, ["decoupler_4"]),
        P("decoupler_4", 0, 0, ["tank_5"]),
        P("tank_5", 0, 0, ["engine_6"], [new("LiquidFuel", 45, 90), new("Oxidizer", 55, 110)]),
        P("engine_6", 1, 0),
    ]);

    [Fact]
    public void Cost_counts_what_the_tanks_actually_hold()
    {
        var (_, b) = Build(TwoStage);

        Assert.Equal(500, b.Parts.Single(p => p.Id == "tank_2").Cost, 1e-9);
        // √500 is with full tanks: take off √91.8 of fuel, add back the half loaded (√45.9).
        Assert.Equal(454.1, b.Parts.Single(p => p.Id == "tank_5").Cost, 1e-9);
        Assert.Equal(3754.1, b.Cost, 1e-9);
        Assert.Equal(2100, b.KeptCost, 1e-9);
    }

    [Fact]
    public void Parts_are_grouped_by_the_stage_that_drops_them_in_firing_order()
    {
        var (_, b) = Build(TwoStage);

        Assert.Equal([0, -1], b.Drops.Select(d => d.Stage));
        Assert.Equal(["decoupler_4", "tank_5", "engine_6"], b.Drops[0].Parts.Select(p => p.Id));
        Assert.Equal(2.05, b.Drops[0].MassT, 1e-9); // 0.05 + 0.5 + 0.5 of fuel + 1
        Assert.Equal(1654.1, b.Drops[0].Cost, 1e-9);
        Assert.True(b.Drops[1].StaysToTheEnd);
        Assert.Equal(3.3, b.Drops[1].MassT, 1e-9); // 0.8 + 0.5 + 1 of fuel + 1
    }

    [Fact]
    public void Mass_by_category_is_dry_mass_with_the_tanks_contents_apart()
    {
        var (design, b) = Build(TwoStage);

        Assert.Equal(["Engine", "FuelTank", "Pods", "Coupling"], b.Categories.Select(c => c.Category));
        Assert.Equal(2.0, b.Categories[0].DryMassT, 1e-9);
        Assert.Equal(2, b.Categories[0].Count);
        Assert.Equal(3.85, b.DryMassT, 1e-9);
        Assert.Equal(1.5, b.ResourceMassT, 1e-9);
        Assert.Equal(design.LaunchMassT, b.DryMassT + b.ResourceMassT, 1e-9);
    }

    [Fact]
    public void What_is_left_at_the_end_crew_and_the_weakest_part_that_lands()
    {
        var (_, b) = Build(TwoStage);

        Assert.Equal(2.3, b.FinalMassT, 1e-9); // pod + empty tank + engine once the upper stage has burned
        Assert.Equal(1, b.CrewCapacity);
        Assert.Equal("tank_2", b.WeakestKeptPart?.Id); // 6 m/s, under the engine's 7 and the pod's 14
        Assert.Empty(b.Warnings);
    }

    [Fact]
    public void Warns_about_an_engine_without_fuel_and_a_launch_stage_too_weak_to_lift_off()
    {
        // Two engines under a pod with no tank, then a lift stage too heavy for one engine: 4 full tanks on it.
        var craft = new CraftFile("Heavy", "VAB", null,
        [
            P("pod_1", -1, -1, ["engine_2"]),
            P("engine_2", 0, -1, ["decoupler_3"]),
            P("decoupler_3", 0, 0, ["tank_4"]),
            P("tank_4", 0, 0, ["tank_5"]),
            P("tank_5", 0, 0, ["tank_6"]),
            P("tank_6", 0, 0, ["tank_7"]),
            P("tank_7", 0, 0, ["tank_8"]),
            P("tank_8", 0, 0, ["tank_9"]),
            P("tank_9", 0, 0, ["tank_10"]),
            P("tank_10", 0, 0, ["tank_11"]),
            P("tank_11", 0, 0, ["engine_12"]),
            P("engine_12", 1, 0),
        ]);
        var (design, b) = Build(craft);

        // 0.8 + 1 + 0.05 + 8 × 1.5 + 1 = 14.85 t; 200 × 250/300 = 166.7 kN at sea level → TWR 1.14 … still flies
        Assert.Equal(166.6667 / (14.85 * 9.81), design.LaunchTwr, 1e-4);
        Assert.Single(b.Warnings);
        Assert.StartsWith("Engine never fires with fuel", b.Warnings[0]);

        // The lift engine's thrust limiter at 80 %: 133.3 kN → TWR 0.92.
        var limited = craft with { Parts = [.. craft.Parts.SkipLast(1), P("engine_12", 1, 0) with { Engines = [new SavedEngineState(80, false, false, false)] }] };
        var (_, worse) = Build(limited);
        Assert.Contains($"TWR at sea level is {133.3333 / (14.85 * 9.81):0.00}:", worse.Warnings[1]);
    }

    [Fact]
    public void A_payload_engine_dropped_before_its_stage_fires_is_a_note_not_a_warning()
    {
        // A satellite (pod + tank + engine lit in stage 0) released by a decoupler in stage 1, on the two-stage rocket's
        // upper stage. Its engine is gone before stage 0 fires: it's flown on its own.
        var craft = TwoStage with
        {
            Parts =
            [
                .. TwoStage.Parts.Select(p => p.Id == "pod_1" ? p with { Children = [.. p.Children, "decoupler_7"] } : p),
                P("decoupler_7", 1, 1, ["pod_8"]),
                P("pod_8", -1, 1, ["tank_9"]),
                P("tank_9", -1, 1, ["engine_10"]),
                P("engine_10", 0, 1),
            ],
        };
        var (_, b) = Build(craft);

        Assert.Empty(b.Warnings);
        Assert.StartsWith("Engine is dropped before its own stage fires", Assert.Single(b.Notes));
    }

    [Fact]
    public void Twr_on_another_body_uses_the_thrust_at_its_surface_pressure()
    {
        // The lander from DesignCheckTests: 4 t at the start, 30 kN vacuum / 10 kN at sea level.
        var stage = new StageResult(0, 4, 2, 2000, 0, 100, 30, 10, 0, 0, []);

        Assert.Equal(30 / (4 * 1.63), CraftBreakdown.TwrAt(stage, 1.63, 0), 1e-9);    // Mun, airless: 4.60
        Assert.Equal(28.66 / (4 * 2.94), CraftBreakdown.TwrAt(stage, 2.94, 0.067), 1e-9); // Duna: 2.44
        Assert.Equal(0, CraftBreakdown.TwrAt(stage, 16.7, 5));                          // Eve: no thrust left
    }
}
