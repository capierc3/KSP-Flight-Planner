using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;
using Xunit.Abstractions;

namespace KspFlightPlanner.Tests;

public sealed class MissionFlightsTests : IDisposable
{
    private const double Now = 1_000_000;

    private readonly ITestOutputHelper _output;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly Dictionary<string, CelestialBody> _bodies;
    private readonly DeltaVMap _map;

    public MissionFlightsTests(ITestOutputHelper output)
    {
        _output = output;
        var db = new PlannerDatabase(_path);
        db.Initialize();
        var repo = new BodyRepository(db);
        _bodies = repo.GetBodies(repo.GetSystems()[0].Id).ToDictionary(b => b.Name);
        _map = new DvMapRepository(db).GetMap()!;
    }

    public void Dispose()
    {
        TestDb.Release(_path);
        File.Delete(_path);
    }

    private MissionPlanContext Context => new(_bodies, 120, InitialRotationDeg: _ => 90);

    private OrbitalElements Circular(string body, double altitudeM, double inclinationDeg = 0) =>
        new(_bodies[body].EquatorialRadiusM + altitudeM, 0, inclinationDeg, 0, 0, 0.3, Now);

    private IReadOnlyList<MissionStep> Steps(params MissionStop[] stops) =>
        MissionFlights.Steps(new MissionDefinition(stops, [], []), _map, _bodies, _ => null).Steps;

    private void Show(IReadOnlyList<MissionStep> steps, IReadOnlyList<MissionStepPlan> plans)
    {
        foreach (var p in plans)
        {
            _output.WriteLine($"{p.Step + 1}. {steps[p.Step].Title}: {p.DeltaV:0} m/s, from {p.StartBodyName}, ends {(p.EndUt - Now) / 3600:0.0} h" +
                              (p.Assumed ? " (assumed)" : "") + (p.Problem is { } x ? " — " + x : ""));
            foreach (var b in p.Burns) _output.WriteLine($"     {b.Title}: {b.DeltaV:0.0} m/s at +{(b.Ut - Now) / 3600:0.0} h");
        }
    }

    [Fact]
    public void A_moon_landing_and_return_becomes_launch_trip_land_launch_trip_and_reentry()
    {
        var steps = Steps(new("kerbin.surface"), new("minmus.surface", Landing: new LandingLeg(new LandingTarget("Flats", "Entered", 0.5, 30))),
            new("kerbin.surface"));
        Assert.Equal(
        [
            MissionStepKind.Launch, MissionStepKind.Trip, MissionStepKind.Land,
            MissionStepKind.Launch, MissionStepKind.Trip, MissionStepKind.Land,
        ], steps.Select(s => s.Kind));
        Assert.Equal("Minmus", steps[1].BodyName);
        Assert.Equal(_map["minmus.lo"].AltitudeM, steps[1].AltitudeM);
        // Home with nowhere in particular to land: straight into Kerbin's air, no capture.
        Assert.Equal(TransferFlights.AerobrakeFraction * _bodies["Kerbin"].AtmosphereHeightM, steps[4].AltitudeM, 1);
        Assert.Null(steps[5].Landing);
    }

    [Fact]
    public void An_orbit_change_is_only_a_step_when_the_launch_or_trip_doesnt_reach_the_stop()
    {
        Assert.Equal([MissionStepKind.Launch], Steps(new("kerbin.surface"), new("kerbin.lo")).Select(s => s.Kind));
        var keo = Steps(new("kerbin.surface"), new("kerbin.keo"));
        Assert.Equal([MissionStepKind.Launch, MissionStepKind.ChangeOrbit], keo.Select(s => s.Kind));
        Assert.Equal(_map["kerbin.keo"].AltitudeM, keo[1].AltitudeM);
        Assert.Equal([MissionStepKind.Trip], Steps(new("kerbin.lo"), new("mun.lo")).Select(s => s.Kind));
    }

    [Fact]
    public void A_constellation_is_released_after_getting_to_its_orbit()
    {
        var steps = Steps(new("kerbin.surface"), new("kerbin.keo", Deploy: new DeployStep(3)));
        Assert.Equal([MissionStepKind.Launch, MissionStepKind.ChangeOrbit, MissionStepKind.Deploy], steps.Select(s => s.Kind));
        Assert.Equal(3, steps[2].SatelliteCount);
    }

    [Fact]
    public void Steps_after_a_launch_are_planned_from_the_orbit_it_aims_for()
    {
        var steps = Steps(new("kerbin.surface"), new("minmus.lo"));
        var plans = MissionFlights.Plan(steps, 0, new MissionSituation("Kerbin", null, Landed: true, Now), Context);
        Show(steps, plans);
        Assert.Empty(plans[0].Burns);
        Assert.True(plans[0].Assumed);
        Assert.Equal(_bodies["Kerbin"].EquatorialRadiusM + steps[0].AltitudeM, plans[0].EndOrbit!.SemiMajorAxisM);
        Assert.True(plans[1].IsPlanned);
        Assert.True(plans[1].Assumed);
        Assert.True(plans[1].Burns[0].Ut >= plans[0].EndUt + Rendezvous.BurnGapS);
        Assert.Equal("Minmus", plans[1].EndBodyName);
    }

    [Fact]
    public void A_lower_orbit_than_planned_changes_the_trip_and_everything_after_it()
    {
        // Planned: launch to 80 km, then Minmus and a landing. Flown: the launch reached 75 km.
        var steps = Steps(new("kerbin.surface"), new("minmus.surface", Landing: new LandingLeg(new LandingTarget("Flats", "Entered", 0.5, 30))));
        var planned = MissionFlights.Plan(steps, 0, new MissionSituation("Kerbin", null, Landed: true, Now), Context);
        var real = Circular("Kerbin", 75_000);
        var here = new MissionSituation("Kerbin", real, Landed: false, Now + MissionFlights.AscentS);

        // The launch is done once in a stable orbit, so the trip is the step being flown.
        var current = MissionFlights.Current(steps, 0, here, planned, _bodies, 90);
        Assert.Equal(1, current);
        var replanned = MissionFlights.Plan(steps, current, here, Context);
        Show(steps, replanned);

        var trip = replanned[0];
        Assert.False(trip.Assumed);
        Assert.Equal(real, trip.StartOrbit);
        Assert.NotEqual(planned[1].Burns[0].Burn, trip.Burns[0].Burn);
        Assert.Equal(steps[1].AltitudeM, Kepler.PeriapsisRadius(trip.EndOrbit!) - _bodies["Minmus"].EquatorialRadiusM, 1_500.0);
        // The landing follows from where the new trip leaves the craft.
        var landing = replanned[1];
        Assert.True(landing.IsPlanned, landing.Problem);
        Assert.Equal(trip.EndOrbit, landing.StartOrbit);
        Assert.True(landing.Burns[0].Ut >= trip.EndUt + Rendezvous.BurnGapS);
        Assert.True(landing.EndsLanded);
    }

    [Fact]
    public void An_orbit_change_raises_then_rounds_then_tilts_when_asked()
    {
        var step = new MissionStep(MissionStepKind.ChangeOrbit, "", "Kerbin", 1) { AltitudeM = 250_000, InclinationDeg = 10 };
        var plans = MissionFlights.Plan([step], 0, new MissionSituation("Kerbin", Circular("Kerbin", 80_000), false, Now), Context);
        Show([step], plans);
        var plan = plans[0];
        Assert.Equal(3, plan.Burns.Count);
        Assert.StartsWith("Tilt", plan.Burns[2].Title);
        var end = plan.EndOrbit!;
        var r = _bodies["Kerbin"].EquatorialRadiusM;
        Assert.Equal(250_000, end.SemiMajorAxisM * (1 - end.Eccentricity) - r, 500.0);
        Assert.Equal(250_000, end.SemiMajorAxisM * (1 + end.Eccentricity) - r, 500.0);
        Assert.Equal(10, end.InclinationDeg, 1);
        Assert.Equal(1, MissionFlights.Current([step], 0, new MissionSituation("Kerbin", end, false, plan.EndUt + 100), plans, _bodies, 90));
    }

    [Fact]
    public void Going_lower_brings_the_periapsis_down_first()
    {
        var step = new MissionStep(MissionStepKind.ChangeOrbit, "", "Kerbin", 1) { AltitudeM = 100_000 };
        var plan = MissionFlights.Plan([step], 0, new MissionSituation("Kerbin", Circular("Kerbin", 400_000), false, Now), Context)[0];
        Assert.Equal(2, plan.Burns.Count);
        Assert.StartsWith("Lower periapsis", plan.Burns[0].Title);
        var r = _bodies["Kerbin"].EquatorialRadiusM;
        Assert.Equal(100_000, plan.EndOrbit!.SemiMajorAxisM - r, 500.0);
    }

    [Fact]
    public void A_step_that_cant_be_planned_holds_the_ones_after_it()
    {
        // Landing at a spot needs the body's rotation.
        var steps = Steps(new("kerbin.lo"), new("mun.surface", Landing: new LandingLeg(new LandingTarget("Crater", "Entered", 0, 0))), new("kerbin.lo"));
        var plans = MissionFlights.Plan(steps, 0, new MissionSituation("Kerbin", Circular("Kerbin", 80_000), false, Now), Context with { InitialRotationDeg = null });
        Show(steps, plans);
        Assert.True(plans[0].IsPlanned);
        Assert.Contains("rotation", plans[1].Problem);
        Assert.All(plans.Skip(2), p => Assert.Equal("Planned once step 2 is flown.", p.Problem));
    }

    [Fact]
    public void A_trip_is_done_once_around_the_destination_and_a_landing_once_on_its_ground()
    {
        var steps = Steps(new("kerbin.lo"), new("mun.surface"));
        Assert.Equal([MissionStepKind.Trip, MissionStepKind.Land], steps.Select(s => s.Kind));
        Assert.Equal(0, MissionFlights.Current(steps, 0, new MissionSituation("Kerbin", Circular("Kerbin", 80_000), false, Now), [], _bodies, 90));
        Assert.Equal(1, MissionFlights.Current(steps, 0, new MissionSituation("Mun", Circular("Mun", 14_000), false, Now), [], _bodies, 90));
        Assert.Equal(2, MissionFlights.Current(steps, 0, new MissionSituation("Mun", null, true, Now), [], _bodies, 90));
    }

    [Fact]
    public void Planning_from_the_start_takes_every_earlier_step_as_flown()
    {
        var steps = Steps(new("kerbin.surface"), new("mun.lo"), new("kerbin.surface"));
        var flight = new Flight { Kind = FlightKind.Mission, MissionStart = new MissionStart("Kerbin", null), Steps = steps, StepIndex = 2 };
        var planned = MissionFlights.PlanFromStart(flight, Now, Context);
        Assert.Equal(2, planned.StepPlans[0].Step);
        Assert.Equal("Mun", planned.BodyName);
        Assert.Equal(planned.StepPlans[0].Burns, planned.Burns);
    }
}
