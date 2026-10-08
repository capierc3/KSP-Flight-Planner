using System.Globalization;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;
using Microsoft.Data.Sqlite;

namespace KspFlightPlanner.Data;

/// <summary>A body that can host custom locations, with the map's low orbit it attaches to.</summary>
public sealed record CelestialBodyOption(CelestialBody Body, DvMapNode LowOrbit)
{
    public string Name => Body.Name;
    public override string ToString() => Body.Name;
}

/// <summary>An image attached to a custom location (stored in the database).</summary>
public sealed record LocationImage(long Id, long LocationId, string FileName, byte[] Data);

public sealed class CustomLocationRepository(PlannerDatabase database)
{
    public IReadOnlyList<CustomLocation> GetAll()
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, body_name, periapsis_altitude_m, apoapsis_altitude_m, inclination_deg, description,
                   is_placed, position_mode, lan_deg, arg_periapsis_deg, mean_anomaly_deg, phase_angle_deg, epoch_ut
            FROM custom_locations ORDER BY body_name, periapsis_altitude_m, name;
            """;
        using var reader = cmd.ExecuteReader();
        var list = new List<CustomLocation>();
        while (reader.Read())
            list.Add(new CustomLocation(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3),
                reader.GetDouble(4), reader.GetDouble(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                IsPlaced: reader.GetInt64(7) != 0,
                Mode: Enum.TryParse<PositionMode>(reader.GetString(8), out var mode) ? mode : PositionMode.Elements,
                LanDeg: reader.GetDouble(9),
                ArgumentOfPeriapsisDeg: reader.GetDouble(10),
                MeanAnomalyDeg: reader.GetDouble(11),
                PhaseAngleDeg: reader.GetDouble(12),
                EpochUt: reader.GetDouble(13)));
        return list;
    }

    public CustomLocation? Get(long id) => GetAll().FirstOrDefault(l => l.Id == id);

    /// <summary>Adds a circular orbit (quick add).</summary>
    public long Add(string name, string bodyName, double altitudeM, string? description = null) =>
        Add(CustomLocation.Circular(0, name, bodyName, altitudeM, description));

    /// <summary>Inserts the location (its Id is ignored) and returns the new id.</summary>
    public long Add(CustomLocation location)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO custom_locations
                (name, body_name, periapsis_altitude_m, apoapsis_altitude_m, inclination_deg, description,
                 is_placed, position_mode, lan_deg, arg_periapsis_deg, mean_anomaly_deg, phase_angle_deg, epoch_ut, created_utc)
            VALUES ($name, $body, $pe, $ap, $inc, $desc, $placed, $mode, $lan, $argpe, $mean, $phase, $epoch, $now) RETURNING id;
            """;
        AddValues(cmd, location);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Changes a location in place (same id, so stops and saved missions keep pointing at it).
    /// Any manual Δv edit on its edge is dropped, since the computed value changes with the orbit.
    /// </summary>
    public void Update(CustomLocation location)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE custom_locations
            SET name = $name, body_name = $body, periapsis_altitude_m = $pe, apoapsis_altitude_m = $ap,
                inclination_deg = $inc, description = $desc, is_placed = $placed, position_mode = $mode, lan_deg = $lan,
                arg_periapsis_deg = $argpe, mean_anomaly_deg = $mean, phase_angle_deg = $phase, epoch_ut = $epoch,
                updated_utc = $now
            WHERE id = $id;
            DELETE FROM dv_map_overrides WHERE node_key = $key;
            """;
        AddValues(cmd, location);
        cmd.Parameters.AddWithValue("$id", location.Id);
        cmd.Parameters.AddWithValue("$key", location.Key);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes the location, its images, and any Δv edit made to its edge.</summary>
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

    // ---------------- Images ----------------

    public IReadOnlyList<LocationImage> GetImages(long locationId)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, file_name, image FROM custom_location_images WHERE location_id = $id ORDER BY sort_order, id;";
        cmd.Parameters.AddWithValue("$id", locationId);
        using var reader = cmd.ExecuteReader();
        var list = new List<LocationImage>();
        while (reader.Read())
            list.Add(new LocationImage(reader.GetInt64(0), locationId, reader.GetString(1), (byte[])reader.GetValue(2)));
        return list;
    }

    public long AddImage(long locationId, string fileName, byte[] data)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO custom_location_images (location_id, file_name, image, sort_order)
            VALUES ($loc, $name, $data, (SELECT COALESCE(MAX(sort_order), 0) + 1 FROM custom_location_images WHERE location_id = $loc))
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$loc", locationId);
        cmd.Parameters.AddWithValue("$name", fileName);
        cmd.Parameters.Add("$data", SqliteType.Blob).Value = data;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void DeleteImage(long imageId)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM custom_location_images WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", imageId);
        cmd.ExecuteNonQuery();
    }

    private static void AddValues(SqliteCommand cmd, CustomLocation location)
    {
        cmd.Parameters.AddWithValue("$name", location.Name.Trim());
        cmd.Parameters.AddWithValue("$body", location.BodyName);
        cmd.Parameters.AddWithValue("$pe", location.PeriapsisAltitudeM);
        cmd.Parameters.AddWithValue("$ap", location.ApoapsisAltitudeM);
        cmd.Parameters.AddWithValue("$inc", location.InclinationDeg);
        cmd.Parameters.AddWithValue("$desc", string.IsNullOrWhiteSpace(location.Description) ? DBNull.Value : location.Description);
        cmd.Parameters.AddWithValue("$placed", location.IsPlaced ? 1 : 0);
        cmd.Parameters.AddWithValue("$mode", location.Mode.ToString());
        cmd.Parameters.AddWithValue("$lan", location.LanDeg);
        cmd.Parameters.AddWithValue("$argpe", location.ArgumentOfPeriapsisDeg);
        cmd.Parameters.AddWithValue("$mean", location.MeanAnomalyDeg);
        cmd.Parameters.AddWithValue("$phase", location.PhaseAngleDeg);
        cmd.Parameters.AddWithValue("$epoch", location.EpochUt);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
    }
}
