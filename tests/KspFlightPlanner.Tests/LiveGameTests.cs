using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.Tests;

public sealed class LiveGameTests
{
    private const double KerbinMu = 3.5316e12;
    private static double? Mu(string body) => body == "Kerbin" ? KerbinMu : null;

    private static SavedOrbit Orbit(double sma, double inc = 0.5, double lan = 280, double argPe = 160, double mna = 4.0, double epoch = 816_000, double ecc = 0.0027) =>
        new(sma, ecc, inc, lan, argPe, mna, epoch, 1, "Squad/Kerbin");

    private static SavedVessel Saved(string id, string name, string type, SavedOrbit orbit) => new(id, name, type, "ORBITING", orbit);

    private static KspSave Save(params SavedVessel[] vessels) => new("Protogen", "SCIENCE_SANDBOX", 816_500, vessels);

    [Fact]
    public void Outside_flight_only_the_game_time_changes()
    {
        var file = Save(Saved("a", "KerbLink Mk2 A", "Relay", Orbit(3_470_000)));

        var merge = LiveMerge.Apply(file, new LiveGame(817_361.9, null), null, Mu);

        Assert.Equal(817_361.9, merge.Save.Ut);
        Assert.Same(file.Vessels, merge.Save.Vessels);
        Assert.Empty(merge.Unmatched);
    }

    [Fact]
    public void Live_orbits_replace_the_saved_ones_matched_by_name_type_then_closest_orbit()
    {
        var file = Save(
            Saved("relay-low", "KerbLink", "Relay", Orbit(1_000_000)),
            Saved("relay-high", "KerbLink", "Relay", Orbit(3_500_000)),
            Saved("debris", "KerbLink", "Debris", Orbit(1_000_000)),
            Saved("other", "Hopper", "Ship", Orbit(700_000)));
        var highNow = Orbit(3_510_000, mna: 1.0, epoch: 817_000);
        var lowNow = Orbit(1_020_000, mna: 2.0, epoch: 817_000);
        var launched = new LiveVessel("Tripper Mk1", "Ship", "SUB_ORBITAL", Orbit(500_000)) { IsActive = true };
        var live = new LiveGame(817_100, [
            new LiveVessel("KerbLink", "Relay", "ORBITING", highNow),
            new LiveVessel("KerbLink", "Relay", "ORBITING", lowNow) { IsTarget = true },
            launched,
        ]);

        var merge = LiveMerge.Apply(file, live, null, Mu);

        var byId = merge.Save.Vessels.ToDictionary(v => v.Id);
        Assert.Same(highNow, byId["relay-high"].Orbit);
        Assert.Same(lowNow, byId["relay-low"].Orbit);
        Assert.Equal(1_000_000, byId["debris"].Orbit!.SemiMajorAxisM); // same name, other type: the relays took the live ones
        Assert.Equal(700_000, byId["other"].Orbit!.SemiMajorAxisM);     // not reported live: kept as saved
        Assert.Equal("relay-low", merge.SaveIds[live.Target!]);
        Assert.Equal([launched], merge.Unmatched);
        Assert.Same(launched, live.Active);
    }

    [Fact]
    public void A_coasting_orbit_restated_at_a_later_epoch_keeps_the_previous_object()
    {
        var before = Orbit(3_470_000, mna: 4.0, epoch: 816_000);
        var n = Math.Sqrt(KerbinMu / Math.Pow(3_470_000, 3));
        var restated = before with { MeanAnomalyRad = 4.0 + n * 1_000, EpochUt = 817_000 };
        var file = Save(Saved("a", "KerbLink Mk2 A", "Relay", before));
        var live = new LiveGame(817_050, [new LiveVessel("KerbLink Mk2 A", "Relay", "ORBITING", restated)]);

        var merge = LiveMerge.Apply(file, live, file, Mu);

        Assert.Same(before, merge.Save.Vessels[0].Orbit);
        Assert.True(LiveMerge.SamePath(before, restated, 817_050, Mu));
    }

    [Theory]
    [InlineData(3_470_100, 4.0)]  // 100 m higher
    [InlineData(3_470_000, 4.01)] // ~35 km further along
    public void A_changed_orbit_is_not_the_same_path(double sma, double mna)
    {
        var before = Orbit(3_470_000);
        Assert.False(LiveMerge.SamePath(before, before with { SemiMajorAxisM = sma, MeanAnomalyRad = mna }, 816_000, Mu));
    }

    [Fact]
    public void Circular_equatorial_orbits_compare_by_where_the_craft_is_not_by_undefined_angles()
    {
        var a = Orbit(700_000, inc: 0, lan: 10, argPe: 20, mna: 1.0, ecc: 0);  // periapsis longitude 30°
        var sameSpot = a with { LanDeg = 100, ArgumentOfPeriapsisDeg = 290 }; // 390° ≡ 30°, same mean anomaly
        var elsewhere = a with { LanDeg = 100 };                               // 120°: a quarter orbit ahead

        Assert.True(LiveMerge.SamePath(a, sameSpot, 816_000, Mu));
        Assert.False(LiveMerge.SamePath(a, elsewhere, 816_000, Mu));
    }
}
