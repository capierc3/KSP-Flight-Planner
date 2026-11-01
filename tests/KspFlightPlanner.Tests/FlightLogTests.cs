using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.Tests;

public sealed class FlightLogTests : IDisposable
{
    private const double Launch = 1_000_000;
    private const double G0 = 9.80665;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly PlannerDatabase _db;

    public FlightLogTests()
    {
        _db = new PlannerDatabase(_path) { ActiveSave = "Protogen" };
        _db.Initialize();
    }

    public void Dispose()
    {
        TestDb.Release(_path);
        File.Delete(_path);
    }

    private static FlightSample At(double met, string situation = "FLYING", double massKg = 10_000, double thrustN = 0, double ispS = 0,
        int stage = 2, string body = "Kerbin", double q = 0, double warp = 1, double altitudeM = 0) =>
        new(Launch + met, met, body, situation, stage, altitudeM, 0, 0, 0, double.NaN, double.NaN, 0, 0, 90, 90,
            thrustN > 0 ? 1 : 0, massKg, thrustN, ispS, q, 1, warp);

    // ---------------- Δv spent ----------------

    [Fact]
    public void Fuel_burned_between_two_samples_is_the_rocket_equation_however_far_apart()
    {
        // 10 t → 8 t on a 300 s engine: 9.80665 × 300 × ln(1.25) = 656.48 m/s. 200 kN at 300 s burns 68 kg/s, so 2 t takes ~29 s.
        var a = At(10, massKg: 10_000, thrustN: 200_000, ispS: 300);
        var b = At(39.4, massKg: 8_000, thrustN: 200_000, ispS: 300);
        Assert.Equal(G0 * 300 * Math.Log(1.25), FlightLogMath.DeltaVBetween(a, b), 1e-9);
        Assert.Equal(656.48, FlightLogMath.DeltaVBetween(a, b), 0.01);
    }

    [Fact]
    public void Parts_dropped_count_thrust_over_mass_instead_of_the_mass_drop()
    {
        // Staging: 10 t → 4 t in half a second. The mass drop would claim 2,696 m/s; thrust ÷ mass gives 8.1 m/s.
        var a = At(60, massKg: 10_000, thrustN: 200_000, ispS: 300, stage: 2);
        var b = At(60.5, massKg: 4_000, thrustN: 50_000, ispS: 320, stage: 1);
        Assert.Equal((20 + 12.5) / 2 * 0.5, FlightLogMath.DeltaVBetween(a, b), 1e-9);

        // Same stage, but a decoupler fired from an action group: far more gone than the engine could burn.
        var c = a with { Stage = 2 };
        var d = b with { Stage = 2, ThrustN = 200_000, IspS = 300 };
        Assert.Equal((20 + 50) / 2.0 * 0.5, FlightLogMath.DeltaVBetween(c, d), 1e-9);
    }

    [Fact]
    public void Coasting_spends_nothing()
    {
        Assert.Equal(0, FlightLogMath.DeltaVBetween(At(100, massKg: 8_000), At(160, massKg: 8_000)));
    }

    // ---------------- Recording ----------------

    [Fact]
    public void A_flight_from_the_launch_notes_staging_orbit_and_soi_and_thins_the_coast()
    {
        var recorder = new FlightLogRecorder("Kerbal X", At(0.4, thrustN: 200_000, ispS: 300, q: 10), flightId: 7);
        recorder.Add(At(1, massKg: 9_960, thrustN: 200_000, ispS: 300, q: 100));
        recorder.Add(At(60, "SUB_ORBITAL", massKg: 6_000, thrustN: 60_000, ispS: 320, stage: 1, altitudeM: 40_000));
        for (var t = 200.0; t <= 300; t += 0.5) recorder.Add(At(t, "ORBITING", massKg: 5_000, stage: 1, altitudeM: 80_000));
        recorder.Add(At(400, "ORBITING", massKg: 5_000, stage: 1, body: "Mun"));

        var log = recorder.Log;
        Assert.Equal(Launch, log.LaunchUt, 1e-9);
        Assert.True(log.FromLaunch);
        Assert.Equal(7, log.FlightId);
        Assert.Equal(
            [FlightLogEventKind.Launch, FlightLogEventKind.Stage, FlightLogEventKind.Situation, FlightLogEventKind.Situation, FlightLogEventKind.Soi],
            log.Events.Select(e => e.Kind));
        Assert.Equal("In orbit of Kerbin", log.Events[3].Text);
        // 200 s of coasting at 2 a second: the first sample, then one every 10 s (11), plus the one at the SOI change.
        Assert.Equal(3 + 11 + 1, log.Samples.Count);
        Assert.Equal(80_000, log.MaxAltitudeM);
        Assert.Equal("Kerbin › Mun", log.Route);
    }

    [Fact]
    public void Joining_a_flight_under_way_keeps_its_launch_time()
    {
        var log = new FlightLogRecorder("Relay 1", At(3_600, "ORBITING"), null).Log;
        Assert.False(log.FromLaunch);
        Assert.Equal(3_600, log.JoinedAtMetS);
        Assert.Equal(Launch, log.LaunchUt, 1e-9);
        Assert.Equal(FlightLogEventKind.Joined, log.Events.Single().Kind);
        Assert.True(log.IsFlightOf("Relay 1", Launch + 5_000.5, 5_000));
        Assert.False(log.IsFlightOf("Relay 1", Launch + 5_010, 5_000));
        Assert.False(log.IsFlightOf("Relay 2", Launch + 5_000, 5_000));
        Assert.True(log.LaunchedAt(Launch + 5_000, 5_000)); // renamed since
    }

    [Fact]
    public void A_vessel_released_in_orbit_is_a_separation_not_a_launch()
    {
        // KSP starts a split-off vessel's MET again: the third relay was first seen at MET 1.4 s, in orbit.
        var log = new FlightLogRecorder("Relay", At(1.36, "ORBITING"), null).Log;
        Assert.False(log.FromLaunch);
        Assert.Equal(0, log.LaunchMassKg);
        var joined = Assert.Single(log.Events);
        Assert.Equal(FlightLogEventKind.Joined, joined.Kind);
        Assert.Equal("Separated from another vessel (orbiting at Kerbin)", joined.Text);
        Assert.Equal(Launch, log.LaunchUt, 1e-9);

        Assert.True(new FlightLogRecorder("Kerbal X", At(0.4, thrustN: 1), null).Log.FromLaunch);
        Assert.StartsWith("Recording started in flight", new FlightLogRecorder("Relay", At(3_600, "ORBITING"), null).Log.Events[0].Text);
    }

    [Fact]
    public void Standing_still_on_the_ground_is_thinned_and_a_paused_game_adds_nothing()
    {
        var recorder = new FlightLogRecorder("Probe", At(100, "LANDED", q: 1.5e-7), null);
        for (var t = 100.5; t <= 120; t += 0.5) recorder.Add(At(t, "LANDED", q: 1.5e-7));
        for (var i = 0; i < 13; i++) recorder.Add(At(120.2, "LANDED", q: 1.5e-7)); // the recovery dialog
        recorder.Flush();
        Assert.Equal([100, 110, 120, 120.2], recorder.Log.Samples.Select(s => s.MetS));
    }

    [Fact]
    public void Rails_warp_is_noted_unless_short_and_its_samples_are_not_kept()
    {
        var recorder = new FlightLogRecorder("Kerbal X", At(100, "ORBITING"), null);
        recorder.Add(At(100.5, "ORBITING", warp: 5));
        recorder.Add(At(101, "ORBITING", warp: 1)); // ×5 for half a second: nothing to note
        recorder.Add(At(101, "ORBITING", warp: 100));
        recorder.Add(At(3_701, "ORBITING", warp: 1_000));
        recorder.Add(At(7_301, "ORBITING", warp: 1));
        recorder.Add(At(7_302, "ORBITING", warp: 4)); // physics warp: still recorded

        var warp = Assert.Single(recorder.Log.Events, e => e.Kind == FlightLogEventKind.Warp);
        Assert.Equal("Time warp ×1,000 for 2h 00m: path worked out from the orbit", warp.Text);
        Assert.Equal(7_200, warp.DurationS);

        // On rails kRPC's readings can be from different moments: only samples at ×4 or less are kept, the last before
        // the warp included (to fill the warp from).
        Assert.Equal([100, 101, 7_301], recorder.Log.Samples.Select(s => s.MetS)); // 7,302 waits, coasting (thinned)
    }

    [Fact]
    public void Samples_stored_on_rails_by_older_recordings_are_left_out()
    {
        var log = new FlightLog { Samples = [At(0, "ORBITING"), At(500, "ORBITING", warp: 1_000), At(900, "ORBITING", warp: 4)] };
        var clean = log.WithoutRailsSamples();
        Assert.Equal([0, 900], clean.Samples.Select(s => s.MetS));
        Assert.Same(clean, clean.WithoutRailsSamples());
    }

    [Fact]
    public void A_quickload_drops_what_came_after_it()
    {
        var recorder = new FlightLogRecorder("Kerbal X", At(0.5, thrustN: 200_000, ispS: 300), null);
        recorder.Add(At(30, "SUB_ORBITAL", thrustN: 200_000, ispS: 300, massKg: 8_000, stage: 1));
        recorder.Add(At(60, "SUB_ORBITAL", thrustN: 200_000, ispS: 300, massKg: 7_000, stage: 0));
        recorder.Add(At(20, thrustN: 200_000, ispS: 300, massKg: 9_000, stage: 2));

        var log = recorder.Log;
        Assert.Equal([0.5, 20], log.Samples.Select(s => s.MetS));
        Assert.Equal([FlightLogEventKind.Launch, FlightLogEventKind.Quickload], log.Events.Select(e => e.Kind));
    }

    [Fact]
    public void A_quickload_while_another_vessel_is_flown_drops_what_this_log_noted_after_it()
    {
        // The station's log noted a shuttle docking during play that was then undone by loading a quicksave from before it.
        var station = new FlightLogRecorder("Mun Station", At(100, "ORBITING"), null);
        station.Add(At(200, "ORBITING"));
        station.NoteDocking(Launch + 500, FlightLogEventKind.Docked, "Shuttle docked", "Shuttle", 31, joined: false);

        Assert.True(station.RewindTo(Launch + 300));
        var log = station.Log;
        Assert.Equal([100, 200], log.Samples.Select(s => s.MetS));
        Assert.Equal([FlightLogEventKind.Joined, FlightLogEventKind.Quickload], log.Events.Select(e => e.Kind));
        Assert.Equal(300, log.Events[^1].MetS, 1e-9);

        // Nothing after that point: nothing to drop, and no note.
        Assert.False(station.RewindTo(Launch + 300));
        Assert.Equal(2, station.Log.Events.Count);
    }

    // ---------------- Ending from the save ----------------

    [Fact]
    public void A_flight_ends_when_the_save_no_longer_has_it()
    {
        var landed = new FlightLogRecorder("Kerbal X", At(0.5, thrustN: 1), null);
        landed.Add(At(600, "LANDED"));
        var log = landed.Log;

        Assert.Null(FlightLogs.EndedIn(log, Save(700, Vessel("Kerbal X", 0)))); // still there
        Assert.Null(FlightLogs.EndedIn(log, Save(300))); // written before the landing: can't tell
        Assert.Equal(FlightLogOutcome.Recovered, FlightLogs.EndedIn(log, Save(700, Vessel("Kerbal X", 600)))); // another launch
        Assert.Equal(FlightLogOutcome.Reverted, FlightLogs.EndedIn(log, Save(-60)));

        var crashed = log with { Samples = [.. log.Samples.SkipLast(1), log.Samples[^1] with { Situation = "FLYING" }] };
        Assert.Equal(FlightLogOutcome.Lost, FlightLogs.EndedIn(crashed, Save(700)));
        Assert.Null(FlightLogs.EndedIn(log with { Outcome = FlightLogOutcome.Recovered }, Save(700)));

        // A save without lct: launched at the file's time less met.
        Assert.Null(FlightLogs.EndedIn(log, Save(700, new SavedVessel("1", "Kerbal X", "Ship", "LANDED", null) { MissionTimeS = 700 })));
    }

    [Fact]
    public void A_vessel_left_behind_while_another_warps_is_still_there()
    {
        // The MunLink Shuttle's relays: the save's met only moves on while a vessel is loaded, so a relay left in orbit
        // while the shuttle warped a day reads lct + met = its lastUT, a day short of the file's time.
        var relay = new FlightLogRecorder("Relay", At(4, "ORBITING", altitudeM: 2_863_000), null).Log;
        var left = Vessel("Relay", 0) with { Situation = "ORBITING", MissionTimeS = 146 };
        Assert.Null(FlightLogs.EndedIn(relay, Save(86_400, left)));
    }

    [Fact]
    public void A_renamed_vessel_is_still_there()
    {
        var relay = new FlightLogRecorder("MunLink Shuttle mk1 Relay", At(4, "ORBITING"), null).Log;
        Assert.Null(FlightLogs.EndedIn(relay, Save(700, Vessel("KerbLink mk1 A", 0, "Relay"))));
        // Decommissioned: renamed and set to debris, but the only vessel launched then.
        Assert.Null(FlightLogs.EndedIn(relay, Save(700, Vessel("KerbLink mk1 A DeComp", 0, "Debris"))));
        // Two pieces of debris from that moment, neither of its name: it's gone.
        Assert.Equal(FlightLogOutcome.Lost,
            FlightLogs.EndedIn(relay, Save(700, Vessel("Fairing", 0, "Debris"), Vessel("Decoupler", 0.5, "Debris"))));
    }

    // ---------------- Which vessel a flight is now ----------------

    [Fact]
    public void A_flight_follows_its_vessel_through_a_rename()
    {
        // Three relays split off a shuttle on different days, then renamed in orbit (the Minmus relays of 2026-10-20).
        var relayB = new FlightLogRecorder("Link Shuttle mk2 Relay", At(1, "ORBITING", altitudeM: 357_000), null).Log.Summary;
        var shuttle = Vessel("Link Shuttle mk2", -86_400) with { Id = "s" };
        var a = Vessel("MinLink A", -3_600, "Relay") with { Id = "a" };
        var b = Vessel("MinLink B", 0, "Relay") with { Id = "b" };
        var file = Save(700, shuttle, a, b);

        Assert.Equal("b", FlightLogs.VesselOf(relayB, file)?.Id);
        Assert.Same(relayB, Assert.Single(FlightLogs.LogsOf(b, file, [relayB])));
        Assert.Empty(FlightLogs.LogsOf(a, file, [relayB]));

        // Tagged by hand: that vessel, whenever it was launched; tagged to none: no vessel.
        Assert.Equal("a", FlightLogs.VesselOf(relayB with { VesselId = "a" }, file)?.Id);
        Assert.Null(FlightLogs.VesselOf(relayB with { VesselId = FlightLogs.NoVessel }, file));
        Assert.Null(FlightLogs.VesselOf(relayB with { VesselId = "gone" }, file));

        // The likeliest first: launched with the flight, then the rest by how near their launch was.
        Assert.Equal(["b", "a", "s"], FlightLogs.Candidates(relayB, file).Select(v => v.Id));
    }

    [Fact]
    public void Of_vessels_launched_together_the_flight_is_its_namesake_or_the_one_not_debris()
    {
        var log = new FlightLogRecorder("Kerbal X", At(0.5, thrustN: 1), null).Log.Summary;
        var ship = Vessel("Kerbal X", 0) with { Id = "x" };
        var renamed = Vessel("Kerbal Y", 0) with { Id = "y" };
        var fairing = Vessel("Fairing", 0.5, "Debris") with { Id = "f" };

        Assert.Equal("x", FlightLogs.VesselOf(log, Save(700, fairing, renamed, ship))?.Id);
        Assert.Equal("y", FlightLogs.VesselOf(log, Save(700, fairing, renamed))?.Id);
        Assert.Null(FlightLogs.VesselOf(log, Save(700, fairing, fairing with { Id = "g" })));
        Assert.Null(FlightLogs.VesselOf(log, Save(700, renamed, renamed with { Id = "z" }))); // can't tell which
    }

    private static KspSave Save(double met, params SavedVessel[] vessels) => new("Protogen", "SANDBOX", Launch + met, vessels);

    private static SavedVessel Vessel(string name, double launchMet, string type = "Ship") =>
        new("1", name, type, "LANDED", null) { LaunchUt = Launch + launchMet, MissionTimeS = 1 };

    // ---------------- Storage ----------------

    [Fact]
    public void Logs_are_stored_with_their_samples_and_belong_to_the_save()
    {
        var repo = new FlightLogRepository(_db);
        var recorder = new FlightLogRecorder("Kerbal X", At(0.5, thrustN: 200_000, ispS: 300, q: 10), null);
        recorder.Add(At(30, massKg: 8_000, thrustN: 200_000, ispS: 300, q: 1_000, altitudeM: 12_000));
        recorder.Id = repo.Save(recorder.Log);
        recorder.Add(At(31, "SUB_ORBITAL", massKg: 7_950, thrustN: 200_000, ispS: 300, altitudeM: 12_300));
        Assert.Equal(recorder.Id, repo.Save(recorder.Log));

        var stored = repo.Get(recorder.Id)!;
        Assert.Equal(3, stored.Samples.Count);
        Assert.True(double.IsNaN(stored.Samples[0].ApoapsisAltitudeM));
        Assert.Equal(recorder.Log.DeltaVUsed, stored.DeltaVUsed, 1e-9);
        Assert.Equal(recorder.Log.Events, stored.Events);
        Assert.Single(repo.GetInFlight());
        var summary = Assert.Single(repo.GetAll());
        Assert.Equal(12_300, summary.MaxAltitudeM);
        Assert.Equal("SUB_ORBITAL", summary.LastSituation);
        Assert.Equal("Kerbin", summary.Route);

        recorder.End(FlightLogOutcome.Lost, "Lost");
        repo.Save(recorder.Log);
        Assert.Empty(repo.GetInFlight());

        _db.ActiveSave = "SandBox";
        Assert.Empty(repo.GetAll());
        _db.ActiveSave = "Protogen";
        repo.Delete(recorder.Id);
        Assert.Empty(repo.GetAll());
    }

    [Fact]
    public void A_retag_is_kept_when_the_recorder_saves_the_flight_again()
    {
        var repo = new FlightLogRepository(_db);
        var recorder = new FlightLogRecorder("Link Shuttle mk2 Relay", At(1, "ORBITING"), null);
        recorder.Id = repo.Save(recorder.Log);

        repo.Retag(recorder.Id, "MinLink", "b");
        recorder.Add(At(60, "ORBITING"));
        repo.Save(recorder.Log); // the recorder's copy still has the old name and no tag

        var summary = Assert.Single(repo.GetAll());
        Assert.Equal("MinLink", summary.VesselName);
        Assert.Equal("b", summary.VesselId);
        Assert.Equal("b", repo.Get(recorder.Id)!.VesselId);
        Assert.Equal("b", Assert.Single(repo.GetInFlight()).VesselId);

        repo.Retag(recorder.Id, "MinLink", null);
        Assert.Null(repo.Get(recorder.Id)!.VesselId);
        repo.Retag(recorder.Id, "MinLink", FlightLogs.NoVessel);
        Assert.Equal(FlightLogs.NoVessel, repo.Get(recorder.Id)!.VesselId);
    }

    [Fact]
    public void A_log_stored_without_a_route_gets_one_from_its_samples()
    {
        var repo = new FlightLogRepository(_db);
        var recorder = new FlightLogRecorder("Looper Mk3", At(0.5, thrustN: 1), null);
        recorder.Add(At(4_000, "ORBITING", body: "Mun"));
        recorder.Add(At(9_000, "SUB_ORBITAL", body: "Kerbin"));
        var id = repo.Save(recorder.Log);
        using (var c = _db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "UPDATE flight_logs SET route = NULL;";
            cmd.ExecuteNonQuery();
        }

        Assert.Equal("Kerbin › Mun › Kerbin", Assert.Single(repo.GetAll()).Route);
        Assert.Equal("Kerbin › Mun › Kerbin", Assert.Single(repo.GetAll()).Route); // kept
        Assert.Equal(id, repo.GetAll()[0].Id);
    }

    // ---------------- Stale orbit readings ----------------

    private static FlightSample InOrbit(double ut, string body, double altitudeM, double a, double e, double meanAnomaly,
        string situation = "ESCAPING", double warp = 1) =>
        At(ut - Launch, situation, body: body, altitudeM: altitudeM, warp: warp) with
        {
            SemiMajorAxisM = a, Eccentricity = e, MeanAnomalyRad = meanAnomaly,
        };

    [Fact]
    public void An_orbit_that_follows_the_craft_is_not_stale()
    {
        // Leaving Duna (the Duna test flight): the hyperbola puts the craft 1,941,970.59 km out, as its altitude does.
        var before = InOrbit(23_589_027.01, "Duna", 1_612_008.79, -392_795.98, 2.0551626460, 3.8310464549);
        var now = InOrbit(23_589_037.09, "Duna", 1_621_970.59, -392_795.98, 2.0551626460, 3.8535243217);
        Assert.False(FlightLogs.OrbitStale(before, now, 320_000));

        // A circular 100 km Kerbin orbit, and an ellipse at its apoapsis (a = 1,000 km, e = 0.5: 1,500 km from the centre).
        Assert.False(FlightLogs.OrbitStale(null, InOrbit(Launch + 10, "Kerbin", 100_000, 700_000, 0, 1, "ORBITING"), 600_000));
        Assert.False(FlightLogs.OrbitStale(null, InOrbit(Launch + 10, "Kerbin", 900_000, 1_000_000, 0.5, Math.PI, "ORBITING"), 600_000));
    }

    [Fact]
    public void An_orbit_caught_while_the_game_rebuilt_the_vessel_is_stale()
    {
        // After a quickload leaving Duna, the streams got an orbit around Kerbin that never moved again. It puts the craft
        // 9,944.39 km from the centre; the altitude (against Duna, really) says 600 + 1,631.87 = 2,231.87 km.
        var now = InOrbit(23_589_047.09, "Kerbin", 1_631_870.28, -17_712_187.51, 1.0348360906, 0.1989576395);
        Assert.True(FlightLogs.OrbitStale(null, now, 600_000));
    }

    [Fact]
    public void A_mean_anomaly_that_stops_while_time_goes_on_is_stale()
    {
        var a = InOrbit(Launch + 10, "Kerbin", 100_000, 700_000, 0, 1, "ORBITING");
        Assert.True(FlightLogs.OrbitStale(a, a with { Ut = a.Ut + 10 }, 600_000));
        Assert.False(FlightLogs.OrbitStale(a, a, 600_000)); // paused: time didn't move on either
        Assert.False(FlightLogs.OrbitStale(a with { WarpRate = 1000 }, a with { Ut = a.Ut + 10 }, 600_000)); // on rails before
        Assert.False(FlightLogs.OrbitStale(a, a with { Ut = a.Ut + 10, WarpRate = 1000 }, 600_000)); // on rails now
        Assert.False(FlightLogs.OrbitStale(a, a with { Ut = a.Ut + 10, Situation = "PRELAUNCH" }, 600_000)); // on the pad
    }

    [Fact]
    public void Samples_without_an_orbit_are_not_judged() =>
        Assert.False(FlightLogs.OrbitStale(null, At(10, altitudeM: 5_000), 600_000));

    [Fact]
    public void Logs_are_carried_over_when_the_database_is_rebuilt()
    {
        var repo = new FlightLogRepository(_db);
        var id = repo.Save(new FlightLogRecorder("Kerbal X", At(0.5, thrustN: 1), null).Log);

        using (var c = _db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "UPDATE app_meta SET value = 'stale' WHERE key = 'fingerprint';";
            cmd.ExecuteNonQuery();
        }
        TestDb.Release(_path);
        _db.Initialize();

        Assert.Equal("Kerbal X", repo.Get(id)!.VesselName);
        Assert.Single(repo.Get(id)!.Samples);
    }
}
