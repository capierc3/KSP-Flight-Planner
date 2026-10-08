using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.Tests;

public sealed class DesignCheckTests
{
    /// <summary>A design with just its stage Δv (the check only uses the vacuum Δv, in firing order, and the launch TWR).</summary>
    private static DesignReport Design(double launchTwr, params (int Stage, double DeltaV)[] stages) => new("Test", "VAB", 10, 10, 2,
        stages.Select(s => new StageResult(s.Stage, 10, 5, s.DeltaV, s.DeltaV, 60, 100, 90, launchTwr, launchTwr, [])).ToList(),
        [], [], [], []);

    // Worked example in docs/MATH.md §16c: Beyonder Mk1's stages against a Mun landing and return.
    [Fact]
    public void Stages_pour_into_phases_in_order_and_carry_over()
    {
        var beyonder = Design(1.55, (6, 2040), (5, 3322), (3, 1793));
        var r = DesignCheck.Check([new("Launch", 3740), new("Transfer", 1925), new("Lander", 979)], beyonder);

        Assert.True(r.IsEnough);
        Assert.Equal([6, 5], r.Phases[0].Stages);     // 2,040 + 1,700 of stage 5
        Assert.Equal([5, 3], r.Phases[1].Stages);     // stage 5's other 1,622, then 303 of stage 3
        Assert.Equal([3], r.Phases[2].Stages);         // 979 more of stage 3
        Assert.Equal(3322 - 1700, r.Stages[1].Phases[1].DeltaV, 1e-9);
        Assert.Equal(1793 - 303 - 979, r.Stages[2].Spare, 1e-9);
        Assert.Equal(511, r.Surplus, 1e-9);            // 7,155 − 6,644
        Assert.True(r.CanLiftOff);
    }

    [Fact]
    public void Reports_the_shortfall_of_each_phase()
    {
        var r = DesignCheck.Check([new("Launch", 3400), new("Transfer", 1000), new("Lander", 600)], Design(0.9, (2, 3000), (1, 1200)));

        Assert.False(r.IsEnough);
        Assert.Equal((3400, 3400.0, true), (r.Phases[0].Needed, r.Phases[0].Covered, r.Phases[0].IsCovered));
        // 3,000 + 400 of stage 1 cover the launch; stage 1's other 800 go to the transfer; nothing is left for the lander.
        Assert.Equal(200, r.Phases[1].Shortfall, 1e-9);
        Assert.Equal(600, r.Phases[2].Shortfall, 1e-9);
        Assert.Equal(-800, r.Surplus, 1e-9);
        Assert.False(r.CanLiftOff);
    }

    [Fact]
    public void Phases_that_need_nothing_are_skipped()
    {
        var r = DesignCheck.Check([new("Launch", 1000), new("Empty", 0), new("Lander", 500)], Design(2, (1, 2000)));
        Assert.Empty(r.Phases[1].Stages);
        Assert.Equal([1], r.Phases[2].Stages);
        Assert.Equal(500, r.Stages[0].Spare, 1e-9);
    }
}
