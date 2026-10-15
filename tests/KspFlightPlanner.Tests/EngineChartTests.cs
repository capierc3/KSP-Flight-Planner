using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.Tests;

/// <summary>Stock engine and tank figures, so each sizing can be checked by hand (docs/MATH.md §7.6).</summary>
public sealed class EngineChartTests
{
    private const double G0 = 9.80665;

    private static readonly Propellant[] Lfo = [new("LiquidFuel", 0.9), new("Oxidizer", 1.1)];

    private static KspPart Engine(string name, double mass, double cost, double thrust, double ispSea, double ispVac,
        Propellant[]? burns = null, PartResource[]? holds = null) =>
        new(name, name, "Engine", null, mass, cost, 0, holds ?? [], [new PartEngine(null, thrust, ispVac, ispSea, burns ?? Lfo)], []);

    private static KspPart Tank(string name, double mass, double cost, params PartResource[] holds) =>
        new(name, name, "FuelTank", null, mass, cost, 0, holds, [], []);

    private static readonly KspPart Terrier = Engine("Terrier", 0.5, 390, 60, 85, 345);
    private static readonly KspPart Reliant = Engine("Reliant", 1.25, 1100, 240, 265, 310);
    private static readonly KspPart Hammer = Engine("Hammer", 0.75, 400, 227, 170, 195, [new("SolidFuel", 1)], [new PartResource("SolidFuel", 375, 375)]);
    private static readonly KspPart FlT400 = Tank("FL-T400", 0.25, 500, new PartResource("LiquidFuel", 180, 180), new PartResource("Oxidizer", 220, 220));
    private static readonly KspPart Mk1Fuselage = Tank("Mk1 Fuselage", 0.25, 550, new PartResource("LiquidFuel", 400, 400));
    private static readonly KspPart HeavyLfo = Tank("Heavy LFO", 0.5, 400, new PartResource("LiquidFuel", 180, 180), new PartResource("Oxidizer", 220, 220));

    private static readonly PartCatalog Catalog = new([Terrier, Reliant, Hammer, FlT400, Mk1Fuselage, HeavyLfo],
    [
        new KspResource("LiquidFuel", "Liquid Fuel", 0.005, 0.8),
        new KspResource("Oxidizer", "Oxidizer", 0.005, 0.18),
        new KspResource("SolidFuel", "Solid Fuel", 0.0075, 0.6, "NO_FLOW"),
    ]);

    private static EngineChart Chart(params KspPart[] engines) => new(engines, [FlT400, Mk1Fuselage, HeavyLfo], Catalog);

    [Fact]
    public void The_tank_model_is_the_lightest_tank_holding_exactly_the_engines_mix()
    {
        var tanks = EngineChart.TankFor(Terrier.Engines[0], [Mk1Fuselage, HeavyLfo, FlT400], Catalog)!;

        Assert.Same(FlT400, tanks.Tank); // the LF-only fuselage doesn't fit an LF + Ox engine; the heavy tank loses
        Assert.Equal(0.125, tanks.DryPerTonne, 1e-12); // 0.25 t dry for 2 t of propellant
        Assert.Equal(250, tanks.CostPerTonne, 1e-12);
    }

    [Fact]
    public void A_liquid_stage_gets_just_the_propellant_and_tankage_the_dv_needs()
    {
        // 2 t payload, 1,000 m/s: r = e^(1000 / (345 g₀)) − 1 = 0.34389; m_p = r·(2 + 0.5) / (1 − r/8) = 0.8983 t.
        var option = Chart(Terrier).Best(new StageNeed(2, 1000, 0.5))!;

        var r = Math.Exp(1000 / (345 * G0)) - 1;
        Assert.Equal(1, option.Count);
        Assert.Equal(r * 2.5 / (1 - r / 8), option.PropellantT, 1e-9);
        Assert.Equal(0.8983469, option.PropellantT, 1e-6);
        Assert.Equal(0.1122934, option.TankDryT, 1e-6);
        Assert.Equal(3.5106403, option.TotalMassT, 1e-6);
        Assert.Equal(614.587, option.Cost, 1e-3); // √390 + √250 per tonne of propellant
        Assert.Equal(60 / (option.TotalMassT * 9.81), option.Twr, 1e-9);
        // Check the result flies: the rocket equation gives back the 1,000 m/s.
        Assert.Equal(1000, 345 * G0 * Math.Log(option.TotalMassT / (option.TotalMassT - option.PropellantT)), 1e-6);
    }

    [Fact]
    public void More_engines_are_added_until_the_twr_is_met()
    {
        // 10 t off Kerbin's pad, 1,000 m/s, TWR at least 1.3: one Reliant gives 1.27 at sea level, two give 2.29.
        var need = new StageNeed(10, 1000, 1.3, 9.81, 1);
        var option = Chart(Reliant).Best(need)!;

        Assert.Equal(2, option.Count);
        Assert.Equal(18.2576, option.TotalMassT, 1e-4);
        Assert.Equal(2.2909, option.Twr, 1e-4);
        Assert.Null(Chart(Reliant).Best(need with { MaxEngines = 1 }));
    }

    [Fact]
    public void Solid_boosters_carry_their_fuel_so_only_their_number_changes()
    {
        // 1 t payload: one Hammer gives 195 g₀ · ln((1 + 3.5625) / (1 + 0.75)) = 1,832 m/s, burning all 2.8125 t.
        var option = Chart(Hammer).Best(new StageNeed(1, 500, 1))!;

        Assert.True(option.IsSolid);
        Assert.Equal(1, option.Count);
        Assert.Equal(2.8125, option.PropellantT, 1e-9);
        Assert.Equal(1832.468, option.MaxDeltaV, 1e-3);
        Assert.Equal(4.5625, option.TotalMassT, 1e-9);
        // More Δv than one booster has: it takes two (2,254 m/s).
        Assert.Equal(2, Chart(Hammer).Best(new StageNeed(1, 2000, 1))!.Count);
    }

    [Fact]
    public void Nothing_can_give_more_dv_than_its_tanks_allow()
    {
        // With tanks at 1/8 of their propellant, Δv tops out at Isp·g₀·ln 9 = 7,434 m/s for the Terrier.
        Assert.Equal(7433.857, Chart(Terrier).Best(new StageNeed(1, 100, 0))!.MaxDeltaV, 1e-3);
        Assert.Null(Chart(Terrier).Best(new StageNeed(1, 7500, 0)));
    }

    [Fact]
    public void The_goal_picks_the_lightest_or_the_cheapest_stage()
    {
        // 5 t, 1,500 m/s in space: the Terrier makes the lighter stage, the cheaper engine wins on cost.
        var need = new StageNeed(5, 1500, 0.3);
        var chart = Chart(Terrier, Reliant);

        var options = chart.Options(need);
        Assert.Equal(["Terrier", "Reliant"], options.Select(o => o.Engine.Name));
        Assert.Equal("Terrier", chart.Best(need)!.Engine.Name);
        Assert.True(options[0].TotalMassT < options[1].TotalMassT);
        var cheapest = chart.Options(need, EngineChartGoal.CheapestStage);
        Assert.Equal(cheapest[0].Engine.Name, chart.Best(need, EngineChartGoal.CheapestStage)!.Engine.Name);
        Assert.True(cheapest[0].Cost <= cheapest[1].Cost);
    }

    [Fact]
    public void Thrust_falls_with_pressure()
    {
        Assert.Equal(60, EngineChart.ThrustAt(Terrier.Engines[0], 0), 1e-9);
        Assert.Equal(60 * 85 / 345.0, EngineChart.ThrustAt(Terrier.Engines[0], 1), 1e-9); // 14.8 kN at sea level
        Assert.Equal(0, EngineChart.ThrustAt(Terrier.Engines[0], 5)); // Eve's surface
    }
}
