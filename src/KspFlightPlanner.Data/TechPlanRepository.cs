namespace KspFlightPlanner.Data;

/// <summary>The tech nodes the player plans to research next, kept per KSP save (<see cref="PlannerDatabase.ActiveSave"/>).</summary>
public sealed class TechPlanRepository(PlannerDatabase database)
{
    public IReadOnlySet<string> Load()
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT node_id FROM tech_plans WHERE ksp_save = $save;";
        cmd.Parameters.AddWithValue("$save", database.ActiveSave ?? "");
        using var reader = cmd.ExecuteReader();
        var planned = new HashSet<string>();
        while (reader.Read()) planned.Add(reader.GetString(0));
        return planned;
    }

    /// <summary>Replaces the active save's plan.</summary>
    public void Save(IEnumerable<string> planned)
    {
        using var connection = database.Open();
        using var tx = connection.BeginTransaction();
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM tech_plans WHERE ksp_save = $save;";
            delete.Parameters.AddWithValue("$save", database.ActiveSave ?? "");
            delete.ExecuteNonQuery();
        }
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = "INSERT OR IGNORE INTO tech_plans VALUES ($save, $node);";
            var node = insert.Parameters.Add("$node", Microsoft.Data.Sqlite.SqliteType.Text);
            insert.Parameters.AddWithValue("$save", database.ActiveSave ?? "");
            foreach (var id in planned)
            {
                node.Value = id;
                insert.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }
}
