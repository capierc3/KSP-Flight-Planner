using System.Globalization;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Data;

/// <summary>A body that can host custom locations, with the map's low orbit it attaches to.</summary>
public sealed record CelestialBodyOption(CelestialBody Body, DvMapNode LowOrbit)
{
    public string Name => Body.Name;
    public override string ToString() => Body.Name;
}

public sealed class CustomLocationRepository(PlannerDatabase database)
{
    public IReadOnlyList<CustomLocation> GetAll()
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, name, body_name, altitude_m, note FROM custom_locations ORDER BY body_name, altitude_m;";
        using var reader = cmd.ExecuteReader();
        var list = new List<CustomLocation>();
        while (reader.Read())
            list.Add(new CustomLocation(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        return list;
    }

    public long Add(string name, string bodyName, double altitudeM, string? note = null)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO custom_locations (name, body_name, altitude_m, note, created_utc)
            VALUES ($name, $body, $alt, $note, $created) RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$body", bodyName);
        cmd.Parameters.AddWithValue("$alt", altitudeM);
        cmd.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>Deletes the location and any Δv edit made to its edge.</summary>
    public void Delete(long id)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM custom_locations WHERE id = $id;
            DELETE FROM dv_map_overrides WHERE node_key = $key;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$key", $"custom.{id}");
        cmd.ExecuteNonQuery();
    }
}
