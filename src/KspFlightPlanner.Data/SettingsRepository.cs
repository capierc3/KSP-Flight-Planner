namespace KspFlightPlanner.Data;

/// <summary>The user's preferences. Add new options here (with a key in <see cref="SettingsRepository"/>).</summary>
public sealed record AppSettings
{
    /// <summary>Folder Kerbal Space Program is installed in (contains KSP_x64.exe and saves\).</summary>
    public string? KspInstallPath { get; init; }

    /// <summary>Save folder name under saves\ to read vessels from.</summary>
    public string? KspSaveName { get; init; }

    /// <summary>Re-read the save whenever KSP writes it and update linked locations.</summary>
    public bool WatchSave { get; init; } = true;
}

/// <summary>Stores <see cref="AppSettings"/> in the app_settings table as key/value text.</summary>
public sealed class SettingsRepository(PlannerDatabase database)
{
    private const string KspInstallPathKey = "ksp.install_path";
    private const string KspSaveNameKey = "ksp.save_name";
    private const string WatchSaveKey = "ksp.watch_save";

    public AppSettings Load()
    {
        var values = ReadAll();
        return new AppSettings
        {
            KspInstallPath = values.GetValueOrDefault(KspInstallPathKey),
            KspSaveName = values.GetValueOrDefault(KspSaveNameKey),
            WatchSave = values.GetValueOrDefault(WatchSaveKey) is not { } watch || watch == "1",
        };
    }

    public void Save(AppSettings settings)
    {
        Write(KspInstallPathKey, settings.KspInstallPath);
        Write(KspSaveNameKey, settings.KspSaveName);
        Write(WatchSaveKey, settings.WatchSave ? "1" : "0");
    }

    private Dictionary<string, string> ReadAll()
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM app_settings;";
        using var reader = cmd.ExecuteReader();
        var values = new Dictionary<string, string>();
        while (reader.Read()) values[reader.GetString(0)] = reader.GetString(1);
        return values;
    }

    /// <summary>Stores a value; null or blank removes it.</summary>
    private void Write(string key, string? value)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = string.IsNullOrWhiteSpace(value)
            ? "DELETE FROM app_settings WHERE key = $key;"
            : "INSERT INTO app_settings (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = $value;";
        cmd.Parameters.AddWithValue("$key", key);
        if (!string.IsNullOrWhiteSpace(value)) cmd.Parameters.AddWithValue("$value", value.Trim());
        cmd.ExecuteNonQuery();
    }
}
