using KspFlightPlanner.Core;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.Tests;

/// <summary>The Δv map built from body data (docs/MATH.md §6.1); worked examples match the guide.</summary>
public sealed class DeltaVMapGeneratorTests
{
    private static readonly List<Core.Models.CelestialBody> Bodies = BodySeeder.LoadBodies(BodyRepository.StockSystemName);
    private static readonly Dictionary<string, DvMapNodeSpec> Map =
        DeltaVMapGenerator.Generate(Bodies, "Kerbin").ToDictionary(n => n.Key);

    private static Core.Models.CelestialBody Body(string name) => Bodies.Single(b => b.Name == name);

    [Fact]
    public void Kerbin_ascent_is_the_ideal_climb_plus_the_loss_allowance()
    {
        var kerbin = Body("Kerbin");
        Assert.Equal(2398.2, DeltaVMapGenerator.IdealAscentDv(kerbin), 0.1);   // 2,573.1 − 174.9 rotation
        Assert.Equal(1000, DeltaVMapGenerator.AscentLossDv(kerbin), 0);       // 1 g, 1 atm
        Assert.Equal(3400, kerbin.SurfaceToLowOrbitDv);
        Assert.Equal(3400, Map["kerbin.lo"].Dv);
    }

    [Fact]
    public void Airless_landing_is_the_ideal_climb()
    {
        Assert.Equal(0, DeltaVMapGenerator.AscentLossDv(Body("Mun")));
        Assert.Equal(580, Map["mun.surface"].Dv);
        Assert.Equal(10_000, Map["mun.lo"].AltitudeM);
    }

    [Fact]
    public void Stars_have_no_surface_estimate_but_the_map_keeps_the_node()
    {
        Assert.Null(Body("Kerbol").SurfaceToLowOrbitDv);
        Assert.True(Map["kerbol.surface"].Dv > 60_000);
    }

    [Theory]
    [InlineData("kerbin.keo", 1100)]       // Hohmann 80 km → 2,863 km
    [InlineData("mun.intercept", 860)]     // Hohmann Δv₁ from 680 km to 12,000 km
    [InlineData("mun.lo", 310)]            // capture from v∞ ≈ 365 m/s
    [InlineData("kerbin.soi", 930)]        // ellipse out to the SOI edge
    [InlineData("duna.intercept", 140)]    // rest of the ejection for v∞ ≈ 1,040 m/s
    [InlineData("duna.elliptical", 250)]
    [InlineData("duna.lo", 360)]
    [InlineData("laythe.surface", 2830)]
    public void Edges_match_the_worked_examples(string key, double dv) => Assert.Equal(dv, Map[key].Dv);

    [Theory]
    [InlineData("minmus.intercept", 330)]  // 6° at the transfer's periapsis speed (3,200 m/s)
    [InlineData("eve.intercept", 430)]
    [InlineData("duna.intercept", 10)]
    public void Plane_changes_are_the_worst_case(string key, double dv) => Assert.Equal(dv, Map[key].PlaneChangeDv);

    [Fact]
    public void Tree_lists_parents_first_and_covers_every_stock_body()
    {
        var nodes = DeltaVMapGenerator.Generate(Bodies, "Kerbin");
        var seen = new HashSet<string>();
        foreach (var n in nodes)
        {
            Assert.True(n.ParentKey is null || seen.Contains(n.ParentKey), $"{n.Key} comes before its parent");
            Assert.True(seen.Add(n.Key), $"{n.Key} is listed twice");
            Assert.True(n.Dv >= 0 && n.PlaneChangeDv >= 0, n.Key);
        }
        Assert.Single(nodes, n => n.ParentKey is null);
        Assert.All(Bodies, b => Assert.Contains($"{b.Name.ToLowerInvariant()}.surface", seen));
    }

    [Fact]
    public void Moons_of_a_planet_hang_off_its_capture_ellipse()
    {
        Assert.Equal("duna.elliptical", Map["ike.intercept"].ParentKey);
        Assert.Equal("jool.elliptical", Map["laythe.intercept"].ParentKey);
        Assert.Equal("kerbin.lo", Map["mun.intercept"].ParentKey);
        Assert.Equal("dres.intercept", Map["dres.lo"].ParentKey); // no moons: straight to low orbit
    }

    [Fact]
    public void Aerobraking_and_jets_follow_the_atmospheres()
    {
        Assert.True(Map["mun.intercept"].AerobrakeUp);      // home through Kerbin's air
        Assert.True(Map["eve.elliptical"].AerobrakeDown);   // aerocapture at Eve
        Assert.False(Map["ike.lo"].AerobrakeDown);
        Assert.True(Map["laythe.surface"].JetEnginesPossible);
        Assert.False(Map["duna.surface"].JetEnginesPossible);
    }
}
