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
        SELECT id, system_id, parent_id, name, body_class, color, is_default,
               mass_kg, mu, equatorial_radius_m, rotation_period_s, soi_radius_m, highest_elevation_m, surface_to_orbit_dv,
               sma_m, eccentricity, inclination_deg, lan_deg, arg_periapsis_deg, mean_anomaly_epoch_rad, epoch_s,
               atmosphere_height_m, sea_level_pressure_atm, scale_height_m, min_temp_c, max_temp_c, has_oxygen
        FROM celestial_bodies
        """;

    private static List<CelestialBody> ReadBodies(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var list = new List<CelestialBody>();
        while (reader.Read())
        {
            double? D(int i) => reader.IsDBNull(i) ? null : reader.GetDouble(i);

            var orbit = reader.IsDBNull(14) ? null : new OrbitalElements(
                SemiMajorAxisM: reader.GetDouble(14),
                Eccentricity: reader.GetDouble(15),
                InclinationDeg: reader.GetDouble(16),
                LongitudeOfAscendingNodeDeg: reader.GetDouble(17),
                ArgumentOfPeriapsisDeg: reader.GetDouble(18),
                MeanAnomalyAtEpochRad: reader.GetDouble(19),
                EpochS: D(20) ?? 0);

            var atmosphere = reader.IsDBNull(21) ? null : new Atmosphere(
                HeightM: reader.GetDouble(21),
                SeaLevelPressureAtm: D(22),
                ScaleHeightM: D(23),
                MinTempC: D(24),
                MaxTempC: D(25),
                HasOxygen: reader.IsDBNull(26) ? null : reader.GetInt64(26) != 0);

            list.Add(new CelestialBody
            {
                Id = reader.GetInt64(0),
                SystemId = reader.GetInt64(1),
                ParentId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                Name = reader.GetString(3),
                BodyClass = reader.IsDBNull(4) ? null : reader.GetString(4),
                Color = reader.GetString(5),
                IsDefault = reader.GetInt64(6) != 0,
                MassKg = reader.GetDouble(7),
                Mu = reader.GetDouble(8),
                EquatorialRadiusM = reader.GetDouble(9),
                RotationPeriodS = reader.GetDouble(10),
                SoiRadiusM = reader.GetDouble(11),
                HighestElevationM = D(12),
                SurfaceToLowOrbitDv = D(13),
                Orbit = orbit,
                Atmosphere = atmosphere,
            });
        }
        return list;
    }
}
