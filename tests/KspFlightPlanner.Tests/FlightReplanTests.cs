using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;
using Xunit.Abstractions;

namespace KspFlightPlanner.Tests;

public sealed class FlightReplanTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly CelestialBody _kerbin;

    public FlightReplanTests(ITestOutputHelper output)
    {
        _output = output;
        var db = new PlannerDatabase(_path);
        db.Initialize();
        var repo = new BodyRepository(db);
        _kerbin = repo.GetBodies(repo.GetSystems()[0].Id).Single(b => b.Name == "Kerbin");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private double Mu => _kerbin.Mu;

    private OrbitalElements Circular(double altitudeM, double inclinationDeg = 0, double meanAnomalyDeg = 0) =>
        new(_kerbin.EquatorialRadiusM + altitudeM, 0, inclinationDeg, 0, 0, meanAnomalyDeg * Math.PI / 180, 0);

    /// <summary>A rendezvous flight as the Flight pane stores it (burns titled and kinded as FlightViewModel.RendezvousBurns).</summary>
    private (Flight Flight, OrbitalElements Start, OrbitalElements Target) Planned(OrbitalElements chaser, OrbitalElements target)
    {
        var plan = Rendezvous.Plan(new RendezvousRequest(_kerbin, [new RendezvousTarget("Station", target)], 1000, 0, new RendezvousTarget("Ship", chaser)));
        var leg = plan.Legs[0];
        var flight = new Flight
        {
            Kind = FlightKind.Rendezvous,
            BodyName = "Kerbin",
            StartOrbit = chaser,
            Burns = leg.Maneuvers.Select(m => new FlightBurn($"{m.Kind}: Station", m.Ut, m.Burn) { Kind = m.Kind }).ToList(),
            Targets = [new FlightTarget("Station", null, target, leg.ArrivalUt)],
            PlannedUt = 1000,
        };
        _output.WriteLine(string.Join(", ", flight.Burns.Select(b => $"{b.KindOrFromTitle} {b.DeltaV:0.0} @ {b.Ut:0}")));
        return (flight, chaser, target);
    }

    /// <summary>The craft's orbit after flying <paramref name="burns"/>, the one at <paramref name="errorAt"/> off by <paramref name="error"/>.</summary>
    private OrbitalElements Flown(OrbitalElements start, IEnumerable<FlightBurn> burns, FlightBurn? errorAt = null, NodeBurn error = default) =>
        burns.OrderBy(b => b.Ut).Aggregate(start, (o, b) =>
        {
            var burn = b == errorAt ? new NodeBurn(b.Burn.Prograde + error.Prograde, b.Burn.Normal + error.Normal, b.Burn.Radial + error.Radial) : b.Burn;
            return Rendezvous.OrbitAfter(b.Ut, burn, o, Mu);
        });

    /// <summary>How far from the target the craft is at the meeting if it flies <paramref name="rest"/> (all but the match) exactly.</summary>
    private double MissAtMeeting(OrbitalElements craft, RestPlan rest, double now, OrbitalElements target)
    {
        var arrival = rest.Burns[^1].Ut;
        var after = Flown(craft, rest.Burns.Where(b => b.Ut >= now && b.KindOrFromTitle != ManeuverKind.MatchVelocity));
        return (Kepler.StateAt(after, Mu, arrival).Position - Kepler.StateAt(target, Mu, arrival).Position).Length;
    }

    [Fact]
    public void A_sloppy_transfer_burn_gets_a_course_correction()
    {
        var (flight, start, target) = Planned(Circular(80_000), Circular(150_000, meanAnomalyDeg: 60));
        var transfer = flight.Burns.Single(b => b.KindOrFromTitle == ManeuverKind.Transfer);
        var arrival = flight.Targets[0].ArrivalUt;
        var now = transfer.Ut + 300;
        var craft = Flown(start, flight.Burns.Where(b => b.Ut <= transfer.Ut), transfer, new NodeBurn(1.5, 0.5, 0));

        var rest = FlightReplan.RendezvousRest(flight, _kerbin, craft, _ => target, now, 120)!;
        _output.WriteLine(rest.Summary);

        var correction = Assert.Single(rest.Burns, b => b.Title.StartsWith("Course correction"));
        Assert.True(correction.Ut >= now + 120);
        Assert.True(correction.DeltaV < 10, $"correction {correction.DeltaV:0.0} m/s");
        Assert.Contains("without it you'd pass", rest.Summary);
        Assert.True(MissAtMeeting(craft, rest, now, target) < 5);
        Assert.Contains(rest.Burns, b => b.Ut == transfer.Ut); // the flown burn stays
        Assert.Equal(rest.Burns[^1].Ut, rest.Targets[0].ArrivalUt);
    }

    [Fact]
    public void A_sloppy_plane_match_re_solves_the_transfer_and_keeps_the_steps()
    {
        var (flight, start, target) = Planned(Circular(80_000), Circular(150_000, inclinationDeg: 3, meanAnomalyDeg: 200));
        var plane = flight.Burns.First();
        Assert.Equal(ManeuverKind.PlaneChange, plane.KindOrFromTitle);
        var arrival = flight.Targets[0].ArrivalUt;
        var now = plane.Ut + 200;
        var craft = Flown(start, [plane], plane, new NodeBurn(0, 3, 1));

        var rest = FlightReplan.RendezvousRest(flight, _kerbin, craft, _ => target, now, 120)!;
        _output.WriteLine(rest.Summary);

        Assert.Equal(flight.Burns.Count, rest.Burns.Count); // same steps, no correction added
        Assert.Equal(flight.Burns.Select(b => b.Title), rest.Burns.Select(b => b.Title));
        Assert.Equal(arrival, rest.Burns[^1].Ut, (arrival - now) * FlightReplan.TimeSlack * 1.5);
        Assert.True(rest.Burns[^2].DeltaV < flight.Burns[^2].DeltaV + 15, $"transfer {rest.Burns[^2].DeltaV:0.0} m/s");
        Assert.True(rest.Burns[^1].DeltaV < flight.Burns[^1].DeltaV + 15, $"match {rest.Burns[^1].DeltaV:0.0} m/s");
        Assert.True(MissAtMeeting(craft, rest, now, target) < 5);
    }

    [Fact]
    public void A_tilted_and_sloppy_transfer_burn_gets_a_small_correction()
    {
        // A tilt alone barely matters on a half-orbit transfer (the paths cross again at the far side); with a speed
        // error too, the correction has to fix both while the meeting is still nearly 180° away.
        var (flight, start, target) = Planned(Circular(80_000), Circular(150_000, meanAnomalyDeg: 60));
        var transfer = flight.Burns.Single(b => b.KindOrFromTitle == ManeuverKind.Transfer);
        var now = transfer.Ut + 210;
        var craft = Flown(start, flight.Burns.Where(b => b.Ut <= transfer.Ut), transfer, new NodeBurn(1.5, 4, 0.5));

        var rest = FlightReplan.RendezvousRest(flight, _kerbin, craft, _ => target, now, 120)!;
        _output.WriteLine(rest.Summary);

        var correction = Assert.Single(rest.Burns, b => b.Title.StartsWith("Course correction"));
        Assert.True(correction.DeltaV < 10, $"correction {correction.DeltaV:0.0} m/s");
        Assert.True(rest.Burns[^1].DeltaV < flight.Burns[^1].DeltaV + 10, $"match {rest.Burns[^1].DeltaV:0.0} m/s");
        Assert.True(MissAtMeeting(craft, rest, now, target) < 10);
    }

    [Fact]
    public void A_leg_not_started_yet_is_planned_again_in_full()
    {
        var (flight, start, target) = Planned(Circular(80_000), Circular(150_000, inclinationDeg: 3));
        Assert.Null(FlightReplan.RendezvousRest(flight, _kerbin, start, _ => target, 1000, 120));
    }

    [Fact]
    public void On_course_there_is_no_correction()
    {
        var (flight, start, target) = Planned(Circular(80_000), Circular(150_000, meanAnomalyDeg: 60));
        var transfer = flight.Burns.Single(b => b.KindOrFromTitle == ManeuverKind.Transfer);
        var now = transfer.Ut + 300;
        var craft = Flown(start, flight.Burns.Where(b => b.Ut <= transfer.Ut));

        var rest = FlightReplan.RendezvousRest(flight, _kerbin, craft, _ => target, now, 120)!;
        _output.WriteLine(rest.Summary);

        Assert.DoesNotContain(rest.Burns, b => b.Title.StartsWith("Course correction"));
        Assert.StartsWith("On course", rest.Summary);
        Assert.Equal(flight.Burns[^1].DeltaV, rest.Burns[^1].DeltaV, 0.5);
    }

    [Fact]
    public void Too_close_to_the_meeting_says_to_fly_by_eye()
    {
        var (flight, start, target) = Planned(Circular(80_000), Circular(150_000, meanAnomalyDeg: 60));
        var transfer = flight.Burns.Single(b => b.KindOrFromTitle == ManeuverKind.Transfer);
        var craft = Flown(start, flight.Burns.Where(b => b.Ut <= transfer.Ut), transfer, new NodeBurn(2, 0, 0));
        var now = flight.Targets[0].ArrivalUt - 200;
        var ex = Assert.Throws<ArgumentException>(() => FlightReplan.RendezvousRest(flight, _kerbin, craft, _ => target, now, 120));
        Assert.Contains("by eye", ex.Message);
    }

    [Fact]
    public void Old_flights_read_the_burn_kind_from_the_title()
    {
        Assert.Equal(ManeuverKind.Transfer, new FlightBurn("Transfer burn: KerbLink Mk2 B", 0, default).KindOrFromTitle);
        Assert.Equal(ManeuverKind.MatchVelocity, new FlightBurn("Match velocity: KerbLink Mk2 B", 0, default).KindOrFromTitle);
        Assert.Equal(ManeuverKind.Deorbit, new FlightBurn("Deorbit KerbLink", 0, default).KindOrFromTitle);
        Assert.Null(new FlightBurn("Circularize at apoapsis (250 km)", 0, default).KindOrFromTitle);
    }
}
