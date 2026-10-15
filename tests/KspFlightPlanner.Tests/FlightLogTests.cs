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
    }

    [Fact]
    public void Rails_warp_is_noted_as_data_lost_unless_it_was_short()
    {
        var recorder = new FlightLogRecorder("Kerbal X", At(100, "ORBITING"), null);
        recorder.Add(At(100.5, "ORBITING", warp: 5));
        recorder.Add(At(101, "ORBITING", warp: 1)); // ×5 for half a second: nothing to note
        recorder.Add(At(101, "ORBITING", warp: 100));
        recorder.Add(At(3_701, "ORBITING", warp: 1_000));
        recorder.Add(At(7_301, "ORBITING", warp: 1));
        recorder.Add(At(7_302, "ORBITING", warp: 4)); // physics warp: still recorded

        var warp = Assert.Single(recorder.Log.Events, e => e.Kind == FlightLogEventKind.Warp);
        Assert.Equal("Data lost in warp: ×1,000 for 2h 00m", warp.Text);
        Assert.Equal(7_200, warp.DurationS);
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

    // ---------------- Ending from the save ----------------

    [Fact]
    public void A_flight_ends_when_the_save_no_longer_has_it()
    {
        var landed = new FlightLogRecorder("Kerbal X", At(0.5, thrustN: 1), null);
        landed.Add(At(600, "LANDED"));
        var log = landed.Log;

        KspSave Save(double met, params SavedVessel[] vessels) => new("Protogen", "SANDBOX", Launch + met, vessels);
        SavedVessel Vessel(string name, double saveMet) => new("1", name, "Ship", "LANDED", null) { MissionTimeS = saveMet };

        Assert.Null(FlightLogs.EndedIn(log, Save(700, Vessel("Kerbal X", 700)))); // still there
        Assert.Null(FlightLogs.EndedIn(log, Save(300))); // written before the landing: can't tell
        Assert.Equal(FlightLogOutcome.Recovered, FlightLogs.EndedIn(log, Save(700, Vessel("Kerbal X", 100)))); // another launch
        Assert.Equal(FlightLogOutcome.Reverted, FlightLogs.EndedIn(log, Save(-60)));

        var crashed = log with { Samples = [.. log.Samples.SkipLast(1), log.Samples[^1] with { Situation = "FLYING" }] };
        Assert.Equal(FlightLogOutcome.Lost, FlightLogs.EndedIn(crashed, Save(700)));
        Assert.Null(FlightLogs.EndedIn(log with { Outcome = FlightLogOutcome.Recovered }, Save(700)));
    }

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
