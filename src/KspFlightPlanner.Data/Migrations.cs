using Microsoft.Data.Sqlite;

namespace KspFlightPlanner.Data;

/// <summary>
/// Ordered schema scripts. Index i upgrades the database from user_version i to i+1.
/// Pre-release: v1 may still be edited freely (delete your local database to rebuild).
/// Once user data must be preserved, freeze shipped scripts and only append new ones.
/// </summary>
internal static class Migrations
{
    private static readonly string[] Scripts =
    [
        // v1: constant body data + first user table
        """
        CREATE TABLE app_meta (
            key    TEXT PRIMARY KEY,
            value  TEXT NOT NULL
        );

        CREATE TABLE star_systems (
            id          INTEGER PRIMARY KEY,
            name        TEXT    NOT NULL UNIQUE,
            sort_order  INTEGER NOT NULL,
            is_builtin  INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE celestial_bodies (
            id                       INTEGER PRIMARY KEY,
            system_id                INTEGER NOT NULL REFERENCES star_systems(id) ON DELETE CASCADE,
            parent_id                INTEGER REFERENCES celestial_bodies(id),
            name                     TEXT    NOT NULL,
            body_class               TEXT,              -- Star / Planet / Dwarf planet / Moon
            color                    TEXT    NOT NULL,
            is_default               INTEGER NOT NULL DEFAULT 0,
            sort_order               INTEGER NOT NULL,
            is_builtin               INTEGER NOT NULL DEFAULT 0,

            -- physical
            mass_kg                  REAL    NOT NULL,
            mu                       REAL    NOT NULL,  -- gravitational parameter GM, m^3/s^2
            equatorial_radius_m      REAL    NOT NULL,
            rotation_period_s        REAL    NOT NULL,  -- sidereal; negative = retrograde
            soi_radius_m             REAL    NOT NULL,  -- from body centre
            highest_elevation_m      REAL,
            surface_to_orbit_dv      REAL,              -- approximate, community delta-v map

            -- orbit around parent (NULL for the star / unknown)
            sma_m                    REAL,
            eccentricity             REAL,
            inclination_deg          REAL,
            lan_deg                  REAL,              -- longitude of ascending node
            arg_periapsis_deg        REAL,
            mean_anomaly_epoch_rad   REAL,
            epoch_s                  REAL,

            -- atmosphere (atmosphere_height_m NULL = no atmosphere)
            atmosphere_height_m      REAL,
            sea_level_pressure_atm   REAL,
            scale_height_m           REAL,
            min_temp_c               REAL,
            max_temp_c               REAL,
            has_oxygen               INTEGER,

            UNIQUE (system_id, name)
        );

        -- Community delta-v map as a tree rooted at Kerbin's surface; each node stores the edge to its parent.
        CREATE TABLE dv_map_nodes (
            id               INTEGER PRIMARY KEY,
            system_id        INTEGER NOT NULL REFERENCES star_systems(id) ON DELETE CASCADE,
            key              TEXT    NOT NULL,
            parent_id        INTEGER REFERENCES dv_map_nodes(id),
            body_id          INTEGER REFERENCES celestial_bodies(id),
            body_name        TEXT    NOT NULL,
            kind             TEXT    NOT NULL,   -- Surface / LowOrbit / Intercept / Elliptical / Synchronous
            label            TEXT    NOT NULL,
            dv_from_parent   REAL    NOT NULL DEFAULT 0,
            plane_change_dv  REAL    NOT NULL DEFAULT 0,  -- worst case, on the edge to the parent
            aerobrake_down   INTEGER NOT NULL DEFAULT 0,  -- parent -> node
            aerobrake_up     INTEGER NOT NULL DEFAULT 0,  -- node -> parent
            jet_possible     INTEGER NOT NULL DEFAULT 0,
            flight_time      TEXT,
            altitude_m       REAL,               -- low / synchronous orbits
            sort_order       INTEGER NOT NULL,
            UNIQUE (system_id, key)
        );

        -- User edits to map values (e.g. Mun landing 580 -> 700). Keyed by node key so they survive reseeding.
        CREATE TABLE dv_map_overrides (
            node_key         TEXT    PRIMARY KEY,
            dv_from_parent   REAL    NOT NULL,
            updated_utc      TEXT    NOT NULL
        );

        -- User-defined places: orbits such as a space station. Only independent values are stored;
        -- semi-major axis, eccentricity, period, speeds and Δv are derived (CustomLocations.Analyze).
        CREATE TABLE custom_locations (
            id                    INTEGER PRIMARY KEY,
            name                  TEXT    NOT NULL,
            body_name             TEXT    NOT NULL,
            periapsis_altitude_m  REAL    NOT NULL,
            apoapsis_altitude_m   REAL    NOT NULL,   -- equal to periapsis for a circular orbit
            inclination_deg       REAL    NOT NULL DEFAULT 0,
            description           TEXT,
            -- Position (placed locations only). Planned locations have no fixed position yet.
            is_placed             INTEGER NOT NULL DEFAULT 0,
            position_mode         TEXT    NOT NULL DEFAULT 'Elements',   -- Elements | PhaseAngle
            lan_deg               REAL    NOT NULL DEFAULT 0,
            arg_periapsis_deg     REAL    NOT NULL DEFAULT 0,
            mean_anomaly_deg      REAL    NOT NULL DEFAULT 0,   -- at epoch_ut (Elements mode)
            phase_angle_deg       REAL    NOT NULL DEFAULT 0,   -- ahead of Kerbin at epoch_ut (PhaseAngle mode)
            epoch_ut              REAL    NOT NULL DEFAULT 0,   -- seconds since Year 1, Day 1
            -- Imported from a KSP save: the orbit follows the vessel (save folder name + vessel pid). NULL = not linked.
            ksp_save              TEXT,
            ksp_vessel_id         TEXT,
            created_utc           TEXT    NOT NULL,
            updated_utc           TEXT
        );

        CREATE TABLE custom_location_images (
            id           INTEGER PRIMARY KEY,
            location_id  INTEGER NOT NULL REFERENCES custom_locations(id) ON DELETE CASCADE,
            file_name    TEXT    NOT NULL,
            image        BLOB    NOT NULL,
            sort_order   INTEGER NOT NULL DEFAULT 0
        );

        -- User preferences (KSP install folder, chosen save, …) as key/value text.
        CREATE TABLE app_settings (
            key    TEXT PRIMARY KEY,
            value  TEXT NOT NULL
        );

        -- Part data read from the user's KSP install (GameData, after mod patches), refreshed when it changes.
        -- Names use '.' as KSP's saves and craft files do (microEngine.v2).
        CREATE TABLE ksp_resources (
            name          TEXT PRIMARY KEY,
            display_name  TEXT NOT NULL,
            density_t     REAL NOT NULL,     -- tonnes per unit
            unit_cost     REAL NOT NULL DEFAULT 0,
            flow_mode     TEXT NOT NULL DEFAULT 'STACK_PRIORITY_SEARCH'   -- NO_FLOW / STACK_PRIORITY_SEARCH / ALL_VESSEL …
        );

        CREATE TABLE ksp_parts (
            name           TEXT PRIMARY KEY,
            title          TEXT NOT NULL,
            category       TEXT,
            manufacturer   TEXT,
            mass_t         REAL NOT NULL,    -- dry
            cost           REAL NOT NULL DEFAULT 0,
            crew_capacity  INTEGER NOT NULL DEFAULT 0,
            fuel_crossfeed INTEGER NOT NULL DEFAULT 1    -- 0: fuel can't pass through (decouplers)
        );

        CREATE TABLE ksp_part_resources (      -- what a new part holds (full tanks)
            part_name      TEXT NOT NULL REFERENCES ksp_parts(name) ON DELETE CASCADE,
            resource_name  TEXT NOT NULL,
            amount         REAL NOT NULL,
            max_amount     REAL NOT NULL,
            PRIMARY KEY (part_name, resource_name)
        );

        CREATE TABLE ksp_part_engines (        -- one row per engine module (multi-mode engines have several)
            id               INTEGER PRIMARY KEY,
            part_name        TEXT    NOT NULL REFERENCES ksp_parts(name) ON DELETE CASCADE,
            module_index     INTEGER NOT NULL,
            engine_id        TEXT,
            max_thrust_kn    REAL    NOT NULL,
            isp_vacuum_s     REAL    NOT NULL,
            isp_sea_level_s  REAL    NOT NULL,
            UNIQUE (part_name, module_index)
        );

        CREATE TABLE ksp_engine_propellants (
            engine_id      INTEGER NOT NULL REFERENCES ksp_part_engines(id) ON DELETE CASCADE,
            resource_name  TEXT    NOT NULL,
            ratio          REAL    NOT NULL,   -- by volume, e.g. LiquidFuel 0.9 : Oxidizer 1.1
            PRIMARY KEY (engine_id, resource_name)
        );

        CREATE TABLE ksp_part_antennas (
            part_name            TEXT    NOT NULL REFERENCES ksp_parts(name) ON DELETE CASCADE,
            module_index         INTEGER NOT NULL,
            antenna_type         TEXT    NOT NULL,   -- DIRECT / RELAY / INTERNAL
            power                REAL    NOT NULL,   -- range rating in metres
            combinable           INTEGER NOT NULL,
            combinable_exponent  REAL    NOT NULL,
            PRIMARY KEY (part_name, module_index)
        );

        CREATE TABLE saved_missions (
            id               INTEGER PRIMARY KEY,
            name             TEXT    NOT NULL,
            definition_json  TEXT    NOT NULL,   -- MissionDefinition (stops as node keys, phases, leg choices)
            created_utc      TEXT    NOT NULL
        );

        CREATE TABLE flights (
            id           INTEGER PRIMARY KEY,
            name         TEXT    NOT NULL,
            flight_json  TEXT    NOT NULL,   -- Flight (burns, start orbit, the plan's inputs)
            created_utc  TEXT    NOT NULL
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

    /// <summary>Hash of all schema scripts; changes whenever the schema text changes.</summary>
    public static string Fingerprint { get; } = Hashing.Sha256(string.Join("\n", Scripts));

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
