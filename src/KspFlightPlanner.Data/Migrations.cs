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
        // v1: the whole schema (pre-release, so edited in place)
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
            molar_mass_g_mol         REAL,
            adiabatic_index          REAL,
            equator_temp_night_c     REAL,              -- sea level
            equator_temp_day_c       REAL,
            pole_temp_low_c          REAL,
            pole_temp_high_c         REAL,

            -- science multipliers (KSP wiki); NULL = situation doesn't apply
            sci_landed               REAL,
            sci_splashed             REAL,
            sci_flying_low           REAL,
            sci_flying_high          REAL,
            sci_space_low            REAL,
            sci_space_high           REAL,
            sci_recovery             REAL,
            sci_flying_high_from_m   REAL,              -- altitude where "flying high" begins
            sci_space_high_from_m    REAL,              -- altitude where "in space high" begins

            UNIQUE (system_id, name)
        );

        -- Pressure-vs-altitude table (KSP wiki), used instead of the scale height when present.
        CREATE TABLE body_pressure_points (
            body_id          INTEGER NOT NULL REFERENCES celestial_bodies(id) ON DELETE CASCADE,
            altitude_m       REAL    NOT NULL,
            pressure_pa      REAL    NOT NULL,
            density_kg_m3    REAL,
            PRIMARY KEY (body_id, altitude_m)
        );

        CREATE TABLE body_biomes (
            body_id          INTEGER NOT NULL REFERENCES celestial_bodies(id) ON DELETE CASCADE,
            sort_order       INTEGER NOT NULL,
            name             TEXT    NOT NULL,
            PRIMARY KEY (body_id, sort_order)
        );

        -- Named surface spots (launch sites, anomalies, highest point); latitude north / longitude east positive.
        CREATE TABLE body_landmarks (
            id               INTEGER PRIMARY KEY,
            body_id          INTEGER NOT NULL REFERENCES celestial_bodies(id) ON DELETE CASCADE,
            sort_order       INTEGER NOT NULL,
            name             TEXT    NOT NULL,
            kind             TEXT    NOT NULL,      -- launch-site / anomaly / feature
            latitude_deg     REAL    NOT NULL,
            longitude_deg    REAL    NOT NULL,
            note             TEXT,
            source           TEXT
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
            -- The KSP save (folder name) it belongs to; NULL = shared by every save.
            ksp_save              TEXT,
            -- Imported from that save: the orbit follows the vessel (its pid). NULL = not linked.
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
            fuel_crossfeed INTEGER NOT NULL DEFAULT 1,   -- 0: fuel can't pass through (decouplers)
            tech_required  TEXT,                         -- the tech node that unlocks it (ksp_tech_nodes.id)
            description    TEXT,
            bulkhead_profiles TEXT,                      -- sizes it attaches at, comma-separated (size1,srf)
            crash_tolerance REAL NOT NULL DEFAULT 0,     -- m/s
            max_temp       REAL NOT NULL DEFAULT 0       -- K
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

        CREATE TABLE ksp_part_power (          -- electric charge a part makes or uses, one row per module
            part_name     TEXT    NOT NULL REFERENCES ksp_parts(name) ON DELETE CASCADE,
            module_index  INTEGER NOT NULL,
            kind          TEXT    NOT NULL,   -- Solar / Generator / FuelCell / Command / ReactionWheel / Antenna / Light / Lab / Converter / Drill
            ec_per_s      REAL    NOT NULL,
            tracking      INTEGER NOT NULL DEFAULT 1,   -- solar panels that turn to follow the Sun
            inputs        TEXT,               -- fuel cells: what they burn, units/s ("LiquidFuel=0.0016875;Oxidizer=0.0020625")
            PRIMARY KEY (part_name, module_index)
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

        -- The R&D tech tree of the install (RDNode), read with the parts.
        CREATE TABLE ksp_tech_nodes (
            id             TEXT PRIMARY KEY,
            sort_order     INTEGER NOT NULL,   -- as GameData lists them
            title          TEXT NOT NULL,
            description    TEXT,
            cost           REAL NOT NULL,      -- science
            x              REAL NOT NULL,      -- position in the R&D building, y up
            y              REAL NOT NULL,
            any_to_unlock  INTEGER NOT NULL    -- 1: one researched parent is enough
        );

        CREATE TABLE ksp_tech_parents (
            node_id    TEXT NOT NULL REFERENCES ksp_tech_nodes(id) ON DELETE CASCADE,
            parent_id  TEXT NOT NULL,
            PRIMARY KEY (node_id, parent_id)
        );

        -- Tech nodes the player plans to research next, per KSP save ('' when no save is chosen).
        CREATE TABLE tech_plans (
            ksp_save  TEXT NOT NULL,
            node_id   TEXT NOT NULL,
            PRIMARY KEY (ksp_save, node_id)
        );

        -- Surface data sampled from the running game over kRPC ("baked"), per body of the user's install.
        -- heights: float32 little-endian, row-major from 90° S / 180° W; biomes: one byte per point indexing biome_names.
        CREATE TABLE ksp_surface_bakes (
            body_name             TEXT PRIMARY KEY,
            step_deg              REAL NOT NULL,
            initial_rotation_deg  REAL NOT NULL,     -- rotation angle at UT 0
            rotation_period_s     REAL NOT NULL,
            radius_m              REAL NOT NULL,
            mu                    REAL NOT NULL,
            baked_utc             TEXT NOT NULL,
            heights               BLOB NOT NULL,
            biomes                BLOB NOT NULL,
            biome_names           TEXT NOT NULL      -- JSON array
        );

        CREATE TABLE ksp_surface_bake_air (
            body_name      TEXT NOT NULL REFERENCES ksp_surface_bakes(body_name) ON DELETE CASCADE,
            altitude_m     REAL NOT NULL,
            pressure_pa    REAL NOT NULL,
            density_kg_m3  REAL,
            PRIMARY KEY (body_name, altitude_m)
        );

        CREATE TABLE saved_missions (
            id               INTEGER PRIMARY KEY,
            name             TEXT    NOT NULL,
            definition_json  TEXT    NOT NULL,   -- MissionDefinition (stops as node keys, phases, leg choices, options, notes)
            ksp_save         TEXT,               -- the KSP save it belongs to; NULL = shared by every save
            created_utc      TEXT    NOT NULL,
            updated_utc      TEXT    NOT NULL    -- last saved; the Missions list shows the most recent first
        );

        CREATE TABLE flights (
            id           INTEGER PRIMARY KEY,
            name         TEXT    NOT NULL,
            flight_json  TEXT    NOT NULL,   -- Flight (burns, start orbit, the plan's inputs)
            ksp_save     TEXT,               -- the KSP save it belongs to; NULL = shared by every save
            created_utc  TEXT    NOT NULL
        );

        CREATE TABLE flight_logs (
            id              INTEGER PRIMARY KEY,
            vessel_name     TEXT    NOT NULL,   -- the rocket: the vessel's name at launch
            launch_ut       REAL    NOT NULL,   -- game time − MET; with the name, tells this flight from others of the rocket
            joined_at_met   REAL    NOT NULL DEFAULT 0,   -- 0 = recorded from the launch
            outcome         TEXT    NOT NULL,   -- FlightLogOutcome
            flight_id       INTEGER,
            launch_parts    INTEGER,            -- parts on the pad: with the launch mass, tells a changed design            -- the planner flight being flown, if any (not a foreign key: flights get deleted)
            last_ut         REAL    NOT NULL,
            delta_v_used    REAL    NOT NULL,   -- the samples' sums, kept for the list
            max_altitude_m  REAL    NOT NULL,
            launch_mass_kg  REAL    NOT NULL DEFAULT 0,
            last_situation  TEXT,
            last_body       TEXT,
            route           TEXT,               -- bodies flown around in order, "Kerbin › Mun › Kerbin"
            events_json     TEXT    NOT NULL,   -- FlightLogEvent list
            samples         BLOB    NOT NULL,   -- FlightSample list as Brotli-compressed JSON
            ksp_save        TEXT,               -- the KSP save it belongs to; NULL = shared by every save
            created_utc     TEXT    NOT NULL,
            updated_utc     TEXT    NOT NULL
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
            ksp_save           TEXT,               -- the KSP save it belongs to; NULL = shared by every save
            created_utc        TEXT    NOT NULL
        );
        """,
    ];

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
