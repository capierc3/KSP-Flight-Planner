using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Tests;

public sealed class FlightAnalysisTests
{
    private const double G0 = 9.80665;

    private static readonly CelestialBody Kerbin = new()
    {
        Name = "Kerbin",
        Mu = 3.5316e12,
        EquatorialRadiusM = 600_000,
        SoiRadiusM = 84_159_286,
    };

    private static readonly CelestialBody Mun = new()
    {
        Name = "Mun",
        Mu = 6.5138398e10,
        EquatorialRadiusM = 200_000,
        SoiRadiusM = 2_429_559,
    };

    private static CelestialBody? Body(string name) => name switch { "Kerbin" => Kerbin, "Mun" => Mun, _ => null };

    private static FlightSample At(double met, string situation, double massKg, double thrustN = 0, double ispS = 300, int stage = 1,
        string body = "Kerbin", double altitudeM = 0, double verticalSpeed = 0, double orbitalSpeed = 175, double surfaceSpeed = 0,
        double q = 0, double g = 1, double latitude = 0, double longitude = 0) =>
        new(met, met, body, situation, stage, altitudeM, surfaceSpeed, orbitalSpeed, verticalSpeed, 80_000, 75_000, latitude, longitude, 90, 90,
            thrustN > 0 ? 1 : 0, massKg, thrustN, thrustN > 0 ? ispS : 0, q, g, 1);

    private static FlightLog Log(params FlightSample[] samples) => new() { VesselName = "Kerbal X", Samples = samples };

    [Fact]
    public void Gravity_loss_is_gravity_along_the_path_and_the_rest_is_drag_and_steering()
    {
        // Straight up at 100 m/s for 2 s from the pad: sin γ = 1, g = μ/R² = 9.81 → 19.62 m/s lost to gravity.
        var log = Log(
            At(0, "FLYING", 10_000, thrustN: 200_000, verticalSpeed: 100, orbitalSpeed: 100),
            At(1, "FLYING", 9_932, thrustN: 200_000, verticalSpeed: 100, orbitalSpeed: 100, q: 5_000, altitudeM: 0),
            At(2, "ORBITING", 9_864, thrustN: 200_000, verticalSpeed: 110, orbitalSpeed: 110, altitudeM: 0));
        var ascent = FlightAnalysis.Analyze(log, Body).Ascent!;

        var g = Kerbin.Mu / (600_000.0 * 600_000);
        Assert.True(ascent.ReachedOrbit);
        Assert.Equal(2 * g, ascent.GravityLoss, 1e-9);
        Assert.Equal(10, ascent.SpeedGained, 1e-9);
        var spent = G0 * 300 * Math.Log(10_000 / 9_864.0);
        Assert.Equal(spent, ascent.DeltaVSpent, 1e-9);
        Assert.Equal(spent - 10 - 2 * g, ascent.DragAndSteeringLoss, 1e-9);
        Assert.Equal(5_000, ascent.MaxQPa);
        Assert.Equal(1, ascent.MaxQMetS);
    }

    [Fact]
    public void Burns_split_where_the_engines_stop_and_add_up_to_the_total()
    {
        var log = Log(
            At(0, "FLYING", 10_000, thrustN: 200_000, stage: 2),
            At(1, "FLYING", 9_932, thrustN: 200_000, stage: 2),
            At(1.5, "FLYING", 6_000, thrustN: 50_000, stage: 1), // staged mid-burn: still one burn
            At(2.5, "SUB_ORBITAL", 5_983, thrustN: 50_000, stage: 1),
            At(3, "SUB_ORBITAL", 5_983, stage: 1),
            At(60, "ORBITING", 5_983, stage: 1),
            At(70, "ORBITING", 5_983, stage: 1),
            At(70.5, "ORBITING", 5_966, thrustN: 50_000, stage: 1, body: "Kerbin"),
            At(71, "ORBITING", 5_949, thrustN: 50_000, stage: 1),
            At(71.5, "ORBITING", 5_949, stage: 1));
        var report = FlightAnalysis.Analyze(log, Body);

        Assert.Equal(2, report.Burns.Count);
        Assert.Equal((2, 1), (report.Burns[0].FirstStage, report.Burns[0].LastStage));
        Assert.Equal(3, report.Burns[0].DurationS);
        Assert.Equal(log.DeltaVUsed, report.Burns.Sum(b => b.DeltaV), 1e-9);
        Assert.Equal(log.DeltaVUsed, report.ByStage.Sum(s => s.DeltaV), 1e-9);
        Assert.Equal([2, 1], report.ByStage.Select(s => s.Stage));
        Assert.Equal(log.DeltaVUsed, report.DeltaVSoFar[^1], 1e-9);
    }

    [Fact]
    public void A_touchdown_takes_the_speeds_from_just_before_it_and_the_descent_from_orbit()
    {
        var log = Log(
            At(0, "FLYING", 10_000, thrustN: 1),
            At(5_000, "ORBITING", 4_000, body: "Mun", altitudeM: 20_000),
            At(5_100, "SUB_ORBITAL", 3_900, thrustN: 20_000, body: "Mun", altitudeM: 15_000),
            At(5_400, "FLYING", 3_500, thrustN: 20_000, body: "Mun", altitudeM: 10, verticalSpeed: -2.5, surfaceSpeed: 3),
            At(5_401, "LANDED", 3_500, body: "Mun", latitude: 2, longitude: 40));
        var touchdown = Assert.Single(FlightAnalysis.Analyze(log, Body).Touchdowns);

        Assert.Equal(("Mun", false), (touchdown.Body, touchdown.Splashed));
        Assert.Equal(-2.5, touchdown.VerticalSpeed);
        Assert.Equal(3, touchdown.SurfaceSpeed);
        Assert.Equal(5_000, touchdown.DescentStartMetS);
        Assert.Equal(G0 * 300 * Math.Log(4_000 / 3_500.0), touchdown.DeltaVSpent, 1e-6);
        Assert.Equal((2, 40), (touchdown.LatitudeDeg, touchdown.LongitudeDeg));
    }

    [Fact]
    public void A_flight_joined_in_orbit_has_no_ascent()
    {
        var log = Log(At(3_600, "ORBITING", 5_000)) with { JoinedAtMetS = 3_600 };
        Assert.Null(FlightAnalysis.Analyze(log, Body).Ascent);
    }

    private static FlightSample OnOrbit(double met, double a, double e, double meanAnomaly, string body = "Kerbin", string situation = "ORBITING",
        double massKg = 5_000, double q = 0) =>
        At(met, situation, massKg, body: body, q: q) with { SemiMajorAxisM = a, Eccentricity = e, MeanAnomalyRad = meanAnomaly };

    [Fact]
    public void A_warp_on_a_round_orbit_is_filled_at_its_height_and_speed()
    {
        // 100 km circular Kerbin orbit warped through for 1,000 s: every point at 100 km and √(μ/r) = 2,246.1 m/s.
        var filled = FlightAnalysis.FillGaps([OnOrbit(0, 700_000, 0, 0), OnOrbit(1_000, 700_000, 0, 1.43)], Body);

        Assert.Equal(99, filled.Count); // every 10 s
        Assert.All(filled, p => Assert.Equal(100_000, p.AltitudeM, 1e-6));
        Assert.All(filled, p => Assert.Equal(Math.Sqrt(3.5316e12 / 700_000), p.OrbitalSpeed, 1e-6));
        Assert.Equal(2_246.1, filled[0].OrbitalSpeed, 0.1);
        Assert.Equal(Enumerable.Range(1, 99).Select(k => k * 10.0), filled.Select(p => p.MetS));
    }

    [Fact]
    public void An_elliptic_orbit_reaches_its_apoapsis_half_an_orbit_on()
    {
        const double a = 1_000_000, e = 0.5;
        var half = Math.PI * Math.Sqrt(a * a * a / 3.5316e12); // 1,671.6 s
        var filled = FlightAnalysis.FillGaps([OnOrbit(0, a, e, 0), OnOrbit(half + 50, a, e, Math.PI)], Body);

        var top = filled.MaxBy(p => p.AltitudeM)!;
        Assert.Equal(a * (1 + e) - 600_000, top.AltitudeM, 50.0); // 900 km
        Assert.Equal(Math.Sqrt(3.5316e12 * (2 / 1.5e6 - 1 / a)), top.OrbitalSpeed, 0.5); // 1,085 m/s at apoapsis
    }

    [Fact]
    public void A_gap_across_a_change_of_body_is_filled_from_both_ends_up_to_the_boundary()
    {
        // Out of Kerbin on a long ellipse (apoapsis past the edge of its sphere), into the Mun on a hyperbola.
        var filled = FlightAnalysis.FillGaps([OnOrbit(0, 60_000_000, 0.98, 0), OnOrbit(400_000, -1_000_000, 1.5, 0, body: "Mun")], Body);

        var kerbin = filled.Where(p => p.Body == "Kerbin").ToList();
        var mun = filled.Where(p => p.Body == "Mun").ToList();
        Assert.NotEmpty(kerbin);
        Assert.NotEmpty(mun);
        Assert.All(kerbin, p => Assert.True(p.AltitudeM + 600_000 <= 84_159_286));
        Assert.All(mun, p => Assert.True(p.AltitudeM + 200_000 <= 2_429_559));
        Assert.True(kerbin[^1].Ut < mun[0].Ut);
        Assert.Equal(filled.OrderBy(p => p.Ut).Select(p => p.Ut), filled.Select(p => p.Ut));
        Assert.Equal(400_000 - 2_000, mun[^1].Ut); // a point every 400,000 / 200 s, the last one step before the sample
        Assert.True(mun[^1].AltitudeM > 300_000); // still above the periapsis the sample is at
    }

    [Fact]
    public void Gaps_with_a_burn_or_in_the_air_or_from_old_logs_are_left_empty()
    {
        Assert.Empty(FlightAnalysis.FillGaps([OnOrbit(0, 700_000, 0, 0), OnOrbit(1_000, 700_000, 0, 1.43, massKg: 4_000)], Body));
        Assert.Empty(FlightAnalysis.FillGaps([OnOrbit(0, 700_000, 0, 0, q: 50), OnOrbit(1_000, 700_000, 0, 1.43)], Body));
        Assert.Empty(FlightAnalysis.FillGaps([At(0, "ORBITING", 5_000), At(1_000, "ORBITING", 5_000)], Body)); // no orbit recorded
        Assert.Empty(FlightAnalysis.FillGaps([OnOrbit(0, 700_000, 0, 0), OnOrbit(20, 700_000, 0, 0.03)], Body)); // short: samples suffice
    }

    [Fact]
    public void Downrange_is_the_distance_along_the_surface()
    {
        var pad = At(0, "FLYING", 1, latitude: 0, longitude: -74.5);
        var later = At(60, "FLYING", 1, latitude: 0, longitude: -64.5);
        Assert.Equal(600_000 * 10 * Math.PI / 180, FlightAnalysis.Downrange(pad, later, 600_000), 1e-6); // 104.7 km
    }
}
