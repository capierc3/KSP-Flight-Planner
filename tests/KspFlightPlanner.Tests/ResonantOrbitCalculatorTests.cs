using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Tests;

public class ResonantOrbitCalculatorTests
{
    // Values from the KSP Stock entry of the reference calculator's data.
    private static readonly CelestialBody Kerbin = new()
    {
        Name = "Kerbin",
        MassKg = 5.29151583439215E+22,
        Mu = 5.29151583439215E+22 * OrbitMath.G,
        EquatorialRadiusM = 600000,
        RotationPeriodS = 21549.4251830898,
        Atmosphere = new Atmosphere(HeightM: 70000),
        SoiRadiusM = 84159286.4796305,
    };

    [Fact]
    public void Kerbin_synchronous_altitude_matches_reference()
    {
        Assert.Equal(2_863_334, Kerbin.SynchronousAltitudeM!.Value, 0);
    }

    [Fact]
    public void Kerbin_three_sats_at_synchronous_matches_reference()
    {
        // Reference output: Ap 4,327,726.7 m, Pe 2,863,334 m, Δv 84.57 m/s,
        // periods 5h:59m:9.4s and 7h:58m:52.6s.
        var result = ResonantOrbitCalculator.Calculate(new(Kerbin, 3, 2_863_334));

        Assert.False(result.IsDiveOrbit);
        Assert.Equal(4_327_726.7, result.CarrierApoapsisAltitudeM, 0.5);
        Assert.Equal(2_863_334, result.CarrierPeriapsisAltitudeM, 0.01);
        Assert.Equal(84.57, result.InjectionDeltaV, 0.01);
        Assert.Equal(5 * 3600 + 59 * 60 + 9.4, result.SatellitePeriodS, 0.1);
        Assert.Equal(7 * 3600 + 58 * 60 + 52.6, result.CarrierPeriodS, 0.1);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Dive_orbit_keeps_apoapsis_and_lowers_periapsis()
    {
        var result = ResonantOrbitCalculator.Calculate(new(Kerbin, 3, 2_863_334, PreferDiveOrbit: true));

        Assert.True(result.IsDiveOrbit);
        Assert.Equal(2_863_334, result.CarrierApoapsisAltitudeM, 0.01);
        Assert.True(result.CarrierPeriapsisAltitudeM < 2_863_334);
        Assert.Equal(result.SatellitePeriodS * 2 / 3, result.CarrierPeriodS, 1e-6);
    }

    [Fact]
    public void Falls_back_to_dive_when_apoapsis_leaves_soi()
    {
        var result = ResonantOrbitCalculator.Calculate(new(Kerbin, 2, 60_000_000));

        Assert.True(result.IsDiveOrbit);
        Assert.Contains(result.Warnings, w => w.Contains("sphere of influence"));
    }

    [Theory]
    [InlineData(3, 600_000)]               // 1/cos(60°) = 2 → altitude equals the radius
    [InlineData(4, 248_528.137423857)]     // R(√2 − 1)
    public void Minimum_line_of_sight_altitude(int sats, double expected)
    {
        Assert.Equal(expected, OrbitMath.MinimumLineOfSightAltitude(600_000, sats)!.Value, 0.001);
    }

    [Fact]
    public void Occlusion_modifier_uses_atmospheric_value_for_kerbin()
    {
        var result = ResonantOrbitCalculator.Calculate(new(Kerbin, 3, 2_863_334, Occlusion: new OcclusionSettings(true)));
        Assert.Equal(600_000 * 0.75 * 2 - 600_000, result.MinimumLineOfSightAltitudeM!.Value, 0.001);
    }
}
