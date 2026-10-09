using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.Tests;

public sealed class DesignCheckTests
{
    /// <summary>A design with just its stage Δv (the check only uses the vacuum Δv, in firing order, and the launch TWR).</summary>
    private static DesignReport Design(double launchTwr, params (int Stage, double DeltaV)[] stages) => new("Test", "VAB", 10, 10, 2,
        stages.Select(s => new StageResult(s.Stage, 10, 5, s.DeltaV, s.DeltaV, 60, 100, 90, launchTwr, launchTwr, [])).ToList(),
        [], [], [], []);

    // Worked example in docs/MATH.md §7.3: Beyonder Mk1's stages against a Mun landing and return, 10 % margins.
    [Fact]
    public void Stages_pour_into_phases_in_order_and_carry_over()
    {
        var beyonder = Design(1.55, (6, 2040), (5, 3322), (3, 1793));
        var r = DesignCheck.Check([new("Launch", 3400, 340), new("Transfer", 1750, 175), new("Lander", 890, 89)], beyonder);

        Assert.Equal(Coverage.Comfortable, r.Status);
        Assert.Equal([6, 5], r.Phases[0].Stages);     // 2,040 + 1,700 of stage 5
        Assert.Equal([5, 3], r.Phases[1].Stages);     // stage 5's other 1,622, then 303 of stage 3
        Assert.Equal([3], r.Phases[2].Stages);         // 979 more of stage 3
        Assert.Equal(3322 - 1700, r.Stages[1].Phases[1].DeltaV, 1e-9);
        Assert.Equal(1793 - 303 - 979, r.Stages[2].Spare, 1e-9);
        Assert.Equal(511, r.SpareOverMargins, 1e-9);   // 7,155 − 6,644
        Assert.True(r.CanLiftOff);
    }

    // The KerbLink Mk2 case: flies the route with 363 m/s left, but not every margin — still enough (it reached its orbit).
    [Fact]
    public void Using_part_of_the_margin_is_still_enough()
    {
        var kerbLink = Design(1.45, (2, 2831), (0, 2047));
        var r = DesignCheck.Check([new("Launch", 3400, 340), new("Transfer", 1115, 111.5)], kerbLink);

        Assert.Equal(Coverage.InsideMargin, r.Status);
        Assert.Equal(Coverage.Comfortable, r.Phases[0].Status);
        Assert.Equal(Coverage.InsideMargin, r.Phases[1].Status);
        Assert.Equal(0, r.Phases[1].Shortfall);
        Assert.Equal(4878 - 4515, r.SpareOverRequired, 1e-9);
        Assert.Equal(4878 - 4966.5, r.SpareOverMargins, 1e-9);
        Assert.Equal(4878 - 4515, r.Stages[^1].Spare, 1e-9); // the route-only pour is shown, with its spare
    }

    [Fact]
    public void Reports_the_shortfall_of_each_phase_before_margins()
    {
        var r = DesignCheck.Check([new("Launch", 3400, 340), new("Transfer", 1000, 100), new("Lander", 600, 60)], Design(0.9, (2, 3000), (1, 1200)));

        Assert.Equal(Coverage.Short, r.Status);
        // Without margins: 3,000 + 400 of stage 1 cover the launch; its other 800 go to the transfer; nothing is left for the lander.
        Assert.Equal(Coverage.Comfortable, r.Phases[0].Status);   // 3,740 with its margin fits in 4,200
        Assert.Equal(200, r.Phases[1].Shortfall, 1e-9);
        Assert.Equal(600, r.Phases[2].Shortfall, 1e-9);
        Assert.Equal(-800, r.SpareOverRequired, 1e-9);
        Assert.False(r.CanLiftOff);
    }

    [Fact]
    public void Phases_that_need_nothing_are_skipped()
    {
        var r = DesignCheck.Check([new("Launch", 1000), new("Empty", 0), new("Lander", 500)], Design(2, (1, 2000)));
        Assert.Empty(r.Phases[1].Stages);
        Assert.Equal([1], r.Phases[2].Stages);
        Assert.Equal(500, r.Stages[0].Spare, 1e-9);
        Assert.Equal(Coverage.Comfortable, r.Status);
    }

    // Worked example in docs/MATH.md §7.4. Lower stage: 1,000 m/s, 10 → 6 t, 200 kN vacuum / 150 kN at sea level.
    // Lander: 2,000 m/s, 4 → 2 t, 30 kN vacuum / 10 kN at sea level.
    private static DesignReport TwoStage() => new("Lander test", "VAB", 10, 10, 2,
    [
        new StageResult(1, 10, 6, 1000, 900, 60, 200, 150, 200 / (10 * 9.81), 150 / (10 * 9.81), []),
        new StageResult(0, 4, 2, 2000, 700, 120, 30, 10, 30 / (4 * 9.81), 10 / (4 * 9.81), []),
    ], [], [], [], []);

    [Fact]
    public void Surface_twr_uses_the_stage_firing_and_the_mass_left_at_that_moment()
    {
        var surfaces = new SurfaceEvent[]
        {
            new(SurfaceEventKind.LiftOff, 0, 0, "Kerbin", 9.81, 1),   // launch: first stage, full, sea-level thrust
            new(SurfaceEventKind.Landing, 1, 0, "Mun", 1.63),         // lower stage just used up → lander, full
            new(SurfaceEventKind.LiftOff, 1, 580, "Mun", 1.63),       // 580 m/s into the lander stage
            new(SurfaceEventKind.Landing, 1, 2500, "Duna", 2.94, 0.067), // past the end of the stages
        };
        var r = DesignCheck.Check([new("Launch", 1000), new("Lander", 1500)], TwoStage(), surfaces);

        var launch = r.Surfaces[0];
        Assert.Equal((1, 10.0), (launch.Stage!.Value, launch.MassT));
        Assert.Equal(150 / (10 * 9.81), launch.Twr, 1e-9);
        Assert.Equal(TwrRating.Good, launch.Rating);

        var landing = r.Surfaces[1];
        Assert.Equal((0, 4.0), (landing.Stage!.Value, landing.MassT));
        Assert.Equal(30 / (4 * 1.63), landing.Twr, 1e-9);

        var ascent = r.Surfaces[2];
        var mass = 4 * Math.Pow(2.0 / 4, 580.0 / 2000);           // constant Isp: mass falls exponentially with Δv
        Assert.Equal(mass, ascent.MassT, 1e-9);
        Assert.Equal(30 / (mass * 1.63), ascent.Twr, 1e-9);

        Assert.Equal(TwrRating.NoStage, r.Surfaces[3].Rating);
    }

    [Fact]
    public void Thrust_drops_with_surface_pressure_and_rates_low_and_too_low()
    {
        var lander = TwoStage().Stages[1];
        Assert.Equal(30, DesignCheck.ThrustAt(lander, 0), 1e-9);
        Assert.Equal(30 - 20 * 0.067, DesignCheck.ThrustAt(lander, 0.067), 1e-9); // Duna
        Assert.Equal(0, DesignCheck.ThrustAt(lander, 5), 1e-9);                     // Eve: 30 − 20 × 5 < 0

        SurfaceTwr Rated(double twr) => new(new SurfaceEvent(SurfaceEventKind.Landing, 0, 0, "X", 1), 0, 1, twr, twr);
        Assert.Equal(TwrRating.TooLow, Rated(0.95).Rating);
        Assert.Equal(TwrRating.Low, Rated(1.2).Rating);
        Assert.Equal(TwrRating.Good, Rated(1.3).Rating);
    }
}
