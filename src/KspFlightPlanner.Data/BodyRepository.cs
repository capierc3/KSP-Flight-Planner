using KspFlightPlanner.Core.Models;
using Microsoft.Data.Sqlite;

namespace KspFlightPlanner.Data;

public sealed class BodyRepository(PlannerDatabase database)
{
    public IReadOnlyList<StarSystem> GetSystems()
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, name FROM star_systems ORDER BY sort_order, name;";
        using var reader = cmd.ExecuteReader();
        var list = new List<StarSystem>();
        while (reader.Read())
            list.Add(new StarSystem(reader.GetInt64(0), reader.GetString(1)));
        return list;
    }

    public IReadOnlyList<CelestialBody> GetBodies(long systemId)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SelectBodies + " WHERE system_id = $sys ORDER BY sort_order, name;";
        cmd.Parameters.AddWithValue("$sys", systemId);
        return ReadBodies(cmd);
    }

    public CelestialBody? GetBody(long bodyId)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SelectBodies + " WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", bodyId);
        return ReadBodies(cmd).FirstOrDefault();
    }

    private const string SelectBodies = """
        SELECT id, system_id, name, mass_kg, mu, equatorial_radius_m, rotation_period_s,
               atmosphere_height_m, soi_radius_m, color, is_default
        FROM celestial_bodies
        """;

    private static List<CelestialBody> ReadBodies(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var list = new List<CelestialBody>();
        while (reader.Read())
        {
            list.Add(new CelestialBody
            {
                Id = reader.GetInt64(0),
                SystemId = reader.GetInt64(1),
                Name = reader.GetString(2),
                MassKg = reader.GetDouble(3),
                Mu = reader.GetDouble(4),
                EquatorialRadiusM = reader.GetDouble(5),
                RotationPeriodS = reader.GetDouble(6),
                AtmosphereHeightM = reader.GetDouble(7),
                SoiRadiusM = reader.GetDouble(8),
                Color = reader.GetString(9),
                IsDefault = reader.GetInt64(10) != 0,
            });
        }
        return list;
    }
}
