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

    public void Initialize()
    {
        using var connection = Open();
        Migrations.Apply(connection);
        BodySeeder.SeedIfEmpty(connection);
    }
}
