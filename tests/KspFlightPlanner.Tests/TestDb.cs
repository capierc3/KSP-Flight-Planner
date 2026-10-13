using KspFlightPlanner.Data;
using Microsoft.Data.Sqlite;

namespace KspFlightPlanner.Tests;

/// <summary>Test database files.</summary>
internal static class TestDb
{
    /// <summary>
    /// Closes the pooled connections to one test database file so it can be deleted or rebuilt: the database's own and a
    /// test's raw "Data Source=…" ones. Only that file's: clearing every pool would close connections that tests running
    /// alongside are still using.
    /// </summary>
    public static void Release(string path)
    {
        new PlannerDatabase(path).ReleaseConnections();
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={path}"));
    }
}
