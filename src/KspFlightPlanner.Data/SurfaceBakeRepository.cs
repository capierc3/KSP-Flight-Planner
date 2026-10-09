using System.Globalization;
using System.Text.Json;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;
using Microsoft.Data.Sqlite;

namespace KspFlightPlanner.Data;

/// <summary>Surface bakes (terrain, biomes, rotation, air sampled from the game), one per body of the user's install.</summary>
public sealed class SurfaceBakeRepository(PlannerDatabase database)
{
    private readonly Dictionary<string, SurfaceBake?> _cache = new(StringComparer.Ordinal);

    /// <summary>The body's bake, or null if it hasn't been baked. Cached; <see cref="Save"/> and <see cref="Delete"/> refresh it.</summary>
    public SurfaceBake? Get(string bodyName)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(bodyName, out var cached)) return cached;
            return _cache[bodyName] = Read(bodyName);
        }
    }

    // Summaries need the whole height grid (for its highest point); kept per database file and bake so it's read once.
    private static readonly Dictionary<(string Database, string Body, string BakedUtc), BakeSummary> Summaries = [];

    /// <summary>Every body's bake summary (constants, highest grid point, air), by body name.</summary>
    internal static IReadOnlyDictionary<string, BakeSummary> ReadSummaries(SqliteConnection connection)
    {
        var current = new List<(string Database, string Body, string Utc)>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT body_name, baked_utc FROM ksp_surface_bakes;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) current.Add((connection.DataSource, r.GetString(0), r.GetString(1)));
        }
        var result = new Dictionary<string, BakeSummary>(StringComparer.Ordinal);
        foreach (var key in current)
        {
            BakeSummary? summary;
            lock (Summaries) Summaries.TryGetValue(key, out summary);
            if (summary is null)
            {
                if (Read(connection, key.Body) is not { } bake) continue;
                summary = BakeSummary.From(bake);
                lock (Summaries) Summaries[key] = summary;
            }
            result[key.Body] = summary;
        }
        return result;
    }

    /// <summary>Bodies baked so far, with when and how finely.</summary>
    public IReadOnlyList<(string Body, double StepDeg, DateTime BakedUtc)> List()
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT body_name, step_deg, baked_utc FROM ksp_surface_bakes ORDER BY body_name;";
        using var reader = cmd.ExecuteReader();
        var list = new List<(string, double, DateTime)>();
        while (reader.Read()) list.Add((reader.GetString(0), reader.GetDouble(1), ParseUtc(reader.GetString(2))));
        return list;
    }

    public void Save(SurfaceBake bake)
    {
        using var connection = database.Open();
        using var tx = connection.BeginTransaction();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR REPLACE INTO ksp_surface_bakes
                    (body_name, step_deg, initial_rotation_deg, rotation_period_s, radius_m, mu, baked_utc, heights, biomes, biome_names)
                VALUES ($name, $step, $rot0, $period, $radius, $mu, $utc, $heights, $biomes, $names);
                """;
            cmd.Parameters.AddWithValue("$name", bake.BodyName);
            cmd.Parameters.AddWithValue("$step", bake.StepDeg);
            cmd.Parameters.AddWithValue("$rot0", bake.InitialRotationDeg);
            cmd.Parameters.AddWithValue("$period", bake.RotationPeriodS);
            cmd.Parameters.AddWithValue("$radius", bake.RadiusM);
            cmd.Parameters.AddWithValue("$mu", bake.Mu);
            cmd.Parameters.AddWithValue("$utc", bake.BakedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$heights", ToBytes(bake.Heights));
            cmd.Parameters.AddWithValue("$biomes", bake.Biomes);
            cmd.Parameters.AddWithValue("$names", JsonSerializer.Serialize(bake.BiomeNames));
            cmd.ExecuteNonQuery();
        }
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM ksp_surface_bake_air WHERE body_name = $name;";
            clear.Parameters.AddWithValue("$name", bake.BodyName);
            clear.ExecuteNonQuery();
        }
        foreach (var point in bake.Atmosphere)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO ksp_surface_bake_air (body_name, altitude_m, pressure_pa, density_kg_m3) VALUES ($name, $alt, $p, $rho);";
            cmd.Parameters.AddWithValue("$name", bake.BodyName);
            cmd.Parameters.AddWithValue("$alt", point.AltitudeM);
            cmd.Parameters.AddWithValue("$p", point.PressurePa);
            cmd.Parameters.AddWithValue("$rho", point.DensityKgM3 is { } rho ? rho : DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        lock (_cache) _cache[bake.BodyName] = bake;
    }

    public void Delete(string bodyName)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM ksp_surface_bake_air WHERE body_name = $name; DELETE FROM ksp_surface_bakes WHERE body_name = $name;";
        cmd.Parameters.AddWithValue("$name", bodyName);
        cmd.ExecuteNonQuery();
        lock (_cache) _cache.Remove(bodyName);
    }

    private SurfaceBake? Read(string bodyName)
    {
        using var connection = database.Open();
        return Read(connection, bodyName);
    }

    private static SurfaceBake? Read(SqliteConnection connection, string bodyName)
    {
        SurfaceBake? bake;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT step_deg, initial_rotation_deg, rotation_period_s, radius_m, mu, baked_utc, heights, biomes, biome_names
                FROM ksp_surface_bakes WHERE body_name = $name;
                """;
            cmd.Parameters.AddWithValue("$name", bodyName);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            bake = new SurfaceBake(bodyName, r.GetDouble(0), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4),
                ParseUtc(r.GetString(5)), FromBytes((byte[])r.GetValue(6)), (byte[])r.GetValue(7),
                JsonSerializer.Deserialize<List<string>>(r.GetString(8)) ?? [], []);
        }
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT altitude_m, pressure_pa, density_kg_m3 FROM ksp_surface_bake_air WHERE body_name = $name ORDER BY altitude_m;";
            cmd.Parameters.AddWithValue("$name", bodyName);
            using var r = cmd.ExecuteReader();
            var air = new List<AtmospherePoint>();
            while (r.Read()) air.Add(new AtmospherePoint(r.GetDouble(0), r.GetDouble(1), r.IsDBNull(2) ? null : r.GetDouble(2)));
            return bake with { Atmosphere = air };
        }
    }

    private static byte[] ToBytes(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("Surface bakes are stored little-endian.");
        return bytes;
    }

    private static float[] FromBytes(byte[] bytes)
    {
        var values = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, values, 0, values.Length * sizeof(float));
        return values;
    }

    private static DateTime ParseUtc(string text) =>
        DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
