using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using KspFlightPlanner.Core.Ksp;
using Microsoft.Data.Sqlite;

namespace KspFlightPlanner.Data;

/// <summary>
/// Recorded flights (the flight log). Only the active save's logs (<see cref="PlannerDatabase.ActiveSave"/>) and shared
/// ones are read. The samples are stored compressed and only read for a whole log.
/// </summary>
public sealed class FlightLogRepository(PlannerDatabase database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals, // NaN Ap/Pe
    };

    private const string SummaryColumns = "id, vessel_name, launch_ut, joined_at_met, outcome, last_ut, delta_v_used, max_altitude_m, " +
                                          "last_situation, last_body, events_json, route, launch_parts, launch_mass_kg, vessel_id";

    /// <summary>Every log without its samples, the latest launch first.</summary>
    public IReadOnlyList<FlightLogSummary> GetAll()
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {SummaryColumns} FROM flight_logs WHERE {PlannerDatabase.InActiveSave()} ORDER BY launch_ut DESC, id DESC;";
        database.AddSave(cmd);
        using var reader = cmd.ExecuteReader();
        var list = new List<FlightLogSummary>();
        while (reader.Read()) list.Add(ReadSummary(reader));
        reader.Close();
        return list.Select(l => l.Route is null || l.FromLaunch && l.LaunchMassKg <= 0 ? FillDerived(l) : l).ToList();
    }

    /// <summary>A log stored before its route and launch mass were: works them out from the samples once and keeps them.</summary>
    private FlightLogSummary FillDerived(FlightLogSummary summary)
    {
        if (Get(summary.Id) is not { } log) return summary;
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE flight_logs SET route = $route, launch_mass_kg = $mass WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", summary.Id);
        cmd.Parameters.AddWithValue("$route", log.Route);
        cmd.Parameters.AddWithValue("$mass", log.LaunchMassKg);
        cmd.ExecuteNonQuery();
        return summary with { Route = log.Route, LaunchMassKg = log.LaunchMassKg };
    }

    /// <summary>A whole log, samples and all.</summary>
    public FlightLog? Get(long id) => Read("id = $id", cmd => cmd.Parameters.AddWithValue("$id", id)).FirstOrDefault();

    /// <summary>Flights not seen to end yet, to carry on recording or to end from the save.</summary>
    public IReadOnlyList<FlightLog> GetInFlight() =>
        Read($"outcome = '{FlightLogOutcome.InFlight}' AND {PlannerDatabase.InActiveSave()}", database.AddSave);

    /// <summary>
    /// Stores a log: a new one (Id 0) gets its id back, an existing one is replaced — all but its rocket name and vessel tag,
    /// which only <see cref="Retag"/> changes (the recorder holds a copy of the log being flown and saves it now and then).
    /// </summary>
    public long Save(FlightLog log)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        cmd.CommandText = log.Id == 0
            ? """
              INSERT INTO flight_logs (vessel_name, launch_ut, joined_at_met, outcome, flight_id, last_ut, delta_v_used, max_altitude_m,
                                       last_situation, last_body, route, launch_parts, launch_mass_kg, vessel_id, events_json, samples, ksp_save, created_utc, updated_utc)
              VALUES ($name, $launch, $joined, $outcome, $flight, $last, $dv, $maxalt, $situation, $body, $route, $parts, $mass, $vessel, $events, $samples, $active_save, $now, $now)
              RETURNING id;
              """
            : """
              UPDATE flight_logs SET launch_ut = $launch, joined_at_met = $joined, outcome = $outcome,
                  flight_id = $flight, last_ut = $last, delta_v_used = $dv, max_altitude_m = $maxalt, last_situation = $situation,
                  last_body = $body, route = $route, launch_parts = $parts, launch_mass_kg = $mass, events_json = $events, samples = $samples, updated_utc = $now
              WHERE id = $id RETURNING id;
              """;
        database.AddSave(cmd);
        cmd.Parameters.AddWithValue("$id", log.Id);
        cmd.Parameters.AddWithValue("$name", log.VesselName);
        cmd.Parameters.AddWithValue("$launch", log.LaunchUt);
        cmd.Parameters.AddWithValue("$joined", log.JoinedAtMetS);
        cmd.Parameters.AddWithValue("$outcome", log.Outcome.ToString());
        cmd.Parameters.AddWithValue("$flight", (object?)log.FlightId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$last", log.LastUt);
        cmd.Parameters.AddWithValue("$dv", log.DeltaVUsed);
        cmd.Parameters.AddWithValue("$maxalt", log.MaxAltitudeM);
        cmd.Parameters.AddWithValue("$situation", (object?)log.LastSituation ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$body", (object?)log.LastBody ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$route", log.Route);
        cmd.Parameters.AddWithValue("$parts", (object?)log.LaunchPartCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mass", log.LaunchMassKg);
        cmd.Parameters.AddWithValue("$vessel", (object?)log.VesselId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$events", JsonSerializer.Serialize(log.Events, Json));
        cmd.Parameters.AddWithValue("$samples", Pack(log.Samples));
        cmd.Parameters.AddWithValue("$now", now);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? log.Id);
    }

    /// <summary>
    /// Edits what a flight is filed under: the rocket (the name it's grouped and compared by) and the save's vessel it
    /// belongs to (null = found by launch time, <see cref="FlightLogs.NoVessel"/> = none).
    /// </summary>
    public void Retag(long id, string rocket, string? vesselId)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE flight_logs SET vessel_name = $name, vessel_id = $vessel, updated_utc = $now WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", rocket);
        cmd.Parameters.AddWithValue("$vessel", (object?)vesselId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    public void Delete(long id)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM flight_logs WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private IReadOnlyList<FlightLog> Read(string where, Action<SqliteCommand> bind)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {SummaryColumns}, flight_id, samples FROM flight_logs WHERE {where} ORDER BY launch_ut DESC, id DESC;";
        bind(cmd);
        using var reader = cmd.ExecuteReader();
        var list = new List<FlightLog>();
        while (reader.Read())
        {
            var summary = ReadSummary(reader);
            IReadOnlyList<FlightSample> samples;
            try { samples = Unpack((byte[])reader["samples"]); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { samples = []; }
            list.Add(new FlightLog
            {
                Id = summary.Id,
                VesselName = summary.VesselName,
                LaunchUt = summary.LaunchUt,
                JoinedAtMetS = summary.JoinedAtMetS,
                Outcome = summary.Outcome,
                FlightId = reader.IsDBNull(15) ? null : reader.GetInt64(15),
                LaunchPartCount = summary.LaunchPartCount,
                VesselId = summary.VesselId,
                Samples = samples,
                Events = summary.Events,
            });
        }
        return list;
    }

    private static FlightLogSummary ReadSummary(SqliteDataReader reader)
    {
        IReadOnlyList<FlightLogEvent> events;
        try { events = JsonSerializer.Deserialize<List<FlightLogEvent>>(reader.GetString(10), Json) ?? []; }
        catch (JsonException) { events = []; }
        return new FlightLogSummary(
            reader.GetInt64(0), reader.GetString(1), reader.GetDouble(2), reader.GetDouble(3),
            Enum.TryParse<FlightLogOutcome>(reader.GetString(4), out var outcome) ? outcome : FlightLogOutcome.InFlight,
            reader.GetDouble(5), reader.GetDouble(6), reader.GetDouble(7),
            reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9), events)
        {
            Route = reader.IsDBNull(11) ? null : reader.GetString(11),
            LaunchPartCount = reader.IsDBNull(12) ? null : reader.GetInt32(12),
            LaunchMassKg = reader.GetDouble(13),
            VesselId = reader.IsDBNull(14) ? null : reader.GetString(14),
        };
    }

    private static byte[] Pack(IReadOnlyList<FlightSample> samples)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest))
            JsonSerializer.Serialize(brotli, samples, Json);
        return output.ToArray();
    }

    private static IReadOnlyList<FlightSample> Unpack(byte[] data)
    {
        using var brotli = new BrotliStream(new MemoryStream(data), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<List<FlightSample>>(brotli, Json) ?? [];
    }
}
