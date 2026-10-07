using System.Globalization;
using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Data;

public sealed class ConstellationRepository(PlannerDatabase database)
{
    public IReadOnlyList<SavedConstellation> GetAll()
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT c.id, c.name, c.body_id, c.satellite_count, c.altitude_m, c.prefer_dive_orbit,
                   c.use_occlusion, c.notes, c.created_utc, b.name, s.name
            FROM saved_constellations c
            JOIN celestial_bodies b ON b.id = c.body_id
            JOIN star_systems s ON s.id = b.system_id
            ORDER BY c.created_utc DESC;
            """;
        using var reader = cmd.ExecuteReader();
        var list = new List<SavedConstellation>();
        while (reader.Read())
        {
            list.Add(new SavedConstellation
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                BodyId = reader.GetInt64(2),
                SatelliteCount = reader.GetInt32(3),
                AltitudeM = reader.GetDouble(4),
                PreferDiveOrbit = reader.GetInt64(5) != 0,
                UseOcclusion = reader.GetInt64(6) != 0,
                Notes = reader.IsDBNull(7) ? null : reader.GetString(7),
                CreatedUtc = DateTime.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                BodyName = reader.GetString(9),
                SystemName = reader.GetString(10),
            });
        }
        return list;
    }

    /// <summary>Inserts the plan and returns its new id.</summary>
    public long Add(SavedConstellation plan)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO saved_constellations
                (name, body_id, satellite_count, altitude_m, prefer_dive_orbit, use_occlusion, notes, created_utc)
            VALUES ($name, $body, $count, $alt, $dive, $occ, $notes, $created)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$name", plan.Name);
        cmd.Parameters.AddWithValue("$body", plan.BodyId);
        cmd.Parameters.AddWithValue("$count", plan.SatelliteCount);
        cmd.Parameters.AddWithValue("$alt", plan.AltitudeM);
        cmd.Parameters.AddWithValue("$dive", plan.PreferDiveOrbit ? 1 : 0);
        cmd.Parameters.AddWithValue("$occ", plan.UseOcclusion ? 1 : 0);
        cmd.Parameters.AddWithValue("$notes", (object?)plan.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void Delete(long id)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM saved_constellations WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }
}
