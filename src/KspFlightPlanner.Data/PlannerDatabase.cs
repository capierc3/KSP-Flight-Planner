using Microsoft.Data.Sqlite;

namespace KspFlightPlanner.Data;

/// <summary>
/// Owns the SQLite file: opens connections, applies schema migrations (tracked with
/// PRAGMA user_version) and seeds the built-in body data on first run.
/// </summary>
public sealed class PlannerDatabase
{
    private readonly string _connectionString;

    public PlannerDatabase(string databasePath)
    {
        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
        }.ToString();
    }

    public string DatabasePath { get; }

    /// <summary>
    /// Closes this database's pooled connections, so its file can be moved or deleted. Only this file's: others (tests
    /// running alongside) keep theirs.
    /// </summary>
    public void ReleaseConnections() => SqliteConnection.ClearPool(new SqliteConnection(_connectionString));

    /// <summary>
    /// The KSP save (folder name) being planned for. Custom locations, missions, flights and constellations belong to
    /// one save: only that save's rows (and shared ones, with no save) are read, and new rows are added to it.
    /// Null when no save is chosen: only shared rows are read, and new rows are shared.
    /// </summary>
    public string? ActiveSave { get; set; }

    /// <summary>SQL condition: the row (or alias.row) belongs to <see cref="ActiveSave"/> or is shared. Needs <see cref="AddSave"/>.</summary>
    internal static string InActiveSave(string alias = "") =>
        $"({alias}ksp_save IS NULL OR {alias}ksp_save = $active_save)";

    /// <summary>Binds $active_save for <see cref="InActiveSave"/> and inserts.</summary>
    internal void AddSave(SqliteCommand cmd) => cmd.Parameters.AddWithValue("$active_save", (object?)ActiveSave ?? DBNull.Value);

    /// <summary>Default location: %LOCALAPPDATA%\BrickRoadSoftware\KspFlightPlanner\kspflightplanner.db</summary>
    public static string DefaultPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BrickRoadSoftware", "KspFlightPlanner");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "kspflightplanner.db");
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Pre-release only: when the schema or seed data has changed since the database was created, the
    /// database is rebuilt from scratch and the user's data is carried over (see <see cref="UserTables"/>).
    /// Set to false once releases need real migrations; append scripts to <see cref="Migrations"/> from then on.
    /// </summary>
    public static readonly bool ResetOnSchemaChange = true;

    /// <summary>
    /// Tables holding user data, copied into a rebuilt database. Columns are matched by name; rows that
    /// no longer fit (e.g. a body that disappeared) are skipped.
    /// </summary>
    private static readonly string[] UserTables =
    [
        "custom_locations",
        "custom_location_images",
        "dv_map_overrides",
        "saved_missions",
        "saved_constellations",
        "flights",
        "tech_plans",
        "app_settings",
        // Read from the KSP install rather than typed in, but kept so a rebuild doesn't need KSP to be there.
        "ksp_resources",
        "ksp_parts",
        "ksp_part_resources",
        "ksp_part_engines",
        "ksp_engine_propellants",
        "ksp_part_antennas",
        "ksp_part_power",
        "ksp_tech_nodes",
        "ksp_tech_parents",
        // Sampled from the running game; baking again needs KSP and takes minutes.
        "ksp_surface_bakes",
        "ksp_surface_bake_air",
    ];

    public void Initialize()
    {
        var fingerprint = Migrations.Fingerprint + BodySeeder.Fingerprint + DvMapSeeder.Fingerprint;
        if (ResetOnSchemaChange && File.Exists(DatabasePath) && (ReadFingerprint() != fingerprint || DiffersFromSchema()))
        {
            RebuildKeepingUserData(fingerprint);
            return;
        }

        try
        {
            Build(fingerprint);
        }
        catch (SqliteException) when (ResetOnSchemaChange)
        {
            // Pre-release safety net: an unexpected/partial database is rebuilt rather than blocking startup.
            RebuildKeepingUserData(fingerprint);
        }
    }

    /// <summary>Moves the old file aside, builds a fresh database, copies user tables across, then deletes the old file.</summary>
    private void RebuildKeepingUserData(string fingerprint)
    {
        var oldPath = DatabasePath + ".old";
        ReleaseConnections();
        File.Delete(oldPath);
        File.Move(DatabasePath, oldPath);

        Build(fingerprint);
        try
        {
            using var connection = Open();
            using (var attach = connection.CreateCommand())
            {
                attach.CommandText = "ATTACH DATABASE $path AS old;";
                attach.Parameters.AddWithValue("$path", oldPath);
                attach.ExecuteNonQuery();
            }
            foreach (var table in UserTables) CopyTable(connection, table);
            using var detach = connection.CreateCommand();
            detach.CommandText = "DETACH DATABASE old;";
            detach.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Old file unreadable: keep the fresh database rather than failing to start.
        }
        finally
        {
            ReleaseConnections();
            File.Delete(oldPath);
        }
    }

    private static void CopyTable(SqliteConnection connection, string table)
    {
        var oldColumns = Columns(connection, "old", table);
        if (oldColumns.Count == 0) return; // table didn't exist before
        var newColumns = Columns(connection, "main", table);

        var columns = newColumns.Where(oldColumns.Contains).ToList();
        if (columns.Count == 0) return;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"INSERT OR IGNORE INTO main.{table} ({string.Join(", ", columns)}) " +
                          $"SELECT {string.Join(", ", columns)} FROM old.{table};";
        try { cmd.ExecuteNonQuery(); }
        catch (SqliteException) { /* e.g. a foreign key no longer satisfied; skip this table */ }
    }

    private static HashSet<string> Columns(SqliteConnection connection, string schema, string table)
    {
        using var cmd = connection.CreateCommand();
        // Schema goes in as the function's second argument; "old.pragma_table_info(...)" would read main's table.
        cmd.CommandText = "SELECT name FROM pragma_table_info($table, $schema);";
        cmd.Parameters.AddWithValue("$table", table);
        cmd.Parameters.AddWithValue("$schema", schema);
        using var reader = cmd.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) columns.Add(reader.GetString(0));
        return columns;
    }

    private void Build(string fingerprint)
    {
        using var connection = Open();
        Migrations.Apply(connection);
        BodySeeder.SeedIfEmpty(connection);
        DvMapSeeder.SeedIfEmpty(connection);
        WriteFingerprint(connection, fingerprint);
    }

    private void DeleteDatabase()
    {
        ReleaseConnections();
        File.Delete(DatabasePath);
    }

    /// <summary>
    /// Pre-release safety net: the fingerprint says current, but a table or column the scripts create is missing
    /// (seen after an app instance was killed mid-start). Compared against the scripts run on an in-memory database.
    /// </summary>
    private bool DiffersFromSchema()
    {
        try
        {
            using var expected = new SqliteConnection("Data Source=:memory:");
            expected.Open();
            Migrations.Apply(expected);
            using var actual = Open();
            return !Columns(expected).SetEquals(Columns(actual));
        }
        catch (SqliteException)
        {
            return true;
        }
    }

    /// <summary>Every "table.column" in the database's main schema.</summary>
    private static HashSet<string> Columns(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT m.name || '.' || c.name FROM sqlite_master m JOIN pragma_table_info(m.name) c
            WHERE m.type = 'table' AND m.name NOT LIKE 'sqlite_%';
            """;
        using var reader = cmd.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) columns.Add(reader.GetString(0));
        return columns;
    }

    private string? ReadFingerprint()
    {
        try
        {
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM app_meta WHERE key = 'fingerprint';";
            return cmd.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            return null; // older database without app_meta
        }
    }

    private static void WriteFingerprint(SqliteConnection connection, string fingerprint)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO app_meta (key, value) VALUES ('fingerprint', $v) ON CONFLICT(key) DO UPDATE SET value = $v;";
        cmd.Parameters.AddWithValue("$v", fingerprint);
        cmd.ExecuteNonQuery();
    }
}
