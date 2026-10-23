using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;
using Xunit.Abstractions;

namespace KspFlightPlanner.Tests;

public sealed class AscentCoachTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly CelestialBody _kerbin;

    public AscentCoachTests(ITestOutputHelper output)
    {
        _output = output;
        var db = new PlannerDatabase(_path);
        db.Initialize();
        var repo = new BodyRepository(db);
        _kerbin = repo.GetBodies(repo.GetSystems()[0].Id).Single(b => b.Name == "Kerbin");
    }

    public void Dispose()
    {
        TestDb.Release(_path);
        File.Delete(_path);
    }

    [Fact]
    public void The_model_fitted_to_a_flight_flies_it_again_for_the_same_dv()
    {
        // A known rocket (drag area 1.5 m², Isp 285 s at sea level → 320 s in vacuum, two stages) flown with a known turn.
        var flight = SyntheticAscent.Fly(_kerbin, new PitchProgram(80, 45_000, 0.6), dragArea: 1.5);
        var report = FlightAnalysis.Analyze(flight, b => b == "Kerbin" ? _kerbin : null);
        var ascent = report.Ascent!;
        Assert.True(ascent.ReachedOrbit);

        var sim = AscentSimulator.For(flight, ascent, _kerbin)!;
        var subsonic = sim.DragArea.Take(3).Skip(1).Average();
        _output.WriteLine($"flown {ascent.DeltaVSpent:N0} m/s; fitted drag area {string.Join(", ", sim.DragArea.Take(10).Select(a => a.ToString("0.00")))}");
        Assert.InRange(subsonic, 1.35, 1.65); // within 10 % of the 1.5 m² flown

        var opt = sim.Optimize(ascent.DeltaVSpent);
        _output.WriteLine($"replay {opt.Replay!.DeltaV:N0} ({opt.ModelError:P1}); best {opt.Best.DeltaV:N0} with {opt.Best.Program}; saving {opt.Saving:N0}");
        Assert.True(opt.Replay.ReachedOrbit);
        Assert.InRange(opt.ModelError!.Value, -0.03, 0.03);
        Assert.True(opt.Best.ReachedOrbit);
        Assert.True(opt.Best.DeltaV <= opt.Replay.DeltaV + 1); // the search can always do at least as well as the flight
    }

    [Fact]
    public void A_steep_late_turn_is_called_out_and_a_better_one_found()
    {
        var late = SyntheticAscent.Fly(_kerbin, new PitchProgram(140, 30_000, 1.0), dragArea: 1.5, upperFlow: 25);
        var report = FlightAnalysis.Analyze(late, b => b == "Kerbin" ? _kerbin : null);
        var diagnosis = AscentCoach.Diagnose(late, report.Ascent, _kerbin)!;
        foreach (var a in diagnosis.Advice) _output.WriteLine($"[{a.Good}] {a.Title}: {a.Detail}");

        Assert.True(diagnosis.HasPitch);
        Assert.Contains(diagnosis.Advice, a => a.Title.StartsWith("Started the turn late") && a.Good == false);
        Assert.Contains(diagnosis.Advice, a => a.Title.StartsWith("45° came late") && a.Good == false);
        var opt = AscentSimulator.For(late, report.Ascent, _kerbin)!.Optimize(report.Ascent!.DeltaVSpent);
        _output.WriteLine($"flown {report.Ascent.DeltaVSpent:N0}, best {opt.BestEstimate:N0} ({opt.Best.Program})");
        Assert.True(opt.Best.ReachedOrbit);
        Assert.True(opt.Saving > 50);
    }

    [Fact]
    public void An_old_log_without_pitch_gets_no_pitch_advice_and_no_replay()
    {
        var flight = SyntheticAscent.Fly(_kerbin, new PitchProgram(80, 45_000, 0.6), dragArea: 1.5);
        var old = flight with { Samples = flight.Samples.Select(s => s with { SurfaceAttitude = false, PitchDeg = 15 }).ToList() };
        var report = FlightAnalysis.Analyze(old, b => b == "Kerbin" ? _kerbin : null);

        var diagnosis = AscentCoach.Diagnose(old, report.Ascent, _kerbin)!;
        Assert.False(diagnosis.HasPitch);
        Assert.Null(diagnosis.SteeringLoss);
        Assert.Contains(diagnosis.Advice, a => a.Title == "No pitch for this flight");
        Assert.DoesNotContain(diagnosis.Advice, a => a.Title.Contains("turn", StringComparison.OrdinalIgnoreCase) && a.Good is not null);

        var opt = AscentSimulator.For(old, report.Ascent, _kerbin)!.Optimize(report.Ascent!.DeltaVSpent);
        Assert.Null(opt.Replay);
        Assert.Null(opt.ModelError);
    }

    [Fact]
    public void Steering_loss_is_thrust_lost_to_pointing_off_the_path()
    {
        // 10° off prograde at 20 m/s² of thrust acceleration for 10 s: 20 × (1 − cos 10°) × 10 = 3.04 m/s.
        FlightSample At(double t) => new(t, t, "Kerbin", "FLYING", 1, 10_000, 500, 600, 250, 30_000, -500_000, 0, 0, 40, 90, 1,
            10_000, 200_000, 300, 5_000, 2, 1) { SurfaceAttitude = true };
        var samples = Enumerable.Range(0, 21).Select(k => At(k * 0.5)).Append(At(10.5) with { Situation = "ORBITING", ThrustN = 0 }).ToList();
        var log = new FlightLog { VesselName = "Test", Samples = samples };
        var ascent = FlightAnalysis.Analyze(log, b => b == "Kerbin" ? _kerbin : null).Ascent;
        var prograde = AscentCoach.ProgradePitch(samples[0]); // asin(250/500) = 30°
        Assert.Equal(30, prograde, 1e-9);

        var diagnosis = AscentCoach.Diagnose(log, ascent, _kerbin)!;
        Assert.Equal(20 * (1 - Math.Cos(10 * Math.PI / 180)) * 10 + 20 * (1 - Math.Cos(10 * Math.PI / 180)) * 0.25, diagnosis.SteeringLoss!.Value, 1e-6);
        Assert.Equal(10, diagnosis.MeanOffPrograde!.Value, 1e-9);
    }
}

/// <summary>
/// A climb to orbit flown with plain physics (thrust from a fixed fuel flow and a pressure-dependent Isp, drag = q·area,
/// gravity, a turning planet) and recorded as the planner would record it, to test the coach against known answers.
/// </summary>
internal static class SyntheticAscent
{
    public static FlightLog Fly(CelestialBody body, PitchProgram program, double dragArea, double upperFlow = 15)
    {
        const double g0 = 9.80665, dt = 0.02, every = 0.5;
        var mu = body.Mu;
        var radius = body.EquatorialRadiusM;
        var omega = 2 * Math.PI / body.RotationPeriodS;
        var atm = body.Atmosphere!;
        // Two stages: 30 t with 20 t of fuel at 160 kg/s (launch TWR 1.5), dropping 4 t of tank; then 6 t with 4 t of fuel
        // at 15 kg/s (or as given).
        (double Wet, double Fuel, double Flow, double Dry)[] stages = [(30_000, 20_000, 160, 4_000), (6_000, 4_000, upperFlow, 0)];
        static double Isp(double atmP) => 320 - 35 * Math.Min(1, atmP);
        double Density(double h) => (atm.PressureAtmAt(h) ?? 0) * 1.2;

        double x = radius + 70, y = 0, vx = 0, vy = omega * (radius + 70), t = 0, nextSample = 0;
        var stage = 0;
        var fuel = stages[0].Fuel;
        var mass = stages[0].Wet;
        double? turnFrom = null;
        var phase = 0;
        var samples = new List<FlightSample>();
        while (t < 1_500)
        {
            var r = Math.Sqrt(x * x + y * y);
            var (ux, uy) = (x / r, y / r);
            var (ex, ey) = (-uy, ux);
            var h = r - radius;
            if (h < 0) break; // fell back: a broken test rocket
            var (sx, sy) = (vx - omega * r * ex, vy - omega * r * ey);
            var surface = Math.Sqrt(sx * sx + sy * sy);
            var pressure = atm.PressureAtmAt(h) ?? 0;
            var q = 0.5 * Density(h) * surface * surface;
            var energy = (vx * vx + vy * vy) / 2 - mu / r;
            var a = -mu / (2 * energy);
            var hh = x * vy - y * vx;
            var e = Math.Sqrt(Math.Max(0, 1 + 2 * energy * hh * hh / (mu * mu)));
            var (ap, pe) = (a * (1 + e) - radius, a * (1 - e) - radius);
            if (phase == 0 && ap >= 80_000) phase = 1;
            if (phase == 1 && ux * vx + uy * vy <= 0) phase = 2;
            var done = phase == 2 && pe >= 72_000;

            var burning = phase != 1 && !done && fuel > 0;
            var pitch = 0.0;
            if (burning && phase == 0)
            {
                if (turnFrom is null && surface >= program.TurnStartSpeed) turnFrom = h;
                pitch = turnFrom is not { } from ? 90 : 90 * (1 - Math.Pow(Math.Clamp((h - from) / (program.TurnEndAltitudeM - from), 0, 1), program.Shape));
            }
            var p = pitch * Math.PI / 180;
            var (dx, dy) = phase == 2 ? (ex, ey) : (ux * Math.Sin(p) + ex * Math.Cos(p), uy * Math.Sin(p) + ey * Math.Cos(p));
            var thrust = burning ? stages[stage].Flow * g0 * Isp(pressure) : 0;

            if (t >= nextSample || done)
            {
                var vertical = ux * sx + uy * sy;
                var situation = phase == 2 && pe > atm.HeightM ? "ORBITING" : h > atm.HeightM ? "SUB_ORBITAL" : "FLYING";
                samples.Add(new FlightSample(1_000_000 + t, t, "Kerbin", situation, 1 - stage, h, surface, Math.Sqrt(vx * vx + vy * vy), vertical,
                    ap, pe, 0, 0, phase == 2 ? 0 : burning ? pitch : Math.Asin(Math.Clamp(vertical / Math.Max(1, surface), -1, 1)) * 180 / Math.PI, 90,
                    burning ? 1 : 0, mass, thrust, burning ? Isp(pressure) : 0, q, 1, 1) { SurfaceAttitude = true });
                nextSample += every;
            }
            if (done) break;

            var (axx, ayy) = (-mu / (r * r) * ux + thrust / mass * dx, -mu / (r * r) * uy + thrust / mass * dy);
            if (surface > 0)
            {
                axx -= q * dragArea / mass * sx / surface;
                ayy -= q * dragArea / mass * sy / surface;
            }
            vx += axx * dt;
            vy += ayy * dt;
            x += vx * dt;
            y += vy * dt;
            t += dt;
            if (burning)
            {
                var burned = stages[stage].Flow * dt;
                fuel -= burned;
                mass -= burned;
                if (fuel <= 0 && stage == 0)
                {
                    stage = 1;
                    mass = stages[1].Wet;
                    fuel = stages[1].Fuel;
                }
            }
        }
        return new FlightLog { VesselName = "Synthetic", LaunchUt = 1_000_000, Samples = samples };
    }
}
