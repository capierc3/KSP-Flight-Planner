using System.Globalization;

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

    /// <summary>Where the kRPC mod's server listens: KSP's machine (its default, 127.0.0.1) or another PC on the network.</summary>
    public string KrpcAddress { get; init; } = DefaultKrpcAddress;

    /// <summary>kRPC's RPC port (requests and replies).</summary>
    public int KrpcRpcPort { get; init; } = DefaultKrpcRpcPort;

    /// <summary>kRPC's stream port (values the server pushes continuously).</summary>
    public int KrpcStreamPort { get; init; } = DefaultKrpcStreamPort;

    /// <summary>Try to connect to KSP once when the planner starts.</summary>
    public bool KrpcConnectOnStartup { get; init; }

    /// <summary>How often the live game's time, scene and the flown craft's nodes are read, in seconds.</summary>
    public double KrpcPollSeconds { get; init; } = DefaultKrpcPollSeconds;

    /// <summary>How often every vessel's orbit is read while in flight, in seconds (the slow read: it grows with the vessel count).</summary>
    public double KrpcVesselsSeconds { get; init; } = DefaultKrpcVesselsSeconds;

    // kRPC's stock server settings.
    public const string DefaultKrpcAddress = "127.0.0.1";
    public const int DefaultKrpcRpcPort = 50000;
    public const int DefaultKrpcStreamPort = 50001;

    public const double DefaultKrpcPollSeconds = 2;
    public const double DefaultKrpcVesselsSeconds = 10;
    public const double MinKrpcPollSeconds = 0.5, MaxKrpcPollSeconds = 60;
    public const double MinKrpcVesselsSeconds = 1, MaxKrpcVesselsSeconds = 300;
}

/// <summary>Stores <see cref="AppSettings"/> in the app_settings table as key/value text.</summary>
public sealed class SettingsRepository(PlannerDatabase database)
{
    private const string KspInstallPathKey = "ksp.install_path";
    private const string KspSaveNameKey = "ksp.save_name";
    private const string WatchSaveKey = "ksp.watch_save";
    private const string KrpcAddressKey = "krpc.address";
    private const string KrpcRpcPortKey = "krpc.rpc_port";
    private const string KrpcStreamPortKey = "krpc.stream_port";
    private const string KrpcConnectOnStartupKey = "krpc.connect_on_startup";
    private const string KrpcPollSecondsKey = "krpc.poll_seconds";
    private const string KrpcVesselsSecondsKey = "krpc.vessels_seconds";
    private const string LastMissionKey = "app.last_mission_id";

    public AppSettings Load()
    {
        var values = ReadAll();
        return new AppSettings
        {
            KspInstallPath = values.GetValueOrDefault(KspInstallPathKey),
            KspSaveName = values.GetValueOrDefault(KspSaveNameKey),
            WatchSave = values.GetValueOrDefault(WatchSaveKey) is not { } watch || watch == "1",
            KrpcAddress = values.GetValueOrDefault(KrpcAddressKey) ?? AppSettings.DefaultKrpcAddress,
            KrpcRpcPort = Port(values.GetValueOrDefault(KrpcRpcPortKey)) ?? AppSettings.DefaultKrpcRpcPort,
            KrpcStreamPort = Port(values.GetValueOrDefault(KrpcStreamPortKey)) ?? AppSettings.DefaultKrpcStreamPort,
            KrpcConnectOnStartup = values.GetValueOrDefault(KrpcConnectOnStartupKey) == "1",
            KrpcPollSeconds = Seconds(values.GetValueOrDefault(KrpcPollSecondsKey), AppSettings.MinKrpcPollSeconds, AppSettings.MaxKrpcPollSeconds)
                              ?? AppSettings.DefaultKrpcPollSeconds,
            KrpcVesselsSeconds = Seconds(values.GetValueOrDefault(KrpcVesselsSecondsKey), AppSettings.MinKrpcVesselsSeconds, AppSettings.MaxKrpcVesselsSeconds)
                                 ?? AppSettings.DefaultKrpcVesselsSeconds,
        };
    }

    public void Save(AppSettings settings)
    {
        Write(KspInstallPathKey, settings.KspInstallPath);
        Write(KspSaveNameKey, settings.KspSaveName);
        Write(WatchSaveKey, settings.WatchSave ? "1" : "0");
        Write(KrpcAddressKey, settings.KrpcAddress);
        Write(KrpcRpcPortKey, settings.KrpcRpcPort.ToString(CultureInfo.InvariantCulture));
        Write(KrpcStreamPortKey, settings.KrpcStreamPort.ToString(CultureInfo.InvariantCulture));
        Write(KrpcConnectOnStartupKey, settings.KrpcConnectOnStartup ? "1" : "0");
        Write(KrpcPollSecondsKey, settings.KrpcPollSeconds.ToString(CultureInfo.InvariantCulture));
        Write(KrpcVesselsSecondsKey, settings.KrpcVesselsSeconds.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The mission open when the app last closed (or left this save), reopened at the next start or when switching
    /// back. One per save (<see cref="PlannerDatabase.ActiveSave"/>). Kept apart from <see cref="AppSettings"/> because
    /// it isn't a preference the Settings window edits.
    /// </summary>
    public long? LoadLastMissionId() =>
        long.TryParse(ReadAll().GetValueOrDefault(LastMissionKeyForSave), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    public void SaveLastMissionId(long? id) => Write(LastMissionKeyForSave, id?.ToString(CultureInfo.InvariantCulture));

    private string LastMissionKeyForSave => database.ActiveSave is { } save ? $"{LastMissionKey}.{save}" : LastMissionKey;

    /// <summary>A stored port number, or null if it's missing or out of range.</summary>
    private static int? Port(string? text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535 ? port : null;

    /// <summary>A stored number of seconds, or null if it's missing or outside min–max.</summary>
    private static double? Seconds(string? text, double min, double max) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s >= min && s <= max ? s : null;

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
