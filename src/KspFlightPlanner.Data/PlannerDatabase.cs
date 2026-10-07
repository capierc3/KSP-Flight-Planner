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
    /// Pre-release only: when the schema or seed data has changed since the database was created,
    /// delete it and rebuild from scratch (no user data needs preserving yet).
    /// Set to false once releases must keep user data; use appended migrations from then on.
    /// </summary>
    public static readonly bool ResetOnSchemaChange = true;

    public void Initialize()
    {
        var fingerprint = Migrations.Fingerprint + BodySeeder.Fingerprint + DvMapSeeder.Fingerprint;
        if (ResetOnSchemaChange && File.Exists(DatabasePath) && ReadFingerprint() != fingerprint)
            DeleteDatabase();

        try
        {
            Build(fingerprint);
        }
        catch (SqliteException) when (ResetOnSchemaChange)
        {
            // Pre-release safety net: an unexpected/partial database is rebuilt rather than blocking startup.
            DeleteDatabase();
            Build(fingerprint);
        }
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
        SqliteConnection.ClearAllPools();
        File.Delete(DatabasePath);
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
