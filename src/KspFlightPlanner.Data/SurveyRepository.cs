using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using KspFlightPlanner.Core.Ksp;
using Microsoft.Data.Sqlite;

namespace KspFlightPlanner.Data;

/// <summary>A survey report: what was found about a biome, and when in the game.</summary>
public sealed record SurveyReportRow(long Id, double Ut, string BodyName, string Biome, SurveyLevel Level, string Text);

/// <summary>
/// The ore survey's data for the active KSP save (docs/SURVEY.md): each body's ore map, what's been covered, the last
/// reading of each station, and the reports. Nothing is stored without an active save.
/// </summary>
public sealed class SurveyRepository(PlannerDatabase database)
{
    private string? Save => database.ActiveSave;

    public SurveyMap? GetMap(string bodyName)
    {
        if (Save is not { } save) return null;
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT step_deg, radius_m, rotation_period_s, initial_rotation_deg, abundance, estimate, biomes, biome_names, fetched_utc
            FROM survey_maps WHERE ksp_save = $save AND body_name = $body;
            """;
        cmd.Parameters.AddWithValue("$save", save);
        cmd.Parameters.AddWithValue("$body", bodyName);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new SurveyMap(bodyName, r.GetDouble(0), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3),
            Floats((byte[])r[4]), Floats((byte[])r[5]), MemoryMarshal.Cast<byte, short>((byte[])r[6]).ToArray(),
            JsonSerializer.Deserialize<List<string>>(r.GetString(7)) ?? [],
            DateTime.Parse(r.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    public void SaveMap(SurveyMap map)
    {
        if (Save is not { } save) return;
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO survey_maps
                (ksp_save, body_name, step_deg, radius_m, rotation_period_s, initial_rotation_deg, abundance, estimate, biomes, biome_names, fetched_utc)
            VALUES ($save, $body, $step, $radius, $period, $rot0, $abundance, $estimate, $biomes, $names, $utc);
            """;
        cmd.Parameters.AddWithValue("$save", save);
        cmd.Parameters.AddWithValue("$body", map.BodyName);
        cmd.Parameters.AddWithValue("$step", map.StepDeg);
        cmd.Parameters.AddWithValue("$radius", map.RadiusM);
        cmd.Parameters.AddWithValue("$period", map.RotationPeriodS);
        cmd.Parameters.AddWithValue("$rot0", map.InitialRotationDeg);
        cmd.Parameters.AddWithValue("$abundance", Bytes(map.Abundance));
        cmd.Parameters.AddWithValue("$estimate", Bytes(map.Estimate));
        cmd.Parameters.AddWithValue("$biomes", MemoryMarshal.AsBytes(map.Biomes.AsSpan()).ToArray());
        cmd.Parameters.AddWithValue("$names", JsonSerializer.Serialize(map.BiomeNames));
        cmd.Parameters.AddWithValue("$utc", map.FetchedUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    /// <summary>The body's coverage, or an empty one (also when the stored grid is another size).</summary>
    public SurveyCoverage GetCoverage(string bodyName, double stepDeg)
    {
        if (Save is { } save)
        {
            using var connection = database.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT step_deg, rough, estimate FROM survey_coverage WHERE ksp_save = $save AND body_name = $body;";
            cmd.Parameters.AddWithValue("$save", save);
            cmd.Parameters.AddWithValue("$body", bodyName);
            using var r = cmd.ExecuteReader();
            if (r.Read() && r.GetDouble(0) == stepDeg) return new SurveyCoverage(stepDeg, Floats((byte[])r[1]), Floats((byte[])r[2]));
        }
        return new SurveyCoverage(stepDeg);
    }

    public void SaveCoverage(string bodyName, SurveyCoverage coverage)
    {
        if (Save is not { } save) return;
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO survey_coverage (ksp_save, body_name, step_deg, rough, estimate)
            VALUES ($save, $body, $step, $rough, $estimate);
            """;
        cmd.Parameters.AddWithValue("$save", save);
        cmd.Parameters.AddWithValue("$body", bodyName);
        cmd.Parameters.AddWithValue("$step", coverage.StepDeg);
        cmd.Parameters.AddWithValue("$rough", Bytes(coverage.Rough));
        cmd.Parameters.AddWithValue("$estimate", Bytes(coverage.Estimate));
        cmd.ExecuteNonQuery();
    }

    /// <summary>The bodies with stored coverage.</summary>
    public IReadOnlyList<string> CoveredBodies()
    {
        if (Save is not { } save) return [];
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT body_name FROM survey_coverage WHERE ksp_save = $save;";
        cmd.Parameters.AddWithValue("$save", save);
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public SurveyReading? GetReading(string vesselId)
    {
        if (Save is not { } save) return null;
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT reading_json FROM survey_readings WHERE ksp_save = $save AND vessel_id = $id;";
        cmd.Parameters.AddWithValue("$save", save);
        cmd.Parameters.AddWithValue("$id", vesselId);
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<SurveyReading>(json) : null;
    }

    public void SaveReading(string vesselId, string vesselName, SurveyReading reading)
    {
        if (Save is not { } save) return;
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO survey_readings (ksp_save, vessel_id, vessel_name, reading_json) VALUES ($save, $id, $name, $json);
            """;
        cmd.Parameters.AddWithValue("$save", save);
        cmd.Parameters.AddWithValue("$id", vesselId);
        cmd.Parameters.AddWithValue("$name", vesselName);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(reading));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Every report for the save, newest find first.</summary>
    public IReadOnlyList<SurveyReportRow> Reports()
    {
        if (Save is not { } save) return [];
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, ut, body_name, biome, level, text FROM survey_reports WHERE ksp_save = $save ORDER BY ut DESC, id DESC;";
        cmd.Parameters.AddWithValue("$save", save);
        using var r = cmd.ExecuteReader();
        var list = new List<SurveyReportRow>();
        while (r.Read())
            list.Add(new SurveyReportRow(r.GetInt64(0), r.GetDouble(1), r.GetString(2), r.GetString(3),
                Enum.TryParse<SurveyLevel>(r.GetString(4), out var level) ? level : SurveyLevel.Rough, r.GetString(5)));
        return list;
    }

    /// <summary>Adds a report unless the biome already has one at that level; returns whether it was new.</summary>
    public bool AddReport(double ut, string bodyName, string biome, SurveyLevel level, string text)
    {
        if (Save is not { } save) return false;
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO survey_reports (ksp_save, ut, body_name, biome, level, text, created_utc)
            VALUES ($save, $ut, $body, $biome, $level, $text, $utc);
            """;
        cmd.Parameters.AddWithValue("$save", save);
        cmd.Parameters.AddWithValue("$ut", ut);
        cmd.Parameters.AddWithValue("$body", bodyName);
        cmd.Parameters.AddWithValue("$biome", biome);
        cmd.Parameters.AddWithValue("$level", level.ToString());
        cmd.Parameters.AddWithValue("$text", text);
        cmd.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Takes away reports of finds after <paramref name="ut"/> (the game went back in time).</summary>
    public void DeleteReportsAfter(double ut)
    {
        if (Save is not { } save) return;
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM survey_reports WHERE ksp_save = $save AND ut > $ut;";
        cmd.Parameters.AddWithValue("$save", save);
        cmd.Parameters.AddWithValue("$ut", ut);
        cmd.ExecuteNonQuery();
    }

    private static byte[] Bytes(float[] values) => MemoryMarshal.AsBytes(values.AsSpan()).ToArray();

    private static float[] Floats(byte[] bytes) => MemoryMarshal.Cast<byte, float>(bytes).ToArray();
}
