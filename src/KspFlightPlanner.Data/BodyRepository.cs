using KspFlightPlanner.Core.Ksp;
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
        return WithGameData(connection, WithSurfaceData(connection, ReadBodies(cmd)));
    }

    public CelestialBody? GetBody(long bodyId)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SelectBodies + " WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", bodyId);
        return WithGameData(connection, WithSurfaceData(connection, ReadBodies(cmd))).FirstOrDefault();
    }

    /// <summary>The stock system: the one the player's game is (surface bakes are taken from it) and the Δv map covers.</summary>
    public const string StockSystemName = "KSP Stock";

    /// <summary>
    /// Lays the player's surface bakes over the stock bodies' wiki values where they agree (<see cref="BakeMerge"/>).
    /// Other systems keep their shipped values: the bakes are of the stock bodies.
    /// </summary>
    private static List<CelestialBody> WithGameData(SqliteConnection connection, List<CelestialBody> bodies)
    {
        if (bodies.Count == 0) return bodies;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM star_systems WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", bodies[0].SystemId);
            if (cmd.ExecuteScalar() as string != StockSystemName) return bodies;
        }
        var bakes = SurfaceBakeRepository.ReadSummaries(connection);
        return bakes.Count == 0 ? bodies : bodies.Select(b => BakeMerge.Apply(b, bakes.GetValueOrDefault(b.Name))).ToList();
    }

    private const string SelectBodies = """
        SELECT id, system_id, parent_id, name, body_class, color, is_default,
               mass_kg, mu, equatorial_radius_m, rotation_period_s, soi_radius_m, highest_elevation_m, surface_to_orbit_dv,
               sma_m, eccentricity, inclination_deg, lan_deg, arg_periapsis_deg, mean_anomaly_epoch_rad, epoch_s,
               atmosphere_height_m, sea_level_pressure_atm, scale_height_m, min_temp_c, max_temp_c, has_oxygen,
               molar_mass_g_mol, adiabatic_index, equator_temp_night_c, equator_temp_day_c, pole_temp_low_c, pole_temp_high_c,
               sci_landed, sci_splashed, sci_flying_low, sci_flying_high, sci_space_low, sci_space_high, sci_recovery,
               sci_flying_high_from_m, sci_space_high_from_m
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
                HasOxygen: reader.IsDBNull(26) ? null : reader.GetInt64(26) != 0)
            {
                MolarMassGPerMol = D(27),
                AdiabaticIndex = D(28),
                EquatorTempC = D(29) is { } night && D(30) is { } day ? (night, day) : null,
                PoleTempC = D(31) is { } low && D(32) is { } high ? (low, high) : null,
            };

            var science = Enumerable.Range(33, 9).All(reader.IsDBNull) ? null : new ScienceMultipliers(
                Landed: D(33), Splashed: D(34), FlyingLow: D(35), FlyingHigh: D(36),
                SpaceLow: D(37), SpaceHigh: D(38), Recovery: D(39),
                FlyingHighFromM: D(40), SpaceHighFromM: D(41));

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
                Science = science,
            });
        }
        return list;
    }

    /// <summary>Adds pressure tables, biomes and landmarks (separate tables) to bodies just read.</summary>
    private static List<CelestialBody> WithSurfaceData(SqliteConnection connection, List<CelestialBody> bodies)
    {
        if (bodies.Count == 0) return bodies;
        var ids = string.Join(",", bodies.Select(b => b.Id));

        var pressure = new Dictionary<long, List<AtmospherePoint>>();
        foreach (var r in Query(connection, $"SELECT body_id, altitude_m, pressure_pa, density_kg_m3 FROM body_pressure_points WHERE body_id IN ({ids}) ORDER BY body_id, altitude_m;"))
            Add(pressure, (long)r[0]!, new AtmospherePoint((double)r[1]!, (double)r[2]!, (double?)r[3]));

        var biomes = new Dictionary<long, List<string>>();
        foreach (var r in Query(connection, $"SELECT body_id, name FROM body_biomes WHERE body_id IN ({ids}) ORDER BY body_id, sort_order;"))
            Add(biomes, (long)r[0]!, (string)r[1]!);

        var landmarks = new Dictionary<long, List<Landmark>>();
        foreach (var r in Query(connection, $"SELECT body_id, name, kind, latitude_deg, longitude_deg, note, source FROM body_landmarks WHERE body_id IN ({ids}) ORDER BY body_id, sort_order;"))
            Add(landmarks, (long)r[0]!, new Landmark((string)r[1]!, (string)r[2]!, (double)r[3]!, (double)r[4]!, (string?)r[5], (string?)r[6]));

        return bodies.Select(b => b with
        {
            Atmosphere = b.Atmosphere is { } a && pressure.TryGetValue(b.Id, out var table) ? a with { PressureTable = table } : b.Atmosphere,
            Biomes = biomes.GetValueOrDefault(b.Id) ?? [],
            Landmarks = landmarks.GetValueOrDefault(b.Id) ?? [],
        }).ToList();

        static void Add<T>(Dictionary<long, List<T>> map, long key, T value)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.Add(value);
        }
    }

    private static IEnumerable<object?[]> Query(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            yield return row;
        }
    }
}
