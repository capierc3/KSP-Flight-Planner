using System.Numerics;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;
using Xunit.Abstractions;

namespace KspFlightPlanner.Tests;

public sealed class AerobrakingTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly CelestialBody _kerbin;

    public AerobrakingTests(ITestOutputHelper output)
    {
        _output = output;
        var db = new PlannerDatabase(_path);
        db.Initialize();
        var repo = new BodyRepository(db);
        _kerbin = repo.GetBodies(repo.GetSystems()[0].Id).First(b => b.Name == "Kerbin");
    }

    public void Dispose()
    {
        TestDb.Release(_path);
        File.Delete(_path);
    }

    // ── Curves and cubes ─────────────────────────────────────────────

    [Fact]
    public void Curve_passes_through_its_keys_and_is_flat_beyond_them()
    {
        var tip = AeroPhysics.Stock.Tip;
        Assert.Equal(1, tip.Evaluate(0), 6);
        Assert.Equal(2.83, tip.Evaluate(1.1), 6);
        Assert.Equal(4, tip.Evaluate(5), 6);
        Assert.Equal(4, tip.Evaluate(20), 6);
        Assert.InRange(tip.Evaluate(3), 2.83, 4);
    }

    [Fact]
    public void Reads_a_drag_cube_and_physics_cfg()
    {
        var cube = DragCube.Parse("Default, 0.5638,0.6296,0.8621, 0.5638,0.6296,0.8621, 1.213,0.6298,0.752, 1.213,0.7206,0.6559, 0.5638,0.6307,0.8621, 0.5638,0.6275,0.8621, 0,0.3159,-5.96E-08, 1.531,0.6757,1.531")!;
        Assert.Equal("Default", cube.Name);
        Assert.Equal(1.213, cube.Area[2]);
        Assert.Equal(0.7206, cube.Drag[3]);

        var physics = AeroPhysics.Parse("dragMultiplier = 6 // comment\nDRAG_TIP\n{\n\tkey = 0 2 0 0\n\tkey = 10 3 0 0\n}\n");
        Assert.Equal(6, physics.DragMultiplier);
        Assert.Equal(0.1, physics.DragCubeMultiplier); // not in the file: stock
        Assert.Equal(3, physics.Tip.Evaluate(10), 6);
    }

    /// <summary>A part that's a 1 m cube: area 1 on every face, drag 1 (the Cd curve's top).</summary>
    private static AeroData Blocks() => new(AeroPhysics.Stock, new Dictionary<string, PartAero>
    {
        ["block"] = new("block", [new DragCube("Default", [1, 1, 1, 1, 1, 1], [1, 1, 1, 1, 1, 1])],
            new Dictionary<string, Vector3> { ["top"] = Vector3.UnitY, ["bottom"] = -Vector3.UnitY }),
    }, "test");

    [Fact]
    public void One_block_nose_first_at_mach_10()
    {
        var shape = CraftAero.Build([new("a", "block", Quaternion.Identity, [])], 0, Blocks());
        // Front face: tip 4 × multiplier 0.85 at Mach 10; back face: tail 0.14 (about) × 0.85; four sides: surface × 0.85.
        // Each face's Cd is 1 (curve) ^ power = 1; then × 0.1 × 8.
        var p = AeroPhysics.Stock;
        var expected = (p.Tip.Evaluate(10) + p.Tail.Evaluate(10) + 4 * p.Surface.Evaluate(10)) * p.Multiplier.Evaluate(10) * 0.8;
        Assert.Equal(expected, shape.DragArea(10, AeroAttitude.NoseFirst), 6);
        Assert.Equal(shape.DragArea(10, AeroAttitude.NoseFirst), shape.DragArea(10, AeroAttitude.EnginesFirst), 6);
    }

    [Fact]
    public void Stacked_blocks_hide_the_faces_they_touch()
    {
        var one = CraftAero.Build([new("a", "block", Quaternion.Identity, [])], 0, Blocks());
        var two = CraftAero.Build(
        [
            new("a", "block", Quaternion.Identity, [new PartLink("bottom", "b")]),
            new("b", "block", Quaternion.Identity, [new PartLink("top", "a")]),
        ], 0, Blocks());
        // A stack of two seen end-on is still one tip and one tail, plus twice the sides.
        var p = AeroPhysics.Stock;
        var sides = 4 * p.Surface.Evaluate(10) * p.Multiplier.Evaluate(10) * 0.8;
        Assert.Equal(one.DragArea(10, AeroAttitude.NoseFirst) + sides, two.DragArea(10, AeroAttitude.NoseFirst), 6);
        // Side-on, the stack shows two blocks' worth of front.
        Assert.True(two.DragArea(10, AeroAttitude.SideOn) > 1.9 * one.DragArea(10, AeroAttitude.SideOn) - sides);
    }

    [Fact]
    public void Reads_stack_links_and_rotation_from_a_craft_file()
    {
        var craft = CraftReader.Parse("""
            ship = Test
            type = VAB
            PART
            {
            	part = mk1-3pod_4294216776
            	pos = 0,26,0
            	rot = 0,0,0,1
            	istg = -1
            	sepI = -1
            	attN = bottom,Tube2_4294194958_0|-0.318|0_0|-1|0_0|-0.318|0_0|-1|0
            	attN = top,Null_0_0|1.19|0_0|1|0_0|1.19|0_0|1|0
            }
            PART
            {
            	part = Tube2_4294194958
            	rot = 0,0.707106829,0,0.707106709
            	istg = -1
            	sepI = 1
            	attN = top2,mk1-3pod_4294216776_0|0|0_0|1|0_0|0|0_0|1|0
            }
            """);
        Assert.Equal([new PartLink("bottom", "Tube2_4294194958")], craft.Parts[0].StackLinks);
        Assert.Equal([new PartLink("top2", "mk1-3pod_4294216776")], craft.Parts[1].StackLinks);
        Assert.Equal(0.7071, craft.Parts[1].Rotation.Y, 3);
        Assert.Single(CraftAero.FinalStage(craft).Parts);
    }

    // ── Air ──────────────────────────────────────────────────────────

    [Fact]
    public void Air_density_comes_from_pressure_and_temperature()
    {
        var air = AirModel.For(_kerbin)!;
        // Sea level: 101,325 Pa; the wiki's 1.225 kg/m³ gives T = P·M/(ρ·R) ≈ 288 K.
        Assert.InRange(air.Density(0), 1.1, 1.3);
        // Thin air up high, where the wiki's own densities round to 0.
        Assert.InRange(air.Density(50_000), 1e-4, 4e-4);
        Assert.True(air.Density(45_000) > air.Density(50_000));
        Assert.Equal(0, air.Density(70_000));
        Assert.InRange(air.SpeedOfSound(0), 320, 360);
    }

    // ── Passes ───────────────────────────────────────────────────────

    [Fact]
    public void Without_drag_the_orbit_comes_out_unchanged()
    {
        var start = AerobrakeOrbit.FromApsides(_kerbin, 45_000, 11_400_000);
        var plan = Aerobraking.Plan(_kerbin, start, 10_000, DragTable.Constant(0), maxPasses: 1);
        var pass = Assert.Single(plan.Passes);
        Assert.Equal(PassOutcome.Orbiting, pass.Outcome);
        Assert.Equal(11_400_000, pass.ApoapsisAfterM!.Value, 1_000.0);
        Assert.Equal(45_000, pass.PeriapsisAfterM!.Value, 10.0);
        Assert.Equal(45_000, pass.LowestM, 50.0);
    }

    [Fact]
    public void Periapsis_above_the_air_has_no_passes()
    {
        var plan = Aerobraking.Plan(_kerbin, AerobrakeOrbit.FromApsides(_kerbin, 75_000, 500_000), 10_000, DragTable.Constant(10));
        Assert.Empty(plan.Passes);
        Assert.Contains("above the air", plan.Notes[0]);
    }

    /// <summary>
    /// Light drag, no spin: ln(v_in / v_out) ≈ (CdA / 2m) ∫ρ ds, and near periapsis ∫ρ ds ≈ ρ_p √(2π r_p H (1+e)/e) for an
    /// air thinning by e every H (docs/MATH.md §9.3). The sum the simulator does should land close to that.
    /// </summary>
    [Fact]
    public void Light_pass_matches_the_exponential_air_estimate()
    {
        var air = AirModel.For(_kerbin)!;
        const double pe = 50_000, ap = 11_400_000, mass = 100_000, area = 10;
        var plan = Aerobraking.Plan(_kerbin, AerobrakeOrbit.FromApsides(_kerbin, pe, ap, inclinationDeg: 90), mass, DragTable.Constant(area), 1);
        var pass = plan.Passes[0];

        var rp = _kerbin.EquatorialRadiusM + pe;
        var ra = _kerbin.EquatorialRadiusM + ap;
        var e = (ra - rp) / (ra + rp);
        var h = 1_000 / Math.Log(air.Density(pe - 500) / air.Density(pe + 500)); // local scale height
        var column = air.Density(pe) * Math.Sqrt(2 * Math.PI * rp * h * (1 + e) / e);
        var vp = Math.Sqrt(_kerbin.Mu * (2 / rp - 2 / (rp + ra)));
        var estimate = vp * (1 - Math.Exp(-area / (2 * mass) * column));
        _output.WriteLine($"H {h:N0} m, column {column:N2} kg/m², estimate {estimate:N2} m/s, simulated {pass.SpeedLost:N2} m/s");
        Assert.Equal(estimate, pass.SpeedLost, estimate * 0.1);
    }

    [Fact]
    public void Passes_lower_the_apoapsis_until_it_comes_down()
    {
        var plan = Aerobraking.Plan(_kerbin, AerobrakeOrbit.FromApsides(_kerbin, 35_000, 11_400_000), 80_000, DragTable.Constant(30));
        foreach (var p in plan.Passes)
            _output.WriteLine($"#{p.Number} Pe {p.PeriapsisM / 1000:N1} km, lowest {p.LowestM / 1000:N1} km, −{p.SpeedLost:N0} m/s, " +
                              $"{p.PeakG:N2} g, Ap after {p.ApoapsisAfterM / 1000:N0} km, Pe after {p.PeriapsisAfterM / 1000:N2} km, {p.Outcome}");
        Assert.True(plan.Passes.Count >= 2);
        Assert.Equal(PassOutcome.ComesDown, plan.Last!.Outcome);
        var orbiting = plan.Passes.Where(p => p.Outcome == PassOutcome.Orbiting).ToList();
        Assert.True(orbiting.Zip(orbiting.Skip(1)).All(x => x.Second.ApoapsisAfterM < x.First.ApoapsisAfterM));
        // Drag only ever lowers the periapsis: a little on a long orbit, more once the orbit is nearly round.
        Assert.All(orbiting, p => Assert.True(p.PeriapsisAfterM < p.PeriapsisM));
        Assert.InRange(plan.Passes[0].PeriapsisM - plan.Passes[0].PeriapsisAfterM!.Value, 0, 2_000);
        // Each pass bites harder than the one before.
        Assert.True(plan.Passes.Zip(plan.Passes.Skip(1)).All(x => x.Second.SpeedLost >= x.First.SpeedLost));
        Assert.Contains(plan.Notes, n => n.Contains($"after pass {plan.Last.Number - 1}"));
    }

    [Fact]
    public void Going_lower_takes_off_more_and_spin_helps_less_than_against_it()
    {
        double Lost(double pe, double inc) =>
            Aerobraking.Plan(_kerbin, AerobrakeOrbit.FromApsides(_kerbin, pe, 11_400_000, inc), 80_000, DragTable.Constant(30), 1).Passes[0].SpeedLost;
        Assert.True(Lost(40_000, 0) > Lost(45_000, 0));
        // Against the spin, the air meets the craft faster: more drag.
        Assert.True(Lost(45_000, 180) > Lost(45_000, 0));
    }

    [Fact]
    public void Arriving_too_fast_still_escapes()
    {
        var plan = Aerobraking.Plan(_kerbin, AerobrakeOrbit.FromExcessSpeed(60_000, 3_000), 80_000, DragTable.Constant(30));
        Assert.Equal(PassOutcome.Escaping, Assert.Single(plan.Passes).Outcome);
        Assert.Contains(plan.Notes, n => n.Contains("doesn't slow it enough"));
    }

    [Fact]
    public void Sweep_heights_span_most_of_the_air()
    {
        Assert.Equal([30_000, 35_000, 40_000, 45_000, 50_000, 55_000, 60_000, 65_000], Aerobraking.SweepHeights(70_000));
    }

    /// <summary>The worked example in docs/MATH.md §9.3.</summary>
    [Fact]
    public void Worked_example_mun_return_at_35_km()
    {
        var plan = Aerobraking.Plan(_kerbin, AerobrakeOrbit.FromApsides(_kerbin, 35_000, 11_400_000), 79_000, DragTable.Constant(32));
        foreach (var p in plan.Passes)
            _output.WriteLine($"#{p.Number} −{p.SpeedLost:N1} m/s, {p.PeakG:N2} g, Ap after {p.ApoapsisAfterM / 1000:N0} km, " +
                              $"Pe after {p.PeriapsisAfterM / 1000:N2} km, next in {p.PeriodAfterS / 3600:N2} h, {p.Outcome}");
        Assert.Equal(PassOutcome.Orbiting, plan.Passes[0].Outcome);
    }

    // ── Aims ─────────────────────────────────────────────────────────

    private AerobrakeOrbit MunReturn => AerobrakeOrbit.FromApsides(_kerbin, 45_000, 11_400_000);

    [Fact]
    public void Fewest_passes_is_the_lowest_that_survives_more_drag()
    {
        var drag = DragTable.Constant(32);
        var s = AerobrakeAims.FewestPasses(_kerbin, MunReturn, 79_000, drag)!;
        _output.WriteLine($"{s.PeriapsisM / 1000:N1} km: {s.Why}");
        Assert.InRange(s.PeriapsisM, 21_000, 69_500);
        // Safe with 30% more drag; 1 km lower isn't.
        var at = AerobrakeAims.At(_kerbin, MunReturn, s.PeriapsisM);
        Assert.NotEqual(PassOutcome.ComesDown, Aerobraking.Plan(_kerbin, at, 79_000, drag.Scaled(1.3), 1).Passes[0].Outcome);
        var lower = AerobrakeAims.At(_kerbin, MunReturn, s.PeriapsisM - 1_000);
        Assert.Equal(PassOutcome.ComesDown, Aerobraking.Plan(_kerbin, lower, 79_000, drag.Scaled(1.3), 1).Passes[0].Outcome);
        // It stops after the last pass that's safe: that one is, the next isn't.
        var stop = s.StopAfterPass!.Value;
        Assert.True(AerobrakeAims.SafeThrough(_kerbin, at, 79_000, drag, stop));
        Assert.False(AerobrakeAims.SafeThrough(_kerbin, at, 79_000, drag, stop + 1));
    }

    [Fact]
    public void Gentlest_is_the_highest_that_gets_down_in_time()
    {
        var drag = DragTable.Constant(32);
        var s = AerobrakeAims.Gentlest(_kerbin, MunReturn, 79_000, drag, 1_000_000, 10)!;
        _output.WriteLine($"{s.PeriapsisM / 1000:N1} km, {s.StopAfterPass} passes: {s.Why}");
        Assert.InRange(s.StopAfterPass!.Value, 1, 10);
        var plan = Aerobraking.Plan(_kerbin, AerobrakeOrbit.FromApsides(_kerbin, s.PeriapsisM, 11_400_000), 79_000, drag, 10, 1_000_000);
        Assert.True(plan.Last!.ApoapsisAfterM <= 1_000_000);
        // 1 km higher no longer makes it in 10 passes.
        var higher = Aerobraking.Plan(_kerbin, AerobrakeOrbit.FromApsides(_kerbin, s.PeriapsisM + 1_000, 11_400_000), 79_000, drag, 10, 1_000_000);
        Assert.True(higher.Last!.ApoapsisAfterM > 1_000_000);
    }

    [Fact]
    public void Target_orbit_stops_where_it_is_safe_and_burns_the_rest()
    {
        var drag = DragTable.Constant(32);
        var s = AerobrakeAims.TargetOrbit(_kerbin, MunReturn, 79_000, drag, 100_000, 80_000, 10)!;
        var at = AerobrakeAims.At(_kerbin, MunReturn, s.PeriapsisM);
        var plan = Aerobraking.Plan(_kerbin, at, 79_000, drag, s.StopAfterPass!.Value);
        _output.WriteLine($"{s.PeriapsisM / 1000:N1} km, stop after {s.StopAfterPass}: Ap {plan.Last!.ApoapsisAfterM / 1000:N1} km, " +
                          $"Pe {plan.Last.PeriapsisAfterM / 1000:N1} km, raise {s.RaiseBurn:N1} + lower {s.LowerBurn:N1} m/s, engines only {s.EnginesOnly:N1} m/s. {s.Why}");
        Assert.True(AerobrakeAims.SafeThrough(_kerbin, at, 79_000, drag, s.StopAfterPass.Value));
        Assert.Equal(s.StopApoapsisM!.Value, plan.Last.ApoapsisAfterM!.Value, 1.0);
        Assert.True(s.StopApoapsisM >= 100_000 - 15_000);
        Assert.True(s.TotalBurn < s.EnginesOnly / 2);
    }

    [Fact]
    public void Target_orbit_high_enough_is_reached_by_the_passes()
    {
        var drag = DragTable.Constant(32);
        var s = AerobrakeAims.TargetOrbit(_kerbin, MunReturn, 79_000, drag, 2_000_000, 80_000, 10)!;
        _output.WriteLine($"{s.PeriapsisM / 1000:N1} km, {s.StopAfterPass} passes to {s.StopApoapsisM / 1000:N0} km, raise {s.RaiseBurn:N1} m/s. {s.Why}");
        Assert.Null(s.LowerBurn);
        Assert.Equal(2_000_000, s.StopApoapsisM!.Value, 60_000.0);
    }

    [Fact]
    public void Burns_by_vis_viva()
    {
        // At 100 km apoapsis, periapsis 40 km → 80 km.
        var raise = AerobrakeAims.RaiseBurn(_kerbin, 100_000, 40_000, 80_000);
        var mu = _kerbin.Mu;
        var expected = Math.Sqrt(mu * (2 / 700_000.0 - 2 / 1_380_000.0)) - Math.Sqrt(mu * (2 / 700_000.0 - 2 / 1_340_000.0));
        Assert.Equal(expected, raise, 1e-9);
        // Engines only, from the Mun: 3,0xx m/s at 80 km, down to the 80 × 100 km orbit's.
        var engines = AerobrakeAims.EnginesOnly(_kerbin, MunReturn, 100_000, 80_000);
        Assert.InRange(engines, 800, 900);
    }

    // ── Flying it (§9.5) ─────────────────────────────────────────────

    private static FlightSample Reading(double ut, PassPoint p, double massKg, string body = "Kerbin") =>
        new(ut, ut, body, "FLYING", 0, p.AltitudeM, 0, p.Speed, p.VerticalSpeed, double.NaN, double.NaN, 0, 0, 0, 0, 0, massKg, 0, 0, 0, 0, 1);

    [Fact]
    public void A_pass_continued_from_partway_ends_where_the_whole_pass_does()
    {
        var start = AerobrakeOrbit.FromApsides(_kerbin, 45_000, 11_400_000);
        var drag = DragTable.Constant(16);
        var whole = Aerobraking.Plan(_kerbin, start, 20_000, drag, 1).Passes[0];
        var trace = Aerobraking.Trace(_kerbin, start, 20_000, drag);
        // From 60 s in (on the way down), and from the lowest point.
        foreach (var at in new[] { trace[119], trace.MinBy(p => p.AltitudeM) })
        {
            var rest = Aerobraking.Continue(_kerbin, at.AltitudeM, at.Speed, at.VerticalSpeed, 0, 20_000, drag);
            _output.WriteLine($"from {at.AltitudeM / 1000:N1} km: Ap {rest.ApoapsisAfterM / 1000:N0} km (whole pass {whole.ApoapsisAfterM / 1000:N0} km)");
            Assert.Equal(PassOutcome.Orbiting, rest.Outcome);
            Assert.Equal(whole.ApoapsisAfterM!.Value, rest.ApoapsisAfterM!.Value, 0.01 * whole.ApoapsisAfterM.Value);
        }
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(0.8)]
    public void Tracker_measures_how_strong_the_drag_really_was(double really)
    {
        // The "game" flies a pass with the drag this much stronger than the model the tracker has.
        var start = AerobrakeOrbit.FromApsides(_kerbin, 45_000, 11_400_000);
        var model = DragTable.Constant(16);
        var flown = Aerobraking.Trace(_kerbin, start, 20_000, model.Scaled(really));
        var tracker = new AerobrakeTracker(_kerbin, 0, model);
        FlownPass? ended = null;
        foreach (var p in flown) ended = tracker.Add(Reading(1_000 + p.T, p, 20_000)) ?? ended;

        _output.WriteLine($"energy lost: game {tracker.EnergyLost.Measured:N0} J/kg, model {tracker.EnergyLost.Model:N0} J/kg; speed off {ended?.SpeedLost:N1} m/s, peak {ended?.PeakG:N2} g, Ap worked out {ended?.PredictedApoapsisM / 1000:N0} km");
        Assert.NotNull(ended);
        Assert.False(ended!.CameDown);
        Assert.Equal(really, ended.DragFactor!.Value, 0.02);
        Assert.Equal(really, tracker.DragFactor!.Value, 0.02);
        Assert.False(tracker.InAir);
        // The model's apoapsis going in was worked out with the model's drag: stronger drag, lower apoapsis than that.
        var modelAp = Aerobraking.Plan(_kerbin, start, 20_000, model, 1).Passes[0].ApoapsisAfterM!.Value;
        Assert.Equal(modelAp, ended.PredictedApoapsisM!.Value, 0.03 * modelAp);
    }

    [Fact]
    public void Tracker_ignores_readings_with_the_engines_on()
    {
        var start = AerobrakeOrbit.FromApsides(_kerbin, 45_000, 11_400_000);
        var model = DragTable.Constant(16);
        var tracker = new AerobrakeTracker(_kerbin, 0, model);
        foreach (var p in Aerobraking.Trace(_kerbin, start, 20_000, model))
            tracker.Add(Reading(p.T, p, 20_000) with { ThrustN = 50_000 });
        Assert.Null(tracker.DragFactor);
    }

    [Fact]
    public void Periapsis_set_on_the_way_in_with_a_burn_along_the_horizon()
    {
        // Back from the Mun: Ap 11,400 km, Pe 300 km, a third of the way in (true anomaly −120°).
        var r = _kerbin.EquatorialRadiusM;
        var (ra, rp) = (r + 11_400_000, r + 300_000);
        var orbit = new OrbitalElements((ra + rp) / 2, (ra - rp) / (ra + rp), 0, 0, 0, MeanAnomalyFor(-2.1, (ra - rp) / (ra + rp)));
        var burn = AerobrakeLive.SetPeriapsis(_kerbin, orbit, 0, 120, 47_300);
        var after = Rendezvous.OrbitAfter(burn.Ut, burn.Burn, orbit, _kerbin.Mu);
        _output.WriteLine($"{burn.Title}: {burn.DeltaV:N1} m/s at +{burn.Ut:N0} s");
        Assert.Equal(120, burn.Ut, 1e-6);
        Assert.Equal(47_300, Kepler.PeriapsisRadius(after) - r, 1.0);
        Assert.InRange(burn.DeltaV, 1, 200); // far out, moving the periapsis is cheap

        // Climbing away after a pass: at the apoapsis, which stays put.
        var outbound = orbit with { MeanAnomalyAtEpochRad = MeanAnomalyFor(1.0, orbit.Eccentricity) };
        var raise = AerobrakeLive.SetPeriapsis(_kerbin, outbound, 0, 120, 71_000);
        var raised = Rendezvous.OrbitAfter(raise.Ut, raise.Burn, outbound, _kerbin.Mu);
        Assert.True(raise.Ut > 1000);
        Assert.Equal(71_000, Kepler.PeriapsisRadius(raised) - r, 100.0);
        Assert.Equal(ra, raised.SemiMajorAxisM * (1 + raised.Eccentricity), 1_000.0);
    }

    /// <summary>Mean anomaly for a true anomaly (rad) on an ellipse.</summary>
    private static double MeanAnomalyFor(double nu, double e)
    {
        var ea = 2 * Math.Atan(Math.Sqrt((1 - e) / (1 + e)) * Math.Tan(nu / 2));
        return ea - e * Math.Sin(ea);
    }

    [Fact]
    public void Next_step_finishes_once_the_apoapsis_is_down_to_the_target()
    {
        var drag = DragTable.Constant(16);
        var down = AerobrakeOrbit.FromApsides(_kerbin, 47_000, 82_000);
        var step = AerobrakeLive.Next(_kerbin, down, 20_000, drag, AerobrakeAim.TargetOrbit, 0, 80_000, 80_000, 10);
        Assert.True(step.Finish);
        Assert.Equal(80_000, step.PeriapsisM);
        Assert.Null(step.ApoapsisM); // close enough: no burn to bring it down

        var high = AerobrakeOrbit.FromApsides(_kerbin, 47_000, 11_400_000);
        var go = AerobrakeLive.Next(_kerbin, high, 20_000, drag, AerobrakeAim.TargetOrbit, 0, 80_000, 80_000, 10);
        _output.WriteLine($"{go.PeriapsisM / 1000:N1} km: {go.Why}");
        Assert.False(go.Finish);
        Assert.InRange(go.PeriapsisM, 20_000, 70_000);
    }
}
