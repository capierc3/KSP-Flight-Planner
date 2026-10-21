using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.Tests;

/// <summary>Flights through docking and undocking: who carries on, who waits docked, and how a vessel that came off is found again.</summary>
public sealed class FlightDockingTests : IDisposable
{
    private const double Launch = 1_000_000; // the shuttle's launch
    private const double StationLaunch = 900_000;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly PlannerDatabase _db;

    public FlightDockingTests()
    {
        _db = new PlannerDatabase(_path) { ActiveSave = "Protogen" };
        _db.Initialize();
    }

    public void Dispose()
    {
        TestDb.Release(_path);
        File.Delete(_path);
    }

    private static FlightSample At(double ut, double launchUt = Launch, string situation = "ORBITING", double massKg = 10_000, int stage = 0,
        double warp = 1) =>
        new(ut, ut - launchUt, "Kerbin", situation, stage, 100_000, 0, 2_246, 0, 100_500, 99_500, 0, 0, 0, 90, 0, massKg, 0, 0, 0, 0, warp);

    private static LiveVessel Live(ulong id, string name, double smaM = 700_000, double incDeg = 0, string type = "Ship") =>
        new(name, type, "ORBITING", new SavedOrbit(smaM, 0.001, incDeg, 0, 0, 0, 0, 1, "Squad/Kerbin")) { KrpcId = id };

    /// <summary>A shuttle flight docked into the station at <paramref name="dockUt"/>, as the recorder leaves it.</summary>
    private static FlightLog DockedShuttle(double dockUt, long stationLogId = 7)
    {
        var shuttle = new FlightLogRecorder("Shuttle", At(Launch + 100), null);
        shuttle.Add(At(dockUt - 10));
        shuttle.NoteDocking(dockUt, FlightLogEventKind.Docked, "Docked to Kerb Station", "Kerb Station", stationLogId, joined: true, asName: "Shuttle");
        return shuttle.Log with { Id = 3 };
    }

    // ---------------- Spotting it in the readings ----------------

    [Fact]
    public void A_sudden_mass_jump_without_staging_is_a_docking_or_an_undocking()
    {
        // Station of 30 t: a 5 t shuttle docking adds 5 t in one reading; fuel burned never moves it that fast.
        Assert.Equal(1, FlightDocking.MassJump(At(2000, massKg: 30_000), At(2000.5, massKg: 35_000)));
        Assert.Equal(-1, FlightDocking.MassJump(At(2000, massKg: 35_000), At(2000.5, massKg: 30_000)));
        Assert.Equal(0, FlightDocking.MassJump(At(2000, massKg: 30_000), At(2000.5, massKg: 29_990))); // RCS puffs
        Assert.Equal(0, FlightDocking.MassJump(At(2000, massKg: 35_000, stage: 2), At(2000.5, massKg: 30_000, stage: 1))); // staged
        // The real docking (2026-10-21): the station's stage count went 1 → 2 as the 3,737 kg module joined.
        Assert.Equal(1, FlightDocking.MassJump(At(2000, massKg: 23_997, stage: 1), At(2000.5, massKg: 27_734, stage: 2)));
        Assert.Equal(0, FlightDocking.MassJump(At(2000, massKg: 30_000), At(2010, massKg: 35_000))); // too far apart to tell
        Assert.Equal(0, FlightDocking.MassJump(At(2000, massKg: 30_000, warp: 100), At(2000.5, massKg: 35_000, warp: 100))); // on rails
    }

    [Fact]
    public void The_vessel_gone_from_beside_the_kept_one_is_the_one_that_docked()
    {
        var station = Live(1, "Kerb Station");
        var shuttle = Live(2, "Shuttle", smaM: 700_400);
        var relay = Live(3, "MinLink B", smaM: 1_200_000, incDeg: 6);
        var before = new[] { station, shuttle, relay };

        Assert.Equal([shuttle], FlightDocking.Gone(before, [station, relay], hostId: 1));
        // A vessel lost far away (another orbit) at the same time didn't dock here.
        Assert.Empty(FlightDocking.Gone(before, [station, shuttle], hostId: 1));
        // And one that undocks shows up beside it.
        Assert.Equal([shuttle], FlightDocking.Appeared([station, relay], before, hostId: 1));
    }

    [Fact]
    public void A_quickload_that_renumbers_every_vessel_is_not_a_docking()
    {
        var before = new[] { Live(1, "Kerb Station"), Live(2, "Shuttle") };
        var after = new[] { Live(11, "Kerb Station"), Live(12, "Shuttle") };
        Assert.Empty(FlightDocking.Gone(before, after, hostId: 11));
        Assert.Empty(FlightDocking.Appeared(before, after, hostId: 11));
    }

    [Fact]
    public void A_docked_vessel_s_log_is_found_by_its_kRPC_id_else_by_name()
    {
        var log = new FlightLogRecorder("Shuttle", At(Launch + 100), null).Log with { Id = 3 };
        var other = new FlightLogRecorder("Shuttle", At(Launch + 5000, launchUt: Launch + 4900), null).Log with { Id = 4 };
        var logs = new[] { log.Summary, other.Summary };

        Assert.Equal(3, FlightDocking.LogOf(Live(2, "Shuttle"), logs, new Dictionary<long, ulong> { [3] = 2 })?.Id);
        Assert.Equal(4, FlightDocking.LogOf(Live(2, "Shuttle"), logs, new Dictionary<long, ulong>())?.Id); // the latest of that name
        Assert.Equal(3, FlightDocking.LogOf(Live(2, "Ferry"), logs, new Dictionary<long, ulong>(), l => l.Id == 3 ? "Ferry" : null)?.Id); // renamed
        Assert.Null(FlightDocking.LogOf(Live(2, "Ferry"), logs, new Dictionary<long, ulong>()));
    }

    [Fact]
    public void A_new_launch_is_never_taken_for_an_undocked_vessel()
    {
        Assert.True(FlightDocking.JustLaunched(At(Launch + 2, situation: "FLYING")));
        Assert.False(FlightDocking.JustLaunched(At(Launch + 2, situation: "ORBITING"))); // came off in orbit
        Assert.False(FlightDocking.JustLaunched(At(Launch + 3600, situation: "FLYING"))); // flown later
    }

    [Fact]
    public void A_docking_that_changes_the_stage_count_is_not_noted_as_staging()
    {
        var station = new FlightLogRecorder("Kerb Station", At(Launch + 100, massKg: 23_997, stage: 1), null);
        station.Add(At(Launch + 100.5, massKg: 27_734, stage: 2));
        Assert.DoesNotContain(station.Log.Events, e => e.Kind == FlightLogEventKind.Stage);
    }

    [Fact]
    public void Events_come_out_in_time_order_whenever_they_were_noted()
    {
        var station = new FlightLogRecorder("Kerb Station", At(Launch + 100), null);
        station.NoteDocking(Launch + 500, FlightLogEventKind.Docked, "Module docked", "Module", 28, joined: false);
        station.Note(FlightLogEventKind.Returned, "Switched to another vessel", 300); // at the last reading, before the docking
        Assert.Equal(["Joined", "Returned", "Docked"], station.Log.Events.Select(e => e.Kind.ToString()));
    }

    // ---------------- The docked flight ----------------

    [Fact]
    public void A_flight_docked_into_another_vessel_waits_there_under_the_name_it_docked_as()
    {
        var log = DockedShuttle(Launch + 2000);
        Assert.True(log.Away);
        Assert.Equal("Kerb Station", log.DockedIn?.Partner);
        Assert.Equal(7, log.Summary.DockedIn?.PartnerLogId);
        Assert.Equal("Shuttle", FlightLogs.AwayName("Shuttle mk1", log.Events));

        Assert.Equal(3, FlightDocking.AwayLogNamed("Shuttle", [log.Summary])?.Id);
        Assert.Null(FlightDocking.AwayLogNamed("Kerb Station", [log.Summary]));
        // Not away: never picked up by name, and found by its own kRPC id / name instead.
        Assert.Null(FlightDocking.LogOf(Live(2, "Shuttle"), [log.Summary], new Dictionary<long, ulong>()));
    }

    [Fact]
    public void Undocking_gives_the_flight_the_new_vessel_s_launch_time_and_keeps_its_own_MET()
    {
        var dockUt = Launch + 2000;
        var undockUt = Launch + 50_000;
        var recorder = new FlightLogRecorder(DockedShuttle(dockUt));
        recorder.NoteDocking(undockUt, FlightLogEventKind.Undocked, "Undocked from Kerb Station", "Kerb Station", 7, joined: true,
            asName: "Shuttle", newLaunchUt: undockUt - 1);
        // KSP starts the undocked shuttle's MET again: 30 s after undocking it reads 31 s.
        recorder.Add(At(undockUt + 30, launchUt: undockUt - 1));
        var log = recorder.Log;

        Assert.False(log.Away);
        Assert.Null(log.DockedIn);
        Assert.Equal([Launch, undockUt - 1], FlightLogs.LaunchTimes(log.LaunchUt, log.Events));
        Assert.True(log.LaunchedAt(undockUt + 100, 101)); // found again by the new vessel's time
        Assert.True(log.LaunchedAt(Launch + 10, 10));
        Assert.Equal(undockUt + 30 - Launch, log.Samples[^1].MetS); // the log's MET runs on from its launch

        // The time spent docked is a gap in the recording.
        var docked = log.Events.Single(e => e.Kind == FlightLogEventKind.Docked);
        Assert.Equal(undockUt - dockUt, docked.DurationS);
    }

    [Fact]
    public void Undocked_and_not_seen_since_is_still_away_until_flown()
    {
        var recorder = new FlightLogRecorder(DockedShuttle(Launch + 2000));
        recorder.NoteDocking(Launch + 9000, FlightLogEventKind.Undocked, "Undocked from Kerb Station: carried on as a new vessel",
            "Kerb Station", 7, joined: true, asName: "Shuttle");
        Assert.True(recorder.Log.Away);
        Assert.Null(recorder.Log.DockedIn);
        Assert.Equal(3, FlightDocking.AwayLogNamed("Shuttle", [recorder.Log.Summary])?.Id);
    }

    // ---------------- What the save says ----------------

    [Fact]
    public void Docking_ports_in_the_save_remember_the_vessels_docked_together()
    {
        var save = KspSaveReader.Parse("""
            GAME
            {
                FLIGHTSTATE
                {
                    VESSEL
                    {
                        pid = abc
                        name = Kerb Station
                        type = Station
                        sit = ORBITING
                        lct = 900000
                        PART
                        {
                            name = dockingPort2
                            MODULE
                            {
                                name = ModuleDockingNode
                                state = Docked (dockee)
                                DOCKEDVESSEL
                                {
                                    vesselName = Kerb Station
                                    vesselType = 6
                                    rootUId = 1
                                }
                            }
                        }
                        PART
                        {
                            name = dockingPort2
                            MODULE
                            {
                                name = ModuleDockingNode
                                state = Docked (docker)
                                DOCKEDVESSEL
                                {
                                    vesselName = Shuttle
                                    vesselType = 5
                                    rootUId = 2
                                }
                            }
                        }
                        PART
                        {
                            name = dockingPort2
                            MODULE
                            {
                                name = ModuleDockingNode
                                state = Ready
                                DOCKEDVESSEL
                                {
                                    vesselName = Old Tug
                                }
                            }
                        }
                    }
                }
            }
            """);
        Assert.Equal(new HashSet<string> { "Kerb Station", "Shuttle" }, save.Vessels[0].DockedNames);
    }

    private static KspSave Save(double ut, params SavedVessel[] vessels) => new("Protogen", "SANDBOX", ut, vessels);

    private static SavedVessel Station(params string[] docked) =>
        new("st", "Kerb Station", "Station", "ORBITING", null) { LaunchUt = StationLaunch, DockedNames = docked.ToHashSet() };

    [Fact]
    public void A_docked_flight_stays_open_while_the_station_holds_it_and_ends_the_way_the_station_did()
    {
        var log = DockedShuttle(Launch + 2000);

        Assert.Null(FlightLogs.EndedIn(log, Save(Launch + 5000, Station("Kerb Station", "Shuttle"))));
        Assert.Equal("st", FlightLogs.VesselOf(log.Summary, Save(Launch + 5000, Station("Kerb Station", "Shuttle")))?.Id);

        // The station was recovered (brought down with the shuttle aboard): the shuttle went with it.
        Assert.Equal(FlightLogOutcome.Recovered, FlightLogs.EndedIn(log, Save(Launch + 90_000), id => id == 7 ? FlightLogOutcome.Recovered : null));
        // Gone with no word on the station: lost, from where it was last seen.
        Assert.Equal(FlightLogOutcome.Lost, FlightLogs.EndedIn(log, Save(Launch + 90_000)));
    }

    [Fact]
    public void A_flight_that_undocked_is_its_new_vessel_in_the_save()
    {
        var undockUt = Launch + 50_000;
        var recorder = new FlightLogRecorder(DockedShuttle(Launch + 2000));
        recorder.NoteDocking(undockUt, FlightLogEventKind.Undocked, "Undocked", "Kerb Station", 7, joined: true, asName: "Shuttle", newLaunchUt: undockUt);
        var log = recorder.Log;
        var shuttle = new SavedVessel("sh", "Shuttle", "Ship", "ORBITING", null) { LaunchUt = undockUt };

        Assert.Null(FlightLogs.EndedIn(log, Save(undockUt + 100, Station("Kerb Station"), shuttle)));
        Assert.Equal("sh", FlightLogs.VesselOf(log.Summary, Save(undockUt + 100, Station(), shuttle))?.Id);
        Assert.Equal(FlightLogOutcome.Lost, FlightLogs.EndedIn(log, Save(undockUt + 100, Station())));
    }

    [Fact]
    public void A_docking_the_log_never_saw_is_found_from_the_station_s_ports()
    {
        // Docked while the planner was closed: no Docked event, and no vessel launched when the shuttle was.
        var log = new FlightLogRecorder("Shuttle", At(Launch + 100), null).Log with { Id = 3 };
        var save = Save(Launch + 5000, Station("Kerb Station", "Shuttle"));
        Assert.Equal("st", FlightLogs.DockedUnseen(log, save)?.Id);
        Assert.Null(FlightLogs.EndedIn(log, save));
        // Gone and remembered nowhere: lost after all.
        Assert.Null(FlightLogs.DockedUnseen(log, Save(Launch + 5000, Station("Kerb Station"))));
        Assert.Equal(FlightLogOutcome.Lost, FlightLogs.EndedIn(log, Save(Launch + 5000, Station("Kerb Station"))));
    }

    [Fact]
    public void A_save_written_before_the_docking_can_t_end_it()
    {
        var log = DockedShuttle(Launch + 2000);
        Assert.Null(FlightLogs.EndedIn(log, Save(Launch + 1995)));
    }

    // ---------------- Storage ----------------

    [Fact]
    public void Docking_events_are_stored_with_who_and_when()
    {
        var repo = new FlightLogRepository(_db);
        var log = DockedShuttle(Launch + 2000) with { Id = 0 };
        var id = repo.Save(log);
        var back = repo.Get(id)!;
        var docked = back.DockedIn!;
        Assert.Equal(("Kerb Station", 7L, true, "Shuttle"), (docked.Partner, docked.PartnerLogId!.Value, docked.Joined, docked.AsName));
        Assert.True(back.Away);
        Assert.True(repo.GetAll().Single(l => l.Id == id).DockedIn is not null);
    }
}
