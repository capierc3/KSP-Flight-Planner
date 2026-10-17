using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.Tests;

public sealed class FlightMapTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly Dictionary<string, CelestialBody> _bodies;

    public FlightMapTests()
    {
        var db = new PlannerDatabase(_path);
        db.Initialize();
        var repo = new BodyRepository(db);
        _bodies = repo.GetBodies(repo.GetSystems()[0].Id).ToDictionary(b => b.Name);
    }

    public void Dispose()
    {
        TestDb.Release(_path);
        File.Delete(_path);
    }

    private CelestialBody? Body(string name) => _bodies.GetValueOrDefault(name);
    private CelestialBody? Parent(CelestialBody b) => _bodies.Values.FirstOrDefault(p => p.Id == b.ParentId);

    [Fact]
    public void A_flight_around_one_body_is_drawn_where_its_orbit_puts_it()
    {
        var kerbin = _bodies["Kerbin"];
        var orbit = new OrbitalElements(700_000, 0, 0, 0, 0, 0, 1_000_000);
        var samples = Enumerable.Range(0, 30).Select(k => MunTrip.Sample(kerbin, orbit, 1_000_000 + k * 20, 1_000_000)).ToList();
        var log = new FlightLog { VesselName = "Orbiter", Samples = samples };

        Assert.Equal(["Kerbin"], FlightMap.Views(log, Body, Parent));
        var map = FlightMap.Build(log, [], "Kerbin", Body, Parent)!;
        Assert.All(map.Segments.SelectMany(s => s), p => Assert.Equal(700_000, Math.Sqrt(p.X * p.X + p.Y * p.Y), 1e-3));
        Assert.Equal(samples.Count, Assert.Single(map.Segments).Count);
        Assert.Empty(map.Moons);
    }

    [Fact]
    public void A_mun_trip_shows_on_kerbins_map_with_the_mun_where_it_was()
    {
        var log = new FlightLog { VesselName = "Looper", LaunchUt = MunTrip.Start, Samples = MunTrip.Fly(_bodies["Kerbin"], _bodies["Mun"]) };
        var filled = FlightAnalysis.FillGaps(log.Samples, Body);

        Assert.Equal(["Kerbin", "Mun"], FlightMap.Views(log, Body, Parent));
        var map = FlightMap.Build(log, filled, "Kerbin", Body, Parent)!;
        var mun = _bodies["Mun"];

        // Every point drawn while around the Mun sits inside the Mun's sphere, wherever the Mun was then.
        var aroundMun = map.Segments.SelectMany(s => s).Where(p => p.Body == "Mun").ToList();
        Assert.NotEmpty(aroundMun);
        foreach (var p in aroundMun)
        {
            var ut = log.LaunchUt + p.MetS;
            var at = Kepler.StateAt(mun.Orbit!, _bodies["Kerbin"].Mu, ut).Position;
            Assert.True(Math.Sqrt(Math.Pow(p.X - at.X, 2) + Math.Pow(p.Y - at.Y, 2)) <= mun.SoiRadiusM * 1.01);
        }
        var moon = Assert.Single(map.Moons);
        Assert.Equal("Mun", moon.Name);
        Assert.Equal(2, moon.Seen.Count); // arrived, left
        Assert.Contains(map.Segments, s => s[^1].Leg == MapLeg.Powered);
        Assert.Contains(map.Segments, s => s[^1].Leg == MapLeg.Filled);
        Assert.Equal("Launch", map.Marks[0].Label);

        // Filling the way out, the craft is around Kerbin only until it reaches the Mun's sphere (where the Mun was then).
        var filledWithMoons = FlightAnalysis.FillGaps(log.Samples, Body, Parent);
        foreach (var f in filledWithMoons.Where(f => f.Body == "Kerbin" && f.Position is not null))
            Assert.True((f.Position!.Value - Kepler.StateAt(mun.Orbit!, _bodies["Kerbin"].Mu, f.Ut).Position).Length > mun.SoiRadiusM);

        var munMap = FlightMap.Build(log, filled, "Mun", Body, Parent)!;
        Assert.All(munMap.Segments.SelectMany(s => s), p => Assert.Equal("Mun", p.Body));
    }
}

/// <summary>A made-up Mun flyby with patched conics, for the map tests (and to look at in the app).</summary>
internal static class MunTrip
{
    public const double Start = 1_000_000;

    /// <summary>A coasting sample on <paramref name="orbit"/> at <paramref name="ut"/>.</summary>
    public static FlightSample Sample(CelestialBody body, OrbitalElements orbit, double ut, double launchUt, double thrustN = 0, double massKg = 5_000,
        double warp = 1)
    {
        var state = Kepler.StateAt(orbit, body.Mu, ut);
        return At(body, state, ut, launchUt, thrustN, massKg, warp);
    }

    private static FlightSample At(CelestialBody body, StateVector state, double ut, double launchUt, double thrustN, double massKg, double warp)
    {
        var e = Kepler.ElementsFrom(state, body.Mu, ut);
        var r = state.Position.Length;
        var situation = e.Eccentricity >= 1 || e.SemiMajorAxisM * (1 + e.Eccentricity) > body.SoiRadiusM ? "ESCAPING" : "ORBITING";
        return new FlightSample(ut, ut - launchUt, body.Name, situation, 1, r - body.EquatorialRadiusM, 0, state.Velocity.Length, 0,
            e.SemiMajorAxisM * (1 + e.Eccentricity) - body.EquatorialRadiusM, e.SemiMajorAxisM * (1 - e.Eccentricity) - body.EquatorialRadiusM,
            0, 0, 0, 90, thrustN > 0 ? 1 : 0, massKg, thrustN, thrustN > 0 ? 345 : 0, 0, 1, warp)
        {
            SemiMajorAxisM = e.SemiMajorAxisM,
            Eccentricity = e.Eccentricity,
            MeanAnomalyRad = e.MeanAnomalyAtEpochRad,
            InclinationDeg = e.InclinationDeg,
            LanDeg = e.LongitudeOfAscendingNodeDeg,
            ArgumentOfPeriapsisDeg = e.ArgumentOfPeriapsisDeg,
        };
    }

    /// <summary>
    /// 100 km parking orbit, a Hohmann burn timed to pass just ahead of the Mun, the flyby, and the way back, stepped every
    /// 30 s. Long coasts keep a sample every half hour at ×1,000 warp, like a warped flight.
    /// </summary>
    public static List<FlightSample> Fly(CelestialBody kerbin, CelestialBody mun)
    {
        const double r0 = 700_000;
        var munOrbit = mun.Orbit!;
        var rMun = munOrbit.SemiMajorAxisM;
        var aTransfer = (r0 + rMun) / 2;
        var half = Math.PI * Math.Sqrt(Math.Pow(aTransfer, 3) / kerbin.Mu);
        var n0 = Math.Sqrt(kerbin.Mu / Math.Pow(r0, 3));
        double Angle(Vector3d p) => Math.Atan2(p.Y, p.X);

        // Burn when the craft's opposite point is a little ahead of where the Mun will be at arrival (a flyby, not a hit).
        var burnUt = Enumerable.Range(0, 20_000).Select(k => Start + 600 + k * 10.0).MinBy(t =>
        {
            var craft = n0 * (t - Start) + Math.PI;
            var target = Angle(Kepler.StateAt(munOrbit, kerbin.Mu, t + half).Position) + 0.025;
            return Math.Abs(Math.IEEERemainder(craft - target, 2 * Math.PI));
        });

        var parking = new OrbitalElements(r0, 0, 0, 0, 0, 0, Start);
        var samples = new List<FlightSample>();
        for (var t = Start; t < burnUt; t += 30) samples.Add(Sample(kerbin, parking, t, Start, thrustN: t == Start ? 1 : 0));

        // The burn: 860 m/s prograde over 40 s (drawn as burning; the orbit after it is the impulsive one).
        var before = Kepler.StateAt(parking, kerbin.Mu, burnUt);
        var vTransfer = Math.Sqrt(kerbin.Mu * (2 / r0 - 1 / aTransfer));
        var after = new StateVector(before.Position, before.Velocity.Normalized * vTransfer);
        samples.Add(At(kerbin, before, burnUt, Start, 60_000, 5_000, 1));
        samples.Add(At(kerbin, after, burnUt + 1, Start, 60_000, 4_000, 1));

        var around = kerbin;
        var orbit = Kepler.ElementsFrom(after, kerbin.Mu, burnUt + 1);
        var lastKept = burnUt;
        for (var t = burnUt + 31; t < burnUt + 3 * 86_400; t += 30)
        {
            var state = Kepler.StateAt(orbit, around.Mu, t);
            var munState = Kepler.StateAt(munOrbit, kerbin.Mu, t);
            var changed = false;
            if (around == kerbin && (state.Position - munState.Position).Length < mun.SoiRadiusM)
            {
                around = mun;
                state = new StateVector(state.Position - munState.Position, state.Velocity - munState.Velocity);
                changed = true;
            }
            else if (around == mun && state.Position.Length > mun.SoiRadiusM)
            {
                around = kerbin;
                state = new StateVector(state.Position + munState.Position, state.Velocity + munState.Velocity);
                changed = true;
            }
            if (changed) orbit = Kepler.ElementsFrom(state, around.Mu, t);
            if (around == kerbin && state.Position.Length < kerbin.EquatorialRadiusM + 70_000) break; // back in the air
            if (around == mun && state.Position.Length < mun.EquatorialRadiusM) break;
            if (!changed && t - lastKept < 1_800) continue;
            samples.Add(At(around, state, t, Start, 0, 4_000, changed ? 1 : 1_000));
            lastKept = t;
        }
        return samples;
    }
}
