using System.Globalization;
using System.Collections.ObjectModel;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App.ViewModels;

/// <summary>The Settings window. Edits a copy of <see cref="AppSettings"/>; nothing is stored until OK.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private string _installPath;
    private string? _installProblem;
    private KspSaveFolder? _selectedSave;
    private bool _watchSave;
    private string _krpcAddress;
    private string _krpcRpcPort;
    private string _krpcStreamPort;
    private bool _krpcConnectOnStartup;

    public SettingsViewModel(AppSettings settings, PartData parts)
    {
        Parts = parts;
        _installPath = settings.KspInstallPath ?? KspInstallLocator.FindSteamInstall() ?? "";
        _watchSave = settings.WatchSave;
        _krpcAddress = settings.KrpcAddress;
        _krpcRpcPort = settings.KrpcRpcPort.ToString(CultureInfo.InvariantCulture);
        _krpcStreamPort = settings.KrpcStreamPort.ToString(CultureInfo.InvariantCulture);
        _krpcConnectOnStartup = settings.KrpcConnectOnStartup;
        RestoreKrpcDefaultsCommand = new RelayCommand(RestoreKrpcDefaults);
        BrowseCommand = new RelayCommand(Browse);
        DetectCommand = new RelayCommand(Detect);
        ReadPartDataCommand = new RelayCommand(async () => await parts.ReadAsync(InstallPath.Trim()),
            () => !parts.IsReading && InstallProblem is null && InstallPath.Trim().Length > 0);
        LoadSaves(settings.KspSaveName);
    }

    public RelayCommand BrowseCommand { get; }
    public RelayCommand DetectCommand { get; }
    public RelayCommand ReadPartDataCommand { get; }
    public RelayCommand RestoreKrpcDefaultsCommand { get; }

    /// <summary>Part masses, engines and antennas read from this install (shown with a button to read them again).</summary>
    public PartData Parts { get; }

    /// <summary>Lets the user pick a folder; the view supplies the dialog. Returns null when cancelled.</summary>
    public Func<string?, string?> PickFolder { get; set; } = _ => null;

    public string InstallPath
    {
        get => _installPath;
        set { if (SetProperty(ref _installPath, value)) LoadSaves(SelectedSave?.Name); }
    }

    /// <summary>Why the folder isn't a KSP install, or null.</summary>
    public string? InstallProblem { get => _installProblem; private set => SetProperty(ref _installProblem, value); }

    public ObservableCollection<KspSaveFolder> Saves { get; } = [];

    public KspSaveFolder? SelectedSave
    {
        get => _selectedSave;
        set { if (SetProperty(ref _selectedSave, value)) OnPropertyChanged(nameof(SaveDetails)); }
    }

    public string SaveDetails => SelectedSave is { } s ? $"Last saved by KSP {s.LastWriteTime:g} · {s.PersistentPath}" : "";

    public bool WatchSave { get => _watchSave; set => SetProperty(ref _watchSave, value); }

    public string KrpcAddress { get => _krpcAddress; set { if (SetProperty(ref _krpcAddress, value)) OnKrpcChanged(); } }

    public string KrpcRpcPort { get => _krpcRpcPort; set { if (SetProperty(ref _krpcRpcPort, value)) OnKrpcChanged(); } }

    public string KrpcStreamPort { get => _krpcStreamPort; set { if (SetProperty(ref _krpcStreamPort, value)) OnKrpcChanged(); } }

    public bool KrpcConnectOnStartup { get => _krpcConnectOnStartup; set => SetProperty(ref _krpcConnectOnStartup, value); }

    /// <summary>What's wrong with the connection settings, or null. OK is disabled until it's fixed.</summary>
    public string? KrpcProblem
    {
        get
        {
            if (string.IsNullOrWhiteSpace(KrpcAddress)) return "Enter the address of the computer running KSP (127.0.0.1 for this one).";
            if (Port(KrpcRpcPort) is not { } rpc) return "The RPC port must be a whole number from 1 to 65535.";
            if (Port(KrpcStreamPort) is not { } stream) return "The stream port must be a whole number from 1 to 65535.";
            return rpc == stream ? "The RPC and stream ports must be different." : null;
        }
    }

    public bool CanSave => KrpcProblem is null;

    public AppSettings Result => new()
    {
        KspInstallPath = string.IsNullOrWhiteSpace(InstallPath) ? null : InstallPath.Trim(),
        KspSaveName = SelectedSave?.Name,
        WatchSave = WatchSave,
        KrpcAddress = KrpcAddress.Trim(),
        KrpcRpcPort = Port(KrpcRpcPort) ?? AppSettings.DefaultKrpcRpcPort,
        KrpcStreamPort = Port(KrpcStreamPort) ?? AppSettings.DefaultKrpcStreamPort,
        KrpcConnectOnStartup = KrpcConnectOnStartup,
    };

    private void OnKrpcChanged()
    {
        OnPropertyChanged(nameof(KrpcProblem));
        OnPropertyChanged(nameof(CanSave));
    }

    private void RestoreKrpcDefaults()
    {
        KrpcAddress = AppSettings.DefaultKrpcAddress;
        KrpcRpcPort = AppSettings.DefaultKrpcRpcPort.ToString(CultureInfo.InvariantCulture);
        KrpcStreamPort = AppSettings.DefaultKrpcStreamPort.ToString(CultureInfo.InvariantCulture);
    }

    private static int? Port(string text) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535 ? port : null;

    private void Browse()
    {
        if (PickFolder(string.IsNullOrWhiteSpace(InstallPath) ? null : InstallPath) is { } folder) InstallPath = folder;
    }

    private void Detect()
    {
        if (KspInstallLocator.FindSteamInstall() is { } found) InstallPath = found;
        else InstallProblem = "Couldn't find KSP in your Steam libraries — use Browse… instead.";
    }

    private void LoadSaves(string? keep)
    {
        Saves.Clear();
        InstallProblem = KspSaveReader.CheckInstallFolder(InstallPath.Trim());
        if (InstallProblem is null)
        {
            foreach (var save in KspSaveReader.FindSaves(InstallPath.Trim())) Saves.Add(save);
            if (Saves.Count == 0) InstallProblem = "No saves found yet — start a game in KSP first.";
        }
        SelectedSave = Saves.FirstOrDefault(s => s.Name == keep) ?? Saves.FirstOrDefault();
    }
}
