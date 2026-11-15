using System.Globalization;
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
}
