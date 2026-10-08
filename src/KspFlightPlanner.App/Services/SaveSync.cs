using System.IO;
using System.Windows.Threading;
using KspFlightPlanner.App.ViewModels;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App.Services;

/// <summary>
/// Reads the chosen KSP save, keeps custom locations linked to its vessels up to date, and (when enabled) watches
/// persistent.sfs so they follow the game whenever KSP saves — on scene changes, launches and its autosave.
/// </summary>
public sealed class SaveSync : ObservableObject, IDisposable
{
    private readonly SettingsRepository _settingsRepository;
    private readonly BodyRepository _bodies;
    private readonly CustomLocationRepository _locations;
    private readonly PlannerEvents _events;
    private readonly DispatcherTimer _debounce;
    private FileSystemWatcher? _watcher;
    private int _readVersion;

    private AppSettings _settings;
    private KspSave? _save;
    private string _status = "";
    private string? _problem;
    private int _newVessels;

    public SaveSync(SettingsRepository settings, BodyRepository bodies, CustomLocationRepository locations, PlannerEvents events)
    {
        _settingsRepository = settings;
        _bodies = bodies;
        _locations = locations;
        _events = events;
        _settings = settings.Load();
        // KSP can write the file in several steps; wait for it to settle before reading.
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = ReloadAsync(); };
        Restart();
    }

    public AppSettings Settings => _settings;

    /// <summary>The last save read, or null if none is configured or it couldn't be read.</summary>
    public KspSave? Save { get => _save; private set { if (SetProperty(ref _save, value)) OnPropertyChanged(nameof(HasSave)); } }

    public bool HasSave => Save is not null;

    /// <summary>One line for the main window: which save, its game date, when it was read, what changed.</summary>
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>Why the save couldn't be read, or null.</summary>
    public string? Problem { get => _problem; private set { if (SetProperty(ref _problem, value)) OnPropertyChanged(nameof(HasProblem)); } }

    public bool HasProblem => Problem is not null;

    /// <summary>Vessels in orbit that aren't imported yet (ships, stations, probes… not debris or asteroids).</summary>
    public int NewVessels { get => _newVessels; private set { if (SetProperty(ref _newVessels, value)) { OnPropertyChanged(nameof(NewVesselsText)); OnPropertyChanged(nameof(HasNewVessels)); } } }

    public bool HasNewVessels => NewVessels > 0;

    public string NewVesselsText => NewVessels == 1 ? "1 new vessel" : $"{NewVessels} new vessels";

    public string? SaveName => _settings.KspSaveName;

    public string? PersistentPath =>
        _settings.KspInstallPath is { } install && _settings.KspSaveName is { } save ? KspSaveReader.PersistentPath(install, save) : null;

    /// <summary>Stores new settings and starts over with them.</summary>
    public void Apply(AppSettings settings)
    {
        _settingsRepository.Save(settings);
        _settings = _settingsRepository.Load();
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(SaveName));
        Restart();
    }

    /// <summary>Every vessel in the save with what importing it would create (or why it can't be imported).</summary>
    public IReadOnlyList<VesselImport> Candidates()
    {
        if (Save is null || SaveName is null) return [];
        var bodies = StockBodies();
        return Save.Vessels.Select(v => SaveImport.Convert(v, SaveName, bodies)).ToList();
    }

    /// <summary>The custom location already following this vessel, if any.</summary>
    public CustomLocation? LinkedLocation(SavedVessel vessel) =>
        _locations.GetAll().FirstOrDefault(l => l.KspSave == SaveName && l.KspVesselId == vessel.Id);

    /// <summary>Adds the vessels as linked custom locations; returns their ids.</summary>
    public IReadOnlyList<long> Import(IEnumerable<VesselImport> imports)
    {
        var ids = imports.Where(i => i.Location is not null).Select(i => _locations.Add(i.Location!)).ToList();
        if (ids.Count > 0)
        {
            _events.RaiseLocationsChanged(this);
            UpdateStatus(updated: 0);
        }
        return ids;
    }

    /// <summary>Reads the save now (also called by the watcher).</summary>
    public async Task ReloadAsync()
    {
        var path = PersistentPath;
        if (path is null)
        {
            Save = null;
            Problem = null;
            Status = "No KSP save chosen — Tools › Settings to link one.";
            NewVessels = 0;
            return;
        }

        var version = ++_readVersion;
        try
        {
            var save = await Task.Run(() => KspSaveReader.Read(path));
            if (version != _readVersion) return; // settings changed while reading
            Save = save;
            Problem = null;
            UpdateStatus(RefreshLinkedLocations(save));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            if (version != _readVersion) return;
            Save = null;
            Problem = ex is FileNotFoundException or DirectoryNotFoundException
                ? $"Can't find {path}. Check the KSP folder and save in Settings."
                : $"Couldn't read the save: {ex.Message}";
            Status = $"{SaveName}: not read";
            NewVessels = 0;
        }
    }

    /// <summary>Moves every location linked to this save onto its vessel's current orbit. Returns how many changed.</summary>
    private int RefreshLinkedLocations(KspSave save)
    {
        var bodies = StockBodies();
        var byId = save.Vessels.Where(v => v.Id.Length > 0).GroupBy(v => v.Id).ToDictionary(g => g.Key, g => g.First());
        var updated = 0;
        foreach (var location in _locations.GetAll().Where(l => l.IsLinked && l.KspSave == SaveName))
        {
            if (!byId.TryGetValue(location.KspVesselId!, out var vessel)) continue;
            if (SaveImport.Convert(vessel, SaveName!, bodies).Location is not { } current) continue; // e.g. it landed: keep the last orbit
            var refreshed = SaveImport.Refresh(location, current);
            if (refreshed == location) continue;
            _locations.Update(refreshed);
            updated++;
        }
        if (updated > 0) _events.RaiseLocationsChanged(this);
        return updated;
    }

    private void UpdateStatus(int updated)
    {
        if (Save is null) return;
        var linked = _locations.GetAll().Where(l => l.IsLinked && l.KspSave == SaveName).ToList();
        var ids = Save.Vessels.Select(v => v.Id).ToHashSet();
        var missing = linked.Count(l => !ids.Contains(l.KspVesselId!));
        var linkedIds = linked.Select(l => l.KspVesselId).ToHashSet();
        NewVessels = Candidates().Count(c => c.CanImport && SaveImport.IsUsuallyWanted(c.Vessel) && !linkedIds.Contains(c.Vessel.Id));

        var parts = new List<string>
        {
            $"{SaveName} · {GameCalendar.Kerbin.FormatDay(Save.Ut)}",
            $"read {DateTime.Now:t}",
            linked.Count == 1 ? "1 linked location" : $"{linked.Count} linked locations",
        };
        if (updated > 0) parts.Add($"{updated} updated");
        if (missing > 0) parts.Add($"{missing} no longer in the save");
        if (_settings.WatchSave) parts.Add("watching");
        Status = string.Join(" · ", parts);
    }

    private Dictionary<string, CelestialBody> StockBodies()
    {
        var system = _bodies.GetSystems().FirstOrDefault(s => s.Name == "KSP Stock");
        return system is null ? [] : _bodies.GetBodies(system.Id).ToDictionary(b => b.Name);
    }

    private void Restart()
    {
        StopWatching();
        if (_settings.WatchSave && PersistentPath is { } path && Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
        {
            _watcher = new FileSystemWatcher(dir, KspSaveReader.PersistentFileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            FileSystemEventHandler changed = (_, _) => Dispatch();
            _watcher.Changed += changed;
            _watcher.Created += changed;
            _watcher.Renamed += (_, _) => Dispatch();
            _watcher.EnableRaisingEvents = true;
        }
        _ = ReloadAsync();
    }

    /// <summary>Watcher events arrive on a worker thread; restart the debounce on the UI thread.</summary>
    private void Dispatch() => _debounce.Dispatcher.BeginInvoke(() =>
    {
        _debounce.Stop();
        _debounce.Start();
    });

    private void StopWatching()
    {
        _debounce.Stop();
        _watcher?.Dispose();
        _watcher = null;
    }

    public void Dispose() => StopWatching();
}
