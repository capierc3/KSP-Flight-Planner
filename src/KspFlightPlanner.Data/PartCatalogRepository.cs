using System.Globalization;
using KspFlightPlanner.Core.Ksp;
using Microsoft.Data.Sqlite;

namespace KspFlightPlanner.Data;

/// <summary>Where the stored part data came from and when it was read.</summary>
/// <param name="SourceWriteUtc">Last change of the source file or folder; a newer one means the data should be read again.</param>
/// <param name="FormatVersion">The <see cref="PartCatalogReader.FormatVersion"/> it was read with; older data is read again.</param>
public sealed record PartCatalogInfo(string Source, DateTime SourceWriteUtc, DateTime ReadUtc, int PartCount, int FormatVersion);

/// <summary>Stores the <see cref="PartCatalog"/> read from the KSP install (ksp_* tables), replacing it as a whole.</summary>
public sealed class PartCatalogRepository(PlannerDatabase database)
{
    private const string SourceKey = "ksp.parts.source";
    private const string SourceWriteKey = "ksp.parts.source_write_utc";
    private const string ReadKey = "ksp.parts.read_utc";
    private const string FormatKey = "ksp.parts.format";

    public PartCatalog Load()
    {
        using var connection = database.Open();

        var resources = Query(connection, "SELECT name, display_name, density_t, unit_cost, flow_mode FROM ksp_resources;",
            r => new KspResource(r.GetString(0), r.GetString(1), r.GetDouble(2), r.GetDouble(3), r.GetString(4)));

        var partResources = Query(connection, "SELECT part_name, resource_name, amount, max_amount FROM ksp_part_resources;",
            r => (Part: r.GetString(0), Resource: new PartResource(r.GetString(1), r.GetDouble(2), r.GetDouble(3))))
            .ToLookup(x => x.Part, x => x.Resource);
        var propellants = Query(connection, "SELECT engine_id, resource_name, ratio FROM ksp_engine_propellants;",
            r => (Engine: r.GetInt64(0), Propellant: new Propellant(r.GetString(1), r.GetDouble(2))))
            .ToLookup(x => x.Engine, x => x.Propellant);
        var engines = Query(connection,
            "SELECT id, part_name, engine_id, max_thrust_kn, isp_vacuum_s, isp_sea_level_s FROM ksp_part_engines ORDER BY part_name, module_index;",
            r => (Part: r.GetString(1), Engine: new PartEngine(r.IsDBNull(2) ? null : r.GetString(2), r.GetDouble(3), r.GetDouble(4),
                r.GetDouble(5), propellants[r.GetInt64(0)].ToList())))
            .ToLookup(x => x.Part, x => x.Engine);
        var antennas = Query(connection,
            "SELECT part_name, antenna_type, power, combinable, combinable_exponent FROM ksp_part_antennas ORDER BY part_name, module_index;",
            r => (Part: r.GetString(0), Antenna: new PartAntenna(r.GetString(1), r.GetDouble(2), r.GetInt64(3) != 0, r.GetDouble(4))))
            .ToLookup(x => x.Part, x => x.Antenna);

        var parts = Query(connection, "SELECT name, title, category, manufacturer, mass_t, cost, crew_capacity, fuel_crossfeed FROM ksp_parts;",
            r =>
            {
                var name = r.GetString(0);
                return new KspPart(name, r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                    r.GetDouble(4), r.GetDouble(5), r.GetInt32(6), partResources[name].ToList(), engines[name].ToList(), antennas[name].ToList(),
                    r.GetInt64(7) != 0);
            });
        return new PartCatalog(parts, resources);
    }

    public PartCatalogInfo? GetInfo()
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM app_settings WHERE key IN ($source, $write, $read, $format);";
        cmd.Parameters.AddWithValue("$source", SourceKey);
        cmd.Parameters.AddWithValue("$write", SourceWriteKey);
        cmd.Parameters.AddWithValue("$read", ReadKey);
        cmd.Parameters.AddWithValue("$format", FormatKey);
        var values = new Dictionary<string, string>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) values[reader.GetString(0)] = reader.GetString(1);
        if (!values.TryGetValue(SourceKey, out var source)) return null;

        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM ksp_parts;";
        return new PartCatalogInfo(source, Date(values.GetValueOrDefault(SourceWriteKey)), Date(values.GetValueOrDefault(ReadKey)),
            Convert.ToInt32(count.ExecuteScalar()), int.TryParse(values.GetValueOrDefault(FormatKey), out var format) ? format : 1);
    }

    /// <summary>Replaces all stored part data with <paramref name="catalog"/> in one transaction.</summary>
    public void Replace(PartCatalog catalog, string source, DateTime sourceWriteUtc)
    {
        using var connection = database.Open();
        using var tx = connection.BeginTransaction();
        Execute(connection, tx, "DELETE FROM ksp_parts; DELETE FROM ksp_resources;"); // child rows cascade

        using (var cmd = Command(connection, tx, "INSERT INTO ksp_resources VALUES ($name, $display, $density, $cost, $flow);"))
            foreach (var r in catalog.Resources.Values)
                Run(cmd, ("$name", r.Name), ("$display", r.DisplayName), ("$density", r.DensityT), ("$cost", r.UnitCost), ("$flow", r.FlowMode));

        using var part = Command(connection, tx,
            "INSERT INTO ksp_parts VALUES ($name, $title, $category, $maker, $mass, $cost, $crew, $crossfeed);");
        using var resource = Command(connection, tx, "INSERT OR REPLACE INTO ksp_part_resources VALUES ($part, $name, $amount, $max);");
        using var engine = Command(connection, tx, """
            INSERT INTO ksp_part_engines (part_name, module_index, engine_id, max_thrust_kn, isp_vacuum_s, isp_sea_level_s)
            VALUES ($part, $index, $id, $thrust, $vac, $asl) RETURNING id;
            """);
        using var propellant = Command(connection, tx, "INSERT OR REPLACE INTO ksp_engine_propellants VALUES ($engine, $name, $ratio);");
        using var antenna = Command(connection, tx, "INSERT INTO ksp_part_antennas VALUES ($part, $index, $type, $power, $comb, $exp);");

        foreach (var p in catalog.Parts.Values)
        {
            Run(part, ("$name", p.Name), ("$title", p.Title), ("$category", p.Category), ("$maker", p.Manufacturer), ("$mass", p.MassT),
                ("$cost", p.Cost), ("$crew", p.CrewCapacity), ("$crossfeed", p.FuelCrossfeed ? 1 : 0));
            foreach (var r in p.Resources)
                Run(resource, ("$part", p.Name), ("$name", r.Name), ("$amount", r.Amount), ("$max", r.MaxAmount));
            for (var i = 0; i < p.Engines.Count; i++)
            {
                var e = p.Engines[i];
                Set(engine, ("$part", p.Name), ("$index", i), ("$id", e.EngineId), ("$thrust", e.MaxThrustKn), ("$vac", e.IspVacuumS),
                    ("$asl", e.IspSeaLevelS));
                var id = Convert.ToInt64(engine.ExecuteScalar());
                foreach (var pr in e.Propellants)
                    Run(propellant, ("$engine", id), ("$name", pr.Name), ("$ratio", pr.Ratio));
            }
            for (var i = 0; i < p.Antennas.Count; i++)
            {
                var a = p.Antennas[i];
                Run(antenna, ("$part", p.Name), ("$index", i), ("$type", a.Type), ("$power", a.Power), ("$comb", a.Combinable ? 1 : 0),
                    ("$exp", a.CombinableExponent));
            }
        }

        using (var meta = Command(connection, tx,
                   "INSERT INTO app_settings (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = $value;"))
        {
            Run(meta, ("$key", SourceKey), ("$value", source));
            Run(meta, ("$key", SourceWriteKey), ("$value", sourceWriteUtc.ToString("O", CultureInfo.InvariantCulture)));
            Run(meta, ("$key", ReadKey), ("$value", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
            Run(meta, ("$key", FormatKey), ("$value", PartCatalogReader.FormatVersion.ToString(CultureInfo.InvariantCulture)));
        }
        tx.Commit();
    }

    private static List<T> Query<T>(SqliteConnection connection, string sql, Func<SqliteDataReader, T> map)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var list = new List<T>();
        while (reader.Read()) list.Add(map(reader));
        return list;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction tx, string sql)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction tx, string sql)
    {
        using var cmd = Command(connection, tx, sql);
        cmd.ExecuteNonQuery();
    }

    private static void Set(SqliteCommand cmd, params (string Name, object? Value)[] values)
    {
        cmd.Parameters.Clear();
        foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static void Run(SqliteCommand cmd, params (string Name, object? Value)[] values)
    {
        Set(cmd, values);
        cmd.ExecuteNonQuery();
    }

    private static DateTime Date(string? text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date : DateTime.MinValue;
}
