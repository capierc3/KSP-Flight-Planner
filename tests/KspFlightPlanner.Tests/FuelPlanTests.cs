using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.Tests;

public sealed class FuelPlanTests
{
    private readonly PartCatalog _catalog = PartCatalogReader.Parse(VesselAnalysisTests.Cache);

    /// <summary>An Ant on an FL-T50 holding <paramref name="liquidFuel"/> and <paramref name="oxidizer"/> units (it holds 18 and 22).</summary>
    private static SavedVessel Shuttle(double liquidFuel, double oxidizer) =>
        new("pid", "Tiny shuttle", "Ship", "ORBITING", null)
        {
            Parts =
            [
                new SavedPart("microEngine_v2", 0.02, [], [new SavedEngineState(100, false, false, false)], []),
                new SavedPart("miniFuelTank", 0.025, [new PartResource("LiquidFuel", liquidFuel, 18), new PartResource("Oxidizer", oxidizer, 22)], [], []),
            ],
        };

    private const double Exhaust = 315 * VesselAnalysis.G0;

    [Fact]
    public void A_trip_within_whats_aboard_needs_no_fuel_added()
    {
        var report = VesselAnalysis.Analyze(Shuttle(9, 11), _catalog);
        var refuel = FuelPlan.ForTrip(report, _catalog, 2_000)!;

        // 45 kg empty: 0.045 · (e^(2000 / 3089.1) − 1) = 41.0 kg, drawn 0.9 : 1.1 (both 5 kg a unit).
        var need = 0.045 * (Math.Exp(2_000 / Exhaust) - 1);
        Assert.Equal(need, refuel.NeedMassT, 6);
        Assert.Equal(["LiquidFuel", "Oxidizer"], refuel.Lines.Select(l => l.Resource).OrderBy(n => n));
        var lf = refuel.Lines.Single(l => l.Resource == "LiquidFuel");
        var ox = refuel.Lines.Single(l => l.Resource == "Oxidizer");
        Assert.Equal(need * 0.45 / 0.005, lf.Need, 3);
        Assert.Equal(need * 0.55 / 0.005, ox.Need, 3);
        Assert.Equal((9, 18), (lf.Have, lf.Capacity));
        Assert.False(refuel.NeedsFuel);
    }

    [Fact]
    public void A_longer_trip_says_how_much_to_pump_in_and_whether_the_tanks_hold_it()
    {
        var report = VesselAnalysis.Analyze(Shuttle(9, 11), _catalog);
        var refuel = FuelPlan.ForTrip(report, _catalog, 5_000)!;
        var lf = refuel.Lines.Single(l => l.Resource == "LiquidFuel");
        Assert.True(refuel.NeedsFuel);
        Assert.True(refuel.Fits);
        Assert.Equal(0.045 * (Math.Exp(5_000 / Exhaust) - 1) * 0.45 / 0.005 - 9, lf.Add, 3); // about 7.4 units

        var tooFar = FuelPlan.ForTrip(report, _catalog, 7_000)!;
        Assert.False(tooFar.Fits); // more than a full FL-T50
    }

    [Fact]
    public void A_craft_in_flight_checks_like_a_one_stage_design_and_each_phase_gets_its_fuel()
    {
        var vessel = Shuttle(18, 22);
        var report = VesselAnalysis.Analyze(vessel, _catalog);
        var design = FuelPlan.AsDesign(vessel, report, _catalog, 9.81);
        var stage = Assert.Single(design.Stages);
        Assert.Equal(report.DeltaVVacuum, stage.DeltaVVacuum, 6);
        Assert.Equal(18, stage.Burned["LiquidFuel"], 3); // a full FL-T50 burns dry
        Assert.Equal(22, stage.Burned["Oxidizer"], 3);

        var check = DesignCheck.Check([new PhaseNeed("Transfer", 500), new PhaseNeed("Capture", 1_000)], design);
        var fuel = FuelPlan.PerPhase(design, check, _catalog);

        // The transfer burns from the full 245 kg; the capture from what it leaves.
        var transfer = 0.245 * (1 - Math.Exp(-500 / Exhaust));
        var capture = (0.245 - transfer) * (1 - Math.Exp(-1_000 / Exhaust));
        Assert.Equal(transfer, fuel[0].MassT, 6);
        Assert.Equal(capture, fuel[1].MassT, 6);
        Assert.Equal(transfer * 0.45 / 0.005, fuel[0].Fuel.Single(f => f.Resource == "LiquidFuel").Units, 3);
    }
}
