using System.Text.Json;
using KspFlightPlanner.Core;
using Microsoft.Data.Sqlite;

namespace KspFlightPlanner.Data;

/// <summary>
/// Loads the built-in systems from the embedded Seed/bodies.json
/// (source: Eric Meyer's Resonant Orbit Calculator, MIT License).
/// </summary>
internal static class BodySeeder
{
    private const string ResourceName = "KspFlightPlanner.Data.Seed.bodies.json";

    private sealed record SeedFile(List<SeedSystem> Systems);
    private sealed record SeedSystem(string Name, List<SeedBody> Bodies);
    private sealed record SeedBody(
        string Name, string Color, double MassKg, double EquatorialRadiusM,
        double RotationPeriodS, double AtmosphereHeightM, double SoiRadiusM, bool IsDefault);

    public static void SeedIfEmpty(SqliteConnection connection)
    {
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM star_systems;";
            if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;
        }

        var seed = LoadSeed();
        using var tx = connection.BeginTransaction();

        using var insertSystem = connection.CreateCommand();
        insertSystem.Transaction = tx;
        insertSystem.CommandText =
            "INSERT INTO star_systems (name, sort_order, is_builtin) VALUES ($name, $order, 1) RETURNING id;";
        var sysName = insertSystem.Parameters.Add("$name", SqliteType.Text);
        var sysOrder = insertSystem.Parameters.Add("$order", SqliteType.Integer);

        using var insertBody = connection.CreateCommand();
        insertBody.Transaction = tx;
        insertBody.CommandText = """
            INSERT INTO celestial_bodies
                (system_id, name, mass_kg, mu, equatorial_radius_m, rotation_period_s,
                 atmosphere_height_m, soi_radius_m, color, is_default, sort_order, is_builtin)
            VALUES ($sys, $name, $mass, $mu, $eqr, $rot, $atm, $soi, $color, $default, $order, 1);
            """;
        var p = insertBody.Parameters;
        var bSys = p.Add("$sys", SqliteType.Integer);
        var bName = p.Add("$name", SqliteType.Text);
        var bMass = p.Add("$mass", SqliteType.Real);
        var bMu = p.Add("$mu", SqliteType.Real);
        var bEqr = p.Add("$eqr", SqliteType.Real);
        var bRot = p.Add("$rot", SqliteType.Real);
        var bAtm = p.Add("$atm", SqliteType.Real);
        var bSoi = p.Add("$soi", SqliteType.Real);
        var bColor = p.Add("$color", SqliteType.Text);
        var bDefault = p.Add("$default", SqliteType.Integer);
        var bOrder = p.Add("$order", SqliteType.Integer);

        for (var s = 0; s < seed.Systems.Count; s++)
        {
            var system = seed.Systems[s];
            sysName.Value = system.Name;
            sysOrder.Value = s;
            var systemId = Convert.ToInt64(insertSystem.ExecuteScalar());

            for (var b = 0; b < system.Bodies.Count; b++)
            {
                var body = system.Bodies[b];
                bSys.Value = systemId;
                bName.Value = body.Name;
                bMass.Value = body.MassKg;
                bMu.Value = body.MassKg * OrbitMath.G;
                bEqr.Value = body.EquatorialRadiusM;
                bRot.Value = body.RotationPeriodS;
                bAtm.Value = body.AtmosphereHeightM;
                bSoi.Value = body.SoiRadiusM;
                bColor.Value = NormalizeColor(body.Color);
                bDefault.Value = body.IsDefault ? 1 : 0;
                bOrder.Value = b;
                insertBody.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    private static SeedFile LoadSeed()
    {
        using var stream = typeof(BodySeeder).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
        return JsonSerializer.Deserialize<SeedFile>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Seed file is empty.");
    }

    /// <summary>Expands 3-digit hex colours (#FE9) to 6 digits (#FFEE99).</summary>
    private static string NormalizeColor(string color)
    {
        var c = color.Trim();
        if (c.Length == 4 && c[0] == '#')
            return $"#{c[1]}{c[1]}{c[2]}{c[2]}{c[3]}{c[3]}".ToUpperInvariant();
        return string.IsNullOrEmpty(c) ? "#666666" : c.ToUpperInvariant();
    }
}
