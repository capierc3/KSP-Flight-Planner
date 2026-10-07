using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace KspFlightPlanner.Data;

/// <summary>
/// Loads the built-in systems from the embedded Seed/bodies.json. Sources are listed in the file:
/// Eric Meyer's Resonant Orbit Calculator (MIT) for all systems, the KSP wiki for stock orbit/atmosphere data.
/// </summary>
internal static class BodySeeder
{
    private const string ResourceName = "KspFlightPlanner.Data.Seed.bodies.json";

    /// <summary>Hash of the embedded seed file; changes whenever the built-in data changes.</summary>
    public static string Fingerprint { get; } = Hashing.Sha256(OpenSeed());

    private sealed record SeedFile(List<SeedSystem> Systems);
    private sealed record SeedSystem(string Name, List<SeedBody> Bodies);
    private sealed record SeedBody(
        string Name, string Color, bool IsDefault, string? BodyClass, string? Parent,
        double Mu, double MassKg, double EquatorialRadiusM, double RotationPeriodS, double SoiRadiusM,
        double? HighestElevationM, double? SurfaceToLowOrbitDv, SeedOrbit? Orbit, SeedAtmosphere? Atmosphere);
    private sealed record SeedOrbit(
        double SemiMajorAxisM, double Eccentricity, double InclinationDeg, double LongitudeOfAscendingNodeDeg,
        double ArgumentOfPeriapsisDeg, double MeanAnomalyAtEpochRad);
    private sealed record SeedAtmosphere(
        double HeightM, double? SeaLevelPressureAtm, double? ScaleHeightM, double? MinTempC, double? MaxTempC, bool? HasOxygen);

    public static void SeedIfEmpty(SqliteConnection connection)
    {
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM star_systems;";
            if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;
        }

        var seed = LoadSeed();
        using var tx = connection.BeginTransaction();

        for (var s = 0; s < seed.Systems.Count; s++)
        {
            var system = seed.Systems[s];
            long systemId;
            using (var insertSystem = connection.CreateCommand())
            {
                insertSystem.Transaction = tx;
                insertSystem.CommandText =
                    "INSERT INTO star_systems (name, sort_order, is_builtin) VALUES ($name, $order, 1) RETURNING id;";
                insertSystem.Parameters.AddWithValue("$name", system.Name);
                insertSystem.Parameters.AddWithValue("$order", s);
                systemId = Convert.ToInt64(insertSystem.ExecuteScalar());
            }

            var ids = new Dictionary<string, long>();
            for (var b = 0; b < system.Bodies.Count; b++)
                ids[system.Bodies[b].Name] = InsertBody(connection, tx, systemId, system.Bodies[b], b);

            // Parents are linked after insert so bodies can be listed in any order.
            foreach (var body in system.Bodies.Where(x => x.Parent is not null))
            {
                using var link = connection.CreateCommand();
                link.Transaction = tx;
                link.CommandText = "UPDATE celestial_bodies SET parent_id = $parent WHERE id = $id;";
                link.Parameters.AddWithValue("$parent", ids.TryGetValue(body.Parent!, out var pid)
                    ? pid : throw new InvalidOperationException($"{body.Name}: unknown parent '{body.Parent}'."));
                link.Parameters.AddWithValue("$id", ids[body.Name]);
                link.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    private static long InsertBody(SqliteConnection connection, SqliteTransaction tx, long systemId, SeedBody body, int order)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO celestial_bodies
                (system_id, name, body_class, color, is_default, sort_order, is_builtin,
                 mass_kg, mu, equatorial_radius_m, rotation_period_s, soi_radius_m, highest_elevation_m, surface_to_orbit_dv,
                 sma_m, eccentricity, inclination_deg, lan_deg, arg_periapsis_deg, mean_anomaly_epoch_rad, epoch_s,
                 atmosphere_height_m, sea_level_pressure_atm, scale_height_m, min_temp_c, max_temp_c, has_oxygen)
            VALUES
                ($sys, $name, $class, $color, $default, $order, 1,
                 $mass, $mu, $eqr, $rot, $soi, $highest, $dvOrbit,
                 $sma, $ecc, $inc, $lan, $argPe, $mean, $epoch,
                 $atmHeight, $pressure, $scaleHeight, $minTemp, $maxTemp, $oxygen)
            RETURNING id;
            """;
        var p = cmd.Parameters;
        p.AddWithValue("$sys", systemId);
        p.AddWithValue("$name", body.Name);
        p.AddWithValue("$class", Db(body.BodyClass));
        p.AddWithValue("$color", NormalizeColor(body.Color));
        p.AddWithValue("$default", body.IsDefault ? 1 : 0);
        p.AddWithValue("$order", order);
        p.AddWithValue("$mass", body.MassKg);
        p.AddWithValue("$mu", body.Mu);
        p.AddWithValue("$eqr", body.EquatorialRadiusM);
        p.AddWithValue("$rot", body.RotationPeriodS);
        p.AddWithValue("$soi", body.SoiRadiusM);
        p.AddWithValue("$highest", Db(body.HighestElevationM));
        p.AddWithValue("$dvOrbit", Db(body.SurfaceToLowOrbitDv));

        var o = body.Orbit;
        p.AddWithValue("$sma", Db(o?.SemiMajorAxisM));
        p.AddWithValue("$ecc", Db(o?.Eccentricity));
        p.AddWithValue("$inc", Db(o?.InclinationDeg));
        p.AddWithValue("$lan", Db(o?.LongitudeOfAscendingNodeDeg));
        p.AddWithValue("$argPe", Db(o?.ArgumentOfPeriapsisDeg));
        p.AddWithValue("$mean", Db(o?.MeanAnomalyAtEpochRad));
        p.AddWithValue("$epoch", o is null ? DBNull.Value : 0.0);

        var a = body.Atmosphere;
        p.AddWithValue("$atmHeight", Db(a?.HeightM));
        p.AddWithValue("$pressure", Db(a?.SeaLevelPressureAtm));
        p.AddWithValue("$scaleHeight", Db(a?.ScaleHeightM));
        p.AddWithValue("$minTemp", Db(a?.MinTempC));
        p.AddWithValue("$maxTemp", Db(a?.MaxTempC));
        p.AddWithValue("$oxygen", a?.HasOxygen is { } ox ? (ox ? 1 : 0) : DBNull.Value);

        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static object Db(object? value) => value ?? DBNull.Value;

    private static SeedFile LoadSeed()
    {
        using var stream = OpenSeed();
        return JsonSerializer.Deserialize<SeedFile>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Seed file is empty.");
    }

    private static Stream OpenSeed() =>
        typeof(BodySeeder).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");

    /// <summary>Expands 3-digit hex colours (#FE9) to 6 digits (#FFEE99).</summary>
    private static string NormalizeColor(string color)
    {
        var c = color.Trim();
        if (c.Length == 4 && c[0] == '#')
            return $"#{c[1]}{c[1]}{c[2]}{c[2]}{c[3]}{c[3]}".ToUpperInvariant();
        return string.IsNullOrEmpty(c) ? "#666666" : c.ToUpperInvariant();
    }
}
