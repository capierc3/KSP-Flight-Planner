using System.Globalization;
using System.Text.Json;
using KspFlightPlanner.Core;

namespace KspFlightPlanner.Data;

public sealed record SavedMission(long Id, string Name, MissionDefinition Definition, DateTime CreatedUtc, DateTime UpdatedUtc)
{
    public override string ToString() => Name;
}

public sealed class DvMapRepository(PlannerDatabase database)
{
    /// <summary>
    /// The Δv map for a system with the user's custom locations attached and edits applied,
    /// or null if the system has none.
    /// </summary>
    public DeltaVMap? GetMap(string systemName = "KSP Stock")
    {
        var nodes = ReadSeededNodes(systemName);
        if (nodes.Count == 0) return null;

        var overrides = ReadOverrides();
        var bodies = BodiesByName(systemName);
        var seeded = new DeltaVMap(nodes);
        foreach (var location in new CustomLocationRepository(database).GetAll())
        {
            try
            {
                var node = CustomLocations.Place(location, seeded, bodies);
                nodes.Add(overrides.TryGetValue(node.Key, out var dv) ? node with { DvFromParent = dv } : node);
            }
            catch (ArgumentException)
            {
                // Invalid for this body (e.g. inside the atmosphere); left off the map.
            }
        }

        var byId = nodes.ToDictionary(n => n.Id);
        return new DeltaVMap(nodes.Select(n =>
        {
            var edited = !n.IsCustom && overrides.TryGetValue(n.Key, out var dv) ? n with { DvFromParent = dv } : n;
            return edited with
            {
                OrbitRadiusM = OrbitRadius(n, byId, bodies),
                BodyOrbitRadiusM = bodies.TryGetValue(n.BodyName, out var b) ? b.Orbit?.SemiMajorAxisM : null,
            };
        }));
    }

    /// <summary>
    /// How far a stop is from the centre of its parent node's body, so the map can be drawn nearest-first.
    /// Same body as the parent (low orbits, stations, escape node): radius from that body. Another body
    /// (a moon or planet intercept): that body's orbit. The star itself: 0, so it sorts first.
    /// </summary>
    private static double? OrbitRadius(DvMapNode node, Dictionary<long, DvMapNode> byId, Dictionary<string, Core.Models.CelestialBody> bodies)
    {
        if (node.ParentId is not { } pid || !bodies.TryGetValue(node.BodyName, out var body)) return null;
        var parent = byId[pid];
        if (node.BodyName == parent.BodyName)
            return node.Kind == "Elliptical" ? body.SoiRadiusM : body.EquatorialRadiusM + (node.AltitudeM ?? 0);
        if (node.IsCustom) return body.EquatorialRadiusM + (node.AltitudeM ?? 0); // orbit around the star
        return body.Orbit?.SemiMajorAxisM ?? 0;
    }

    /// <summary>Bodies that can host a custom location (have a low orbit on the map), with their physical data.</summary>
    public IReadOnlyList<CelestialBodyOption> GetCustomLocationBodies(string systemName = "KSP Stock")
    {
        var nodes = ReadSeededNodes(systemName);
        var bodies = BodiesByName(systemName);
        return nodes.Where(n => n.Kind == "LowOrbit" && bodies.ContainsKey(n.BodyName))
            .Select(n => new CelestialBodyOption(bodies[n.BodyName], n))
            .ToList();
    }

    private List<DvMapNode> ReadSeededNodes(string systemName)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT n.id, n.key, n.parent_id, n.body_name, n.kind, n.label, n.dv_from_parent, n.plane_change_dv,
                   n.aerobrake_down, n.aerobrake_up, n.jet_possible, n.flight_time, n.altitude_m
            FROM dv_map_nodes n
            JOIN star_systems s ON s.id = n.system_id
            WHERE s.name = $system
            ORDER BY n.sort_order;
            """;
        cmd.Parameters.AddWithValue("$system", systemName);
        using var reader = cmd.ExecuteReader();
        var nodes = new List<DvMapNode>();
        while (reader.Read())
        {
            var mapDv = reader.GetDouble(6);
            nodes.Add(new DvMapNode
            {
                Id = reader.GetInt64(0),
                Key = reader.GetString(1),
                ParentId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                BodyName = reader.GetString(3),
                Kind = reader.GetString(4),
                Label = reader.GetString(5),
                MapDvFromParent = mapDv,
                DvFromParent = mapDv,
                PlaneChangeDv = reader.GetDouble(7),
                AerobrakeDown = reader.GetInt64(8) != 0,
                AerobrakeUp = reader.GetInt64(9) != 0,
                JetEnginesPossible = reader.GetInt64(10) != 0,
                FlightTime = reader.IsDBNull(11) ? null : reader.GetString(11),
                AltitudeM = reader.IsDBNull(12) ? null : reader.GetDouble(12),
            });
        }
        return nodes;
    }

    private Dictionary<string, double> ReadOverrides()
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT node_key, dv_from_parent FROM dv_map_overrides;";
        using var reader = cmd.ExecuteReader();
        var result = new Dictionary<string, double>();
        while (reader.Read()) result[reader.GetString(0)] = reader.GetDouble(1);
        return result;
    }

    private Dictionary<string, Core.Models.CelestialBody> BodiesByName(string systemName)
    {
        var repo = new BodyRepository(database);
        var system = repo.GetSystems().FirstOrDefault(s => s.Name == systemName);
        return system is null ? [] : repo.GetBodies(system.Id).ToDictionary(b => b.Name);
    }

    /// <summary>Replaces the map value of the edge owned by <paramref name="nodeKey"/>; null restores the map value.</summary>
    public void SetOverride(string nodeKey, double? dv)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        if (dv is { } value)
        {
            cmd.CommandText = """
                INSERT INTO dv_map_overrides (node_key, dv_from_parent, updated_utc) VALUES ($key, $dv, $now)
                ON CONFLICT(node_key) DO UPDATE SET dv_from_parent = $dv, updated_utc = $now;
                """;
            cmd.Parameters.AddWithValue("$dv", value);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }
        else
        {
            cmd.CommandText = "DELETE FROM dv_map_overrides WHERE node_key = $key;";
        }
        cmd.Parameters.AddWithValue("$key", nodeKey);
        cmd.ExecuteNonQuery();
    }

    public void ClearOverrides()
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM dv_map_overrides;";
        cmd.ExecuteNonQuery();
    }
}

public sealed class MissionRepository(PlannerDatabase database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Every readable saved mission, most recently saved first.</summary>
    public IReadOnlyList<SavedMission> GetAll() => Read(null);

    public SavedMission? Get(long id) => Read(id).FirstOrDefault();

    public long Add(string name, MissionDefinition definition)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO saved_missions (name, definition_json, created_utc, updated_utc)
            VALUES ($name, $json, $now, $now) RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(definition, Json));
        cmd.Parameters.AddWithValue("$now", Now());
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>Saves over an existing mission. Returns false if it no longer exists (deleted meanwhile).</summary>
    public bool Update(long id, string name, MissionDefinition definition)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE saved_missions SET name = $name, definition_json = $json, updated_utc = $now WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(definition, Json));
        cmd.Parameters.AddWithValue("$now", Now());
        return cmd.ExecuteNonQuery() == 1;
    }

    public void Delete(long id)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM saved_missions WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>The definition as stored, for telling whether a mission has unsaved changes.</summary>
    public static string Serialize(MissionDefinition definition) => JsonSerializer.Serialize(definition, Json);

    private List<SavedMission> Read(long? id)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, name, definition_json, created_utc, updated_utc FROM saved_missions" +
                          (id is null ? "" : " WHERE id = $id") + " ORDER BY updated_utc DESC, id DESC;";
        if (id is { } value) cmd.Parameters.AddWithValue("$id", value);
        using var reader = cmd.ExecuteReader();
        var list = new List<SavedMission>();
        while (reader.Read())
        {
            MissionDefinition? definition;
            try { definition = JsonSerializer.Deserialize<MissionDefinition>(reader.GetString(2), Json); }
            catch (JsonException) { continue; }
            if (definition?.Stops is not { Count: >= 2 }) continue; // unreadable or pre-stops format
            list.Add(new SavedMission(reader.GetInt64(0), reader.GetString(1), definition, Utc(reader.GetString(3)), Utc(reader.GetString(4))));
        }
        return list;
    }

    private static string Now() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static DateTime Utc(string text) => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
