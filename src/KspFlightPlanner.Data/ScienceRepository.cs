using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.Data;

/// <summary>
/// Science gaps' data for the active KSP save (docs/SCIENCE.md): the game's science catalog, fetched once through the
/// add-on. The subjects themselves are read fresh from each save update, so nothing else is stored.
/// </summary>
public sealed class ScienceRepository(PlannerDatabase database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ScienceCatalog? GetCatalog()
    {
        if (database.ActiveSave is not { } save) return null;
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT catalog_json FROM science_catalogs WHERE ksp_save = $save;";
        cmd.Parameters.AddWithValue("$save", save);
        if (cmd.ExecuteScalar() is not string json) return null;
        try { return JsonSerializer.Deserialize<ScienceCatalog>(json, Json); }
        catch (JsonException) { return null; }
    }

    public void SaveCatalog(ScienceCatalog catalog)
    {
        if (database.ActiveSave is not { } save) return;
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO science_catalogs (ksp_save, catalog_json, fetched_utc) VALUES ($save, $json, $utc);";
        cmd.Parameters.AddWithValue("$save", save);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(catalog, Json));
        cmd.Parameters.AddWithValue("$utc", catalog.FetchedUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    public ScienceBiomeMap? GetBiomeMap(string bodyName)
    {
        if (database.ActiveSave is not { } save) return null;
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT step_deg, radius_m, rotation_period_s, initial_rotation_deg, biomes, biome_names, fetched_utc
            FROM science_biome_maps WHERE ksp_save = $save AND body_name = $body;
            """;
        cmd.Parameters.AddWithValue("$save", save);
        cmd.Parameters.AddWithValue("$body", bodyName);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new ScienceBiomeMap(bodyName, r.GetDouble(0), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3),
            MemoryMarshal.Cast<byte, short>((byte[])r[4]).ToArray(),
            JsonSerializer.Deserialize<List<string>>(r.GetString(5)) ?? [],
            DateTime.Parse(r.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    public void SaveBiomeMap(ScienceBiomeMap map)
    {
        if (database.ActiveSave is not { } save) return;
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO science_biome_maps
                (ksp_save, body_name, step_deg, radius_m, rotation_period_s, initial_rotation_deg, biomes, biome_names, fetched_utc)
            VALUES ($save, $body, $step, $radius, $period, $rot0, $biomes, $names, $utc);
            """;
        cmd.Parameters.AddWithValue("$save", save);
        cmd.Parameters.AddWithValue("$body", map.BodyName);
        cmd.Parameters.AddWithValue("$step", map.StepDeg);
        cmd.Parameters.AddWithValue("$radius", map.RadiusM);
        cmd.Parameters.AddWithValue("$period", map.RotationPeriodS);
        cmd.Parameters.AddWithValue("$rot0", map.InitialRotationDeg);
        cmd.Parameters.AddWithValue("$biomes", MemoryMarshal.AsBytes(map.Biomes.AsSpan()).ToArray());
        cmd.Parameters.AddWithValue("$names", JsonSerializer.Serialize(map.BiomeNames));
        cmd.Parameters.AddWithValue("$utc", map.FetchedUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }
}
