using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace KspFlightPlanner.Data;

/// <summary>Loads the community Δv map from the embedded Seed/dvmap.json (source noted in the file).</summary>
internal static class DvMapSeeder
{
    private const string ResourceName = "KspFlightPlanner.Data.Seed.dvmap.json";

    /// <summary>Hash of the embedded seed file; changes whenever the map data changes.</summary>
    public static string Fingerprint { get; } = Hashing.Sha256(OpenSeed());

    private sealed record SeedFile(string System, List<SeedNode> Nodes);
    private sealed record SeedNode(
        string Key, string? Parent, string Body, string Kind, string Label,
        double Dv, double Plane, bool AeroDown, bool AeroUp, bool Jet, string? FlightTime, double? AltitudeKm);

    public static void SeedIfEmpty(SqliteConnection connection)
    {
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM dv_map_nodes;";
            if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;
        }

        using var stream = OpenSeed();
        var seed = JsonSerializer.Deserialize<SeedFile>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Δv map seed is empty.");

        using var tx = connection.BeginTransaction();
        long systemId;
        using (var sys = connection.CreateCommand())
        {
            sys.Transaction = tx;
            sys.CommandText = "SELECT id FROM star_systems WHERE name = $name;";
            sys.Parameters.AddWithValue("$name", seed.System);
            systemId = Convert.ToInt64(sys.ExecuteScalar()
                ?? throw new InvalidOperationException($"Δv map system '{seed.System}' not found."));
        }

        var ids = new Dictionary<string, long>();
        for (var i = 0; i < seed.Nodes.Count; i++)
        {
            var n = seed.Nodes[i];
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO dv_map_nodes
                    (system_id, key, parent_id, body_id, body_name, kind, label, dv_from_parent, plane_change_dv,
                     aerobrake_down, aerobrake_up, jet_possible, flight_time, altitude_m, sort_order)
                VALUES
                    ($sys, $key, $parent,
                     (SELECT id FROM celestial_bodies WHERE system_id = $sys AND name = $body),
                     $body, $kind, $label, $dv, $plane, $down, $up, $jet, $time, $alt, $order)
                RETURNING id;
                """;
            var p = cmd.Parameters;
            p.AddWithValue("$sys", systemId);
            p.AddWithValue("$key", n.Key);
            p.AddWithValue("$parent", n.Parent is null ? DBNull.Value
                : ids.TryGetValue(n.Parent, out var pid) ? pid
                : throw new InvalidOperationException($"Δv map node '{n.Key}' lists parent '{n.Parent}' before it is defined."));
            p.AddWithValue("$body", n.Body);
            p.AddWithValue("$kind", n.Kind);
            p.AddWithValue("$label", n.Label);
            p.AddWithValue("$dv", n.Dv);
            p.AddWithValue("$plane", n.Plane);
            p.AddWithValue("$down", n.AeroDown ? 1 : 0);
            p.AddWithValue("$up", n.AeroUp ? 1 : 0);
            p.AddWithValue("$jet", n.Jet ? 1 : 0);
            p.AddWithValue("$time", (object?)n.FlightTime ?? DBNull.Value);
            p.AddWithValue("$alt", n.AltitudeKm is { } km ? km * 1000 : (object)DBNull.Value);
            p.AddWithValue("$order", i);
            ids[n.Key] = Convert.ToInt64(cmd.ExecuteScalar());
        }

        tx.Commit();
    }

    private static Stream OpenSeed() =>
        typeof(DvMapSeeder).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
}
