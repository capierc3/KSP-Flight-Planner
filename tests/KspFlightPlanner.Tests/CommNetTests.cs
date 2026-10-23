using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Tests;

/// <summary>Stock antennas and orbits, checked by hand (docs/MATH.md §7.7).</summary>
public sealed class CommNetTests
{
    private static readonly PartAntenna Communotron16 = new("DIRECT", 500e3, true, 1);
    private static readonly PartAntenna Hg55 = new("DIRECT", 15e9, true, 0.75);
    private static readonly PartAntenna Ra15 = new("RELAY", 15e9, true, 0.75);
    private static readonly PartAntenna ProbeCore = new("INTERNAL", 5e3, false, 0);
    private static readonly PartAntenna Dts = new("DIRECT", 2e9, false, 0.75);

    [Fact]
    public void Combinable_antennas_add_up_with_the_weighted_exponent()
    {
        // Two Communotron 16s (exponent 1): 500 km · (1,000 / 500)^1 = 1,000 km.
        Assert.Equal(1e6, CommNet.CombinedPower([Communotron16, Communotron16]), 1e-3);
        // Two HG-55s (exponent 0.75): 15 Gm · 2^0.75 = 25.227 Gm.
        Assert.Equal(15e9 * Math.Pow(2, 0.75), CommNet.CombinedPower([Hg55, Hg55]), 1);
        // A 16 with an HG-55: e = (0.5 Mm · 1 + 15 Gm · 0.75) / 15.0005 Gm = 0.75001; 15 Gm · (15.0005 / 15)^0.75001.
        var e = (500e3 * 1 + 15e9 * 0.75) / (15e9 + 500e3);
        Assert.Equal(15e9 * Math.Pow((15e9 + 500e3) / 15e9, e), CommNet.CombinedPower([Communotron16, Hg55]), 1);
    }

    [Fact]
    public void Antennas_that_dont_combine_count_alone_and_the_probe_core_only_without_others()
    {
        // The DTS-M1 doesn't combine: 2 Gm beats the two 16s' 1,000 km, and a second one adds nothing.
        Assert.Equal(2e9, CommNet.CombinedPower([Dts, Dts, Communotron16, Communotron16]), 1e-3);
        Assert.Equal(1e6, CommNet.CombinedPower([ProbeCore, Communotron16, Communotron16]), 1e-3);
        Assert.Equal(5e3, CommNet.CombinedPower([ProbeCore]), 1e-9);
        Assert.Equal(0, CommNet.CombinedPower([]));
    }

    [Fact]
    public void Only_relay_antennas_pass_signals_on_and_the_range_setting_scales_power()
    {
        Assert.Equal(15e9, CommNet.RelayPower([Ra15, Hg55]), 1e-3);
        Assert.Equal(0, CommNet.RelayPower([Hg55]));
        Assert.Equal(2 * 1e6, CommNet.CombinedPower([Communotron16, Communotron16], rangeModifier: 2), 1e-3);
    }

    [Fact]
    public void Range_is_the_geometric_mean_and_strength_falls_smoothly()
    {
        // A Communotron 16 to a level 1 tracking station: √(500 km · 2 Gm) = 31.62 Mm.
        Assert.Equal(Math.Sqrt(500e3 * 2e9), CommNet.Range(500e3, CommNet.TrackingStation(1)), 1e-3);
        Assert.Equal(250e9, CommNet.TrackingStation(3));
        Assert.Equal(100e9, CommNet.TrackingStation(2, dsnModifier: 2));
        Assert.Equal(1, CommNet.Strength(0, 1000));
        Assert.Equal(0.5, CommNet.Strength(500, 1000), 1e-12);
        Assert.Equal(0.104, CommNet.Strength(800, 1000), 1e-12); // x = 0.2: 0.04 × 2.6
        Assert.Equal(0, CommNet.Strength(1200, 1000));
    }

    private static readonly CelestialBody Kerbol = new() { Id = 1, Name = "Kerbol" };
    private static readonly CelestialBody Kerbin = new()
        { Id = 2, Name = "Kerbin", ParentId = 1, IsDefault = true, Orbit = new OrbitalElements(13_599_840_256, 0, 0, 0, 0, 0) };
    private static readonly CelestialBody Mun = new() { Id = 3, Name = "Mun", ParentId = 2, Orbit = new OrbitalElements(12_000_000, 0, 0, 0, 0, 0) };
    private static readonly CelestialBody Duna = new()
        { Id = 4, Name = "Duna", ParentId = 1, Orbit = new OrbitalElements(20_726_155_264, 0.051, 0.06, 135.5, 0, 0) };
    private static readonly CelestialBody Ike = new() { Id = 5, Name = "Ike", ParentId = 4, Orbit = new OrbitalElements(3_200_000, 0.03, 0.2, 0, 0, 0) };
    private static readonly CelestialBody[] System = [Kerbol, Kerbin, Mun, Duna, Ike];

    [Fact]
    public void Farthest_from_home_adds_apoapses_up_to_the_body_they_share()
    {
        Assert.Null(CommNet.FarthestFromHome(Kerbin, System));
        Assert.Equal(12_000_000, CommNet.FarthestFromHome(Mun, System)!.Value, 1e-3);
        Assert.Equal(13_599_840_256, CommNet.FarthestFromHome(Kerbol, System)!.Value, 1e-3);
        // Duna's apoapsis 20,726,155,264 × 1.051 = 21,783,189,182.5 m plus Kerbin's 13,599,840,256 m on the far side.
        Assert.Equal(35_383_029_438.5, CommNet.FarthestFromHome(Duna, System)!.Value, 1e-1);
        Assert.Equal(35_383_029_438.5 + 3_296_000, CommNet.FarthestFromHome(Ike, System)!.Value, 1e-1);
    }

    [Fact]
    public void The_save_gives_the_tracking_station_level_and_the_difficulty_modifiers()
    {
        const string career = """
            GAME
            {
                Mode = CAREER
                PARAMETERS
                {
                    DIFFICULTY
                    {
                        EnableCommNet = True
                    }
                    CommNetParams
                    {
                        rangeModifier = 1.5
                        DSNModifier = 2
                    }
                }
                SCENARIO
                {
                    name = ScenarioUpgradeableFacilities
                    SpaceCenter/TrackingStation
                    {
                        lvl = 0.5
                    }
                }
                FLIGHTSTATE
                {
                    UT = 0
                }
            }
            """;
        var commNet = KspSaveReader.Parse(career).CommNet;

        Assert.Equal(new SavedCommNet(true, 2, 1.5, 2), commNet);
        // Without the facilities scenario (sandbox, science) every building is upgraded.
        Assert.Equal(3, KspSaveReader.Parse(career.Replace("ScenarioUpgradeableFacilities", "Other")).CommNet.TrackingStationLevel);
    }
}
