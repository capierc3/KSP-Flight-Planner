using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using KspFlightPlanner.Core;

namespace KspFlightPlanner.Data;

/// <summary>Flights handed to the Flight tab, kept until the player deletes them (a mission can span many sessions).</summary>
public sealed class FlightRepository(PlannerDatabase database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Every flight, newest first. Rows that can't be read (an older format) are skipped.</summary>
    public IReadOnlyList<Flight> GetAll()
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, name, flight_json FROM flights ORDER BY created_utc DESC, id DESC;";
        using var reader = cmd.ExecuteReader();
        var list = new List<Flight>();
        while (reader.Read())
        {
            Flight? flight;
            try { flight = JsonSerializer.Deserialize<Flight>(reader.GetString(2), Json); }
            catch (JsonException) { continue; }
            if (flight is not null) list.Add(flight with { Id = reader.GetInt64(0), Name = reader.GetString(1) });
        }
        return list;
    }

    public long Add(Flight flight)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO flights (name, flight_json, created_utc)
            VALUES ($name, $json, $created) RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$name", flight.Name);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(flight with { Id = 0 }, Json));
        cmd.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>Stores a re-planned or renamed flight.</summary>
    public void Update(Flight flight)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE flights SET name = $name, flight_json = $json WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", flight.Id);
        cmd.Parameters.AddWithValue("$name", flight.Name);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(flight with { Id = 0 }, Json));
        cmd.ExecuteNonQuery();
    }

    public void Delete(long id)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM flights WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }
}
