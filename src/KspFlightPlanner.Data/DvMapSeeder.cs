using System.Text.Json;
using KspFlightPlanner.Core;
using Microsoft.Data.Sqlite;

namespace KspFlightPlanner.Data;

/// <summary>Stores the Δv map that <see cref="DeltaVMapGenerator"/> builds from the stock bodies in Seed/bodies.json.</summary>
internal static class DvMapSeeder
{
    private const string SystemName = BodyRepository.StockSystemName;

    private static IReadOnlyList<DvMapNodeSpec> Nodes { get; } =
        DeltaVMapGenerator.Generate(BodySeeder.LoadBodies(SystemName), LaunchSite.Ksc.BodyName);

    /// <summary>Hash of the generated map; changes whenever the map data (or the math behind it) changes.</summary>
    public static string Fingerprint { get; } = Hashing.Sha256(JsonSerializer.Serialize(Nodes));

    public static void SeedIfEmpty(SqliteConnection connection)
    {
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM dv_map_nodes;";
            if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;
        }

        using var tx = connection.BeginTransaction();
        long systemId;
        using (var sys = connection.CreateCommand())
        {
            sys.Transaction = tx;
            sys.CommandText = "SELECT id FROM star_systems WHERE name = $name;";
            sys.Parameters.AddWithValue("$name", SystemName);
            systemId = Convert.ToInt64(sys.ExecuteScalar()
                ?? throw new InvalidOperationException($"Δv map system '{SystemName}' not found."));
        }

        var ids = new Dictionary<string, long>();
        for (var i = 0; i < Nodes.Count; i++)
        {
            var n = Nodes[i];
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
            p.AddWithValue("$parent", n.ParentKey is null ? DBNull.Value
                : ids.TryGetValue(n.ParentKey, out var pid) ? pid
                : throw new InvalidOperationException($"Δv map node '{n.Key}' lists parent '{n.ParentKey}' before it is defined."));
            p.AddWithValue("$body", n.BodyName);
            p.AddWithValue("$kind", n.Kind);
            p.AddWithValue("$label", n.Label);
            p.AddWithValue("$dv", n.Dv);
            p.AddWithValue("$plane", n.PlaneChangeDv);
            p.AddWithValue("$down", n.AerobrakeDown ? 1 : 0);
            p.AddWithValue("$up", n.AerobrakeUp ? 1 : 0);
            p.AddWithValue("$jet", n.JetEnginesPossible ? 1 : 0);
            p.AddWithValue("$time", (object?)n.FlightTime ?? DBNull.Value);
            p.AddWithValue("$alt", (object?)n.AltitudeM ?? DBNull.Value);
            p.AddWithValue("$order", i);
            ids[n.Key] = Convert.ToInt64(cmd.ExecuteScalar());
        }

        tx.Commit();
    }
}
