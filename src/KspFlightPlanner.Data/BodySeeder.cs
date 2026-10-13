using System.Text.Json;
using KspFlightPlanner.Core.Models;
using Microsoft.Data.Sqlite;

namespace KspFlightPlanner.Data;

/// <summary>
/// Loads the built-in systems from the embedded Seed/bodies.json. Sources are listed in the file:
/// Eric Meyer's Resonant Orbit Calculator (MIT) for all systems, the KSP wiki for stock orbit/atmosphere data, biomes,
/// landmarks and science multipliers (merged in by tools/wiki).
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
        double? HighestElevationM, SeedOrbit? Orbit, SeedAtmosphere? Atmosphere,
        SeedScience? Science = null, List<string>? Biomes = null, List<SeedLandmark>? Landmarks = null);
    private sealed record SeedOrbit(
        double SemiMajorAxisM, double Eccentricity, double InclinationDeg, double LongitudeOfAscendingNodeDeg,
        double ArgumentOfPeriapsisDeg, double MeanAnomalyAtEpochRad);
    private sealed record SeedAtmosphere(
        double HeightM, double? SeaLevelPressureAtm, double? ScaleHeightM, double? MinTempC, double? MaxTempC, bool? HasOxygen,
        double? MolarMassGPerMol = null, double? AdiabaticIndex = null, SeedTempRange? EquatorTempC = null,
        SeedTempRange? PoleTempC = null, List<SeedPressurePoint>? PressureTable = null);
    private sealed record SeedTempRange(double? Night, double? Day, double? Low, double? High);
    private sealed record SeedPressurePoint(double AltitudeM, double PressurePa, double? DensityKgM3);
    private sealed record SeedScience(
        double? Landed, double? Splashed, double? LowerAtmosphere, double? UpperAtmosphere, double? NearSpace,
        double? OuterSpace, double? Recovery, double? FlyingHighAltitudeM, double? SpaceHighAltitudeM);
    private sealed record SeedLandmark(string Name, string Kind, double LatitudeDeg, double LongitudeDeg, string? Note, string? Source);

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
                 mass_kg, mu, equatorial_radius_m, rotation_period_s, soi_radius_m, highest_elevation_m,
                 sma_m, eccentricity, inclination_deg, lan_deg, arg_periapsis_deg, mean_anomaly_epoch_rad, epoch_s,
                 atmosphere_height_m, sea_level_pressure_atm, scale_height_m, min_temp_c, max_temp_c, has_oxygen,
                 molar_mass_g_mol, adiabatic_index, equator_temp_night_c, equator_temp_day_c, pole_temp_low_c, pole_temp_high_c,
                 sci_landed, sci_splashed, sci_flying_low, sci_flying_high, sci_space_low, sci_space_high, sci_recovery,
                 sci_flying_high_from_m, sci_space_high_from_m)
            VALUES
                ($sys, $name, $class, $color, $default, $order, 1,
                 $mass, $mu, $eqr, $rot, $soi, $highest,
                 $sma, $ecc, $inc, $lan, $argPe, $mean, $epoch,
                 $atmHeight, $pressure, $scaleHeight, $minTemp, $maxTemp, $oxygen,
                 $molar, $gamma, $eqNight, $eqDay, $poleLow, $poleHigh,
                 $sciLanded, $sciSplashed, $sciFlyLow, $sciFlyHigh, $sciSpaceLow, $sciSpaceHigh, $sciRecovery,
                 $sciFlyHighFrom, $sciSpaceHighFrom)
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
        p.AddWithValue("$molar", Db(a?.MolarMassGPerMol));
        p.AddWithValue("$gamma", Db(a?.AdiabaticIndex));
        p.AddWithValue("$eqNight", Db(a?.EquatorTempC?.Night));
        p.AddWithValue("$eqDay", Db(a?.EquatorTempC?.Day));
        p.AddWithValue("$poleLow", Db(a?.PoleTempC?.Low));
        p.AddWithValue("$poleHigh", Db(a?.PoleTempC?.High));

        var sci = body.Science;
        p.AddWithValue("$sciLanded", Db(sci?.Landed));
        p.AddWithValue("$sciSplashed", Db(sci?.Splashed));
        p.AddWithValue("$sciFlyLow", Db(sci?.LowerAtmosphere));
        p.AddWithValue("$sciFlyHigh", Db(sci?.UpperAtmosphere));
        p.AddWithValue("$sciSpaceLow", Db(sci?.NearSpace));
        p.AddWithValue("$sciSpaceHigh", Db(sci?.OuterSpace));
        p.AddWithValue("$sciRecovery", Db(sci?.Recovery));
        p.AddWithValue("$sciFlyHighFrom", Db(sci?.FlyingHighAltitudeM));
        p.AddWithValue("$sciSpaceHighFrom", Db(sci?.SpaceHighAltitudeM));

        var id = Convert.ToInt64(cmd.ExecuteScalar());
        InsertSurfaceData(connection, tx, id, body);
        return id;
    }

    private static void InsertSurfaceData(SqliteConnection connection, SqliteTransaction tx, long bodyId, SeedBody body)
    {
        foreach (var point in body.Atmosphere?.PressureTable ?? [])
            Insert(connection, tx, "INSERT INTO body_pressure_points (body_id, altitude_m, pressure_pa, density_kg_m3) VALUES ($b, $1, $2, $3);",
                bodyId, point.AltitudeM, point.PressurePa, point.DensityKgM3);
        var biomes = body.Biomes ?? [];
        for (var i = 0; i < biomes.Count; i++)
            Insert(connection, tx, "INSERT INTO body_biomes (body_id, sort_order, name) VALUES ($b, $1, $2);", bodyId, i, biomes[i]);
        var landmarks = body.Landmarks ?? [];
        for (var i = 0; i < landmarks.Count; i++)
        {
            var m = landmarks[i];
            Insert(connection, tx, """
                INSERT INTO body_landmarks (body_id, sort_order, name, kind, latitude_deg, longitude_deg, note, source)
                VALUES ($b, $1, $2, $3, $4, $5, $6, $7);
                """, bodyId, i, m.Name, m.Kind, m.LatitudeDeg, m.LongitudeDeg, m.Note, m.Source);
        }
    }

    private static void Insert(SqliteConnection connection, SqliteTransaction tx, string sql, long bodyId, params object?[] values)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$b", bodyId);
        for (var i = 0; i < values.Length; i++) cmd.Parameters.AddWithValue($"${i + 1}", Db(values[i]));
        cmd.ExecuteNonQuery();
    }

    private static object Db(object? value) => value ?? DBNull.Value;

    /// <summary>
    /// The physical data of one built-in system as bodies (ids are list positions, not database ids), for work that
    /// needs the seed data before it's stored, such as generating the Δv map.
    /// </summary>
    public static List<CelestialBody> LoadBodies(string systemName)
    {
        var seeds = LoadSeed().Systems.Single(s => s.Name == systemName).Bodies;
        var ids = seeds.Select((b, i) => (b.Name, Id: (long)i)).ToDictionary(x => x.Name, x => x.Id);
        return seeds.Select((b, i) => new CelestialBody
        {
            Id = i,
            Name = b.Name,
            BodyClass = b.BodyClass,
            ParentId = b.Parent is null ? null : ids[b.Parent],
            IsDefault = b.IsDefault,
            MassKg = b.MassKg,
            Mu = b.Mu,
            EquatorialRadiusM = b.EquatorialRadiusM,
            RotationPeriodS = b.RotationPeriodS,
            SoiRadiusM = b.SoiRadiusM,
            HighestElevationM = b.HighestElevationM,
            Orbit = b.Orbit is { } o ? new OrbitalElements(o.SemiMajorAxisM, o.Eccentricity, o.InclinationDeg,
                o.LongitudeOfAscendingNodeDeg, o.ArgumentOfPeriapsisDeg, o.MeanAnomalyAtEpochRad) : null,
            Atmosphere = b.Atmosphere is { } a ? new Atmosphere(a.HeightM, a.SeaLevelPressureAtm, a.ScaleHeightM,
                a.MinTempC, a.MaxTempC, a.HasOxygen) : null,
        }).ToList();
    }

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
