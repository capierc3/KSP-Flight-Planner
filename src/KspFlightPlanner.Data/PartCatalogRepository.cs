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

        var power = Query(connection,
            "SELECT part_name, kind, ec_per_s, tracking, inputs FROM ksp_part_power ORDER BY part_name, module_index;",
            r => (Part: r.GetString(0), Power: new PartPower(Enum.TryParse<PowerKind>(r.GetString(1), out var kind) ? kind : PowerKind.Converter,
                r.GetDouble(2), r.GetInt64(3) != 0, r.IsDBNull(4) ? null : ParseInputs(r.GetString(4)))))
            .ToLookup(x => x.Part, x => x.Power);

        var parents = Query(connection, "SELECT node_id, parent_id FROM ksp_tech_parents;", r => (Node: r.GetString(0), Parent: r.GetString(1)))
            .ToLookup(x => x.Node, x => x.Parent);
        var techTree = Query(connection,
            "SELECT id, title, description, cost, x, y, any_to_unlock FROM ksp_tech_nodes ORDER BY sort_order;",
            r => new TechNode(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5),
                parents[r.GetString(0)].ToList(), r.GetInt64(6) != 0));

        var parts = Query(connection,
            "SELECT name, title, category, manufacturer, mass_t, cost, crew_capacity, fuel_crossfeed, tech_required, description, " +
            "bulkhead_profiles, crash_tolerance, max_temp FROM ksp_parts;",
            r =>
            {
                var name = r.GetString(0);
                return new KspPart(name, r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                    r.GetDouble(4), r.GetDouble(5), r.GetInt32(6), partResources[name].ToList(), engines[name].ToList(), antennas[name].ToList(),
                    r.GetInt64(7) != 0, r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9),
                    r.IsDBNull(10) ? [] : r.GetString(10).Split(',', StringSplitOptions.RemoveEmptyEntries), r.GetDouble(11), r.GetDouble(12),
                    power[name].ToList());
            });
        return new PartCatalog(parts, resources, techTree);
    }

    /// <summary>"LiquidFuel=0.0016875;Oxidizer=0.0020625" → propellants.</summary>
    private static List<Propellant> ParseInputs(string text) => text.Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Split('='))
        .Where(x => x.Length == 2)
        .Select(x => new Propellant(x[0], double.TryParse(x[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0))
        .ToList();

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
        Execute(connection, tx, "DELETE FROM ksp_parts; DELETE FROM ksp_resources; DELETE FROM ksp_tech_nodes;"); // child rows cascade

        using (var cmd = Command(connection, tx, "INSERT INTO ksp_resources VALUES ($name, $display, $density, $cost, $flow);"))
            foreach (var r in catalog.Resources.Values)
                Run(cmd, ("$name", r.Name), ("$display", r.DisplayName), ("$density", r.DensityT), ("$cost", r.UnitCost), ("$flow", r.FlowMode));

        using var part = Command(connection, tx,
            "INSERT INTO ksp_parts VALUES ($name, $title, $category, $maker, $mass, $cost, $crew, $crossfeed, $tech, $desc, $sizes, $crash, $temp);");
        using var resource = Command(connection, tx, "INSERT OR REPLACE INTO ksp_part_resources VALUES ($part, $name, $amount, $max);");
        using var engine = Command(connection, tx, """
            INSERT INTO ksp_part_engines (part_name, module_index, engine_id, max_thrust_kn, isp_vacuum_s, isp_sea_level_s)
            VALUES ($part, $index, $id, $thrust, $vac, $asl) RETURNING id;
            """);
        using var propellant = Command(connection, tx, "INSERT OR REPLACE INTO ksp_engine_propellants VALUES ($engine, $name, $ratio);");
        using var antenna = Command(connection, tx, "INSERT INTO ksp_part_antennas VALUES ($part, $index, $type, $power, $comb, $exp);");
        using var power = Command(connection, tx, "INSERT INTO ksp_part_power VALUES ($part, $index, $kind, $rate, $tracking, $inputs);");

        foreach (var p in catalog.Parts.Values)
        {
            Run(part, ("$name", p.Name), ("$title", p.Title), ("$category", p.Category), ("$maker", p.Manufacturer), ("$mass", p.MassT),
                ("$cost", p.Cost), ("$crew", p.CrewCapacity), ("$crossfeed", p.FuelCrossfeed ? 1 : 0), ("$tech", p.TechRequired),
                ("$desc", p.Description), ("$sizes", string.Join(",", p.BulkheadProfiles ?? [])), ("$crash", p.CrashToleranceMs), ("$temp", p.MaxTempK));
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
            for (var i = 0; i < p.PowerModules.Count; i++)
            {
                var w = p.PowerModules[i];
                Run(power, ("$part", p.Name), ("$index", i), ("$kind", w.Kind.ToString()), ("$rate", w.EcPerSecond), ("$tracking", w.Tracking ? 1 : 0),
                    ("$inputs", w.Inputs is { Count: > 0 } inputs
                        ? string.Join(";", inputs.Select(x => $"{x.Name}={x.Ratio.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}"))
                        : null));
            }
        }

        using (var node = Command(connection, tx, "INSERT OR REPLACE INTO ksp_tech_nodes VALUES ($id, $order, $title, $desc, $cost, $x, $y, $any);"))
        using (var parent = Command(connection, tx, "INSERT OR IGNORE INTO ksp_tech_parents VALUES ($id, $parent);"))
            for (var i = 0; i < catalog.TechTree.Count; i++)
            {
                var n = catalog.TechTree[i];
                Run(node, ("$id", n.Id), ("$order", i), ("$title", n.Title), ("$desc", n.Description), ("$cost", n.Cost), ("$x", n.X), ("$y", n.Y),
                    ("$any", n.AnyToUnlock ? 1 : 0));
                foreach (var p in n.Parents) Run(parent, ("$id", n.Id), ("$parent", p));
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
