using System.Globalization;
using System.Text.Json;
using KspFlightPlanner.Core;

namespace KspFlightPlanner.Data;

public sealed record SavedMission(long Id, string Name, MissionDefinition Definition, DateTime CreatedUtc)
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

        return new DeltaVMap(nodes.Select(n =>
            !n.IsCustom && overrides.TryGetValue(n.Key, out var dv) ? n with { DvFromParent = dv } : n));
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

    public IReadOnlyList<SavedMission> GetAll()
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, name, definition_json, created_utc FROM saved_missions ORDER BY created_utc DESC;";
        using var reader = cmd.ExecuteReader();
        var list = new List<SavedMission>();
        while (reader.Read())
        {
            MissionDefinition? definition;
            try { definition = JsonSerializer.Deserialize<MissionDefinition>(reader.GetString(2), Json); }
            catch (JsonException) { continue; }
            if (definition?.Stops is not { Count: >= 2 }) continue; // unreadable or pre-stops format
            list.Add(new SavedMission(reader.GetInt64(0), reader.GetString(1), definition,
                DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }
        return list;
    }

    public long Add(string name, MissionDefinition definition)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO saved_missions (name, definition_json, created_utc)
            VALUES ($name, $json, $created) RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(definition, Json));
        cmd.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void Delete(long id)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM saved_missions WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }
}
