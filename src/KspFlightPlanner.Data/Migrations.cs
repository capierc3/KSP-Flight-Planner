using Microsoft.Data.Sqlite;

namespace KspFlightPlanner.Data;

/// <summary>
/// Ordered schema scripts. Index i upgrades the database from user_version i to i+1.
/// Never edit a shipped script — append a new one.
/// </summary>
internal static class Migrations
{
    private static readonly string[] Scripts =
    [
        // v1: constant body data + first user table
        """
        CREATE TABLE star_systems (
            id          INTEGER PRIMARY KEY,
            name        TEXT    NOT NULL UNIQUE,
            sort_order  INTEGER NOT NULL,
            is_builtin  INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE celestial_bodies (
            id                   INTEGER PRIMARY KEY,
            system_id            INTEGER NOT NULL REFERENCES star_systems(id) ON DELETE CASCADE,
            name                 TEXT    NOT NULL,
            mass_kg              REAL    NOT NULL,
            mu                   REAL    NOT NULL,   -- gravitational parameter GM, m^3/s^2
            equatorial_radius_m  REAL    NOT NULL,
            rotation_period_s    REAL    NOT NULL,   -- sidereal; negative = retrograde
            atmosphere_height_m  REAL    NOT NULL,   -- 0 = no atmosphere
            soi_radius_m         REAL    NOT NULL,   -- from body centre
            color                TEXT    NOT NULL,
            is_default           INTEGER NOT NULL DEFAULT 0,
            sort_order           INTEGER NOT NULL,
            is_builtin           INTEGER NOT NULL DEFAULT 0,
            UNIQUE (system_id, name)
        );

        CREATE TABLE saved_constellations (
            id                 INTEGER PRIMARY KEY,
            name               TEXT    NOT NULL,
            body_id            INTEGER NOT NULL REFERENCES celestial_bodies(id) ON DELETE CASCADE,
            satellite_count    INTEGER NOT NULL,
            altitude_m         REAL    NOT NULL,
            prefer_dive_orbit  INTEGER NOT NULL DEFAULT 0,
            use_occlusion      INTEGER NOT NULL DEFAULT 0,
            notes              TEXT,
            created_utc        TEXT    NOT NULL
        );
        """,
    ];

    public static int LatestVersion => Scripts.Length;

    public static void Apply(SqliteConnection connection)
    {
        var version = GetVersion(connection);
        for (var i = version; i < Scripts.Length; i++)
        {
            using var tx = connection.BeginTransaction();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = Scripts[i] + $"\nPRAGMA user_version = {i + 1};";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public static int GetVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
