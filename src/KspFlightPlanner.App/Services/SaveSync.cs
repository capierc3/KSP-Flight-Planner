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
/// While connected to the game (<see cref="KspLink"/>), its live time and orbits are laid over the save, so everything
/// reading <see cref="Save"/> follows the game between saves too.
/// The chosen save also scopes the planner's own data (<see cref="PlannerDatabase.ActiveSave"/>).
/// </summary>
public sealed class SaveSync : ObservableObject, IDisposable
{
    private readonly PlannerDatabase _database;
    private readonly SettingsRepository _settingsRepository;
    private readonly BodyRepository _bodies;
    private readonly CustomLocationRepository _locations;
    private readonly PlannerEvents _events;
    private readonly PartData _parts;
    private readonly KspLink _link;
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _shipsDebounce;
    private FileSystemWatcher? _watcher;
    private FileSystemWatcher? _shipsWatcher;
    private IReadOnlyList<CraftFileInfo> _crafts = [];
    private int _readVersion;

    private AppSettings _settings;
    private KspSave? _save;
    private KspSave? _fileSave;
    private LiveMerge? _live;
    private DateTime _readAt;
    private string _status = "";
    private string? _problem;
    private int _newVessels;

    // A plan that starts "now" moves on this much game time before it's worth re-planning (orbit changes re-plan at once).
    private const double LiveTimeStepS = 60;

    public SaveSync(PlannerDatabase database, SettingsRepository settings, BodyRepository bodies, CustomLocationRepository locations,
        PartData parts, PlannerEvents events, KspLink link)
    {
        _database = database;
        _parts = parts;
        _link = link;
        link.GameUpdated += _ => ApplyLive(fileChanged: false);
        _settingsRepository = settings;
        _bodies = bodies;
        _locations = locations;
        _events = events;
        _settings = settings.Load();
        database.ActiveSave = _settings.KspSaveName;
        // KSP can write the file in several steps; wait for it to settle before reading.
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = ReloadAsync(); };
        // The editor writes the auto-saved ship as you build; settle before reading.
        _shipsDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _shipsDebounce.Tick += (_, _) => { _shipsDebounce.Stop(); RefreshCrafts(); };
        Restart();
    }

    public AppSettings Settings => _settings;

    /// <summary>Part data from the same KSP install; read again when the install or its mods change.</summary>
    public PartData Parts => _parts;

    /// <summary>The live connection to the game.</summary>
    public KspLink Link => _link;

    /// <summary>
    /// The last game state read: the save file, with the game's live time and orbits laid over it while connected.
    /// Null if no save is configured or it couldn't be read.
    /// </summary>
    public KspSave? Save { get => _save; private set { if (SetProperty(ref _save, value)) OnPropertyChanged(nameof(HasSave)); } }

    public bool HasSave => Save is not null;

    /// <summary>How the live game matched the save: which vessel is being flown and targeted, and vessels not in the save yet.</summary>
    public LiveMerge? Live { get => _live; private set { if (SetProperty(ref _live, value)) OnPropertyChanged(nameof(IsLive)); } }

    /// <summary>The game time and (in flight) orbits come from the running game rather than the save file.</summary>
    public bool IsLive => Live is not null;

    /// <summary>Game time now: the live game's while connected, else when KSP last saved.</summary>
    public double? Now => _link.Game?.Ut ?? Save?.Ut;

    /// <summary>The vessel being flown in KSP (with its save id when it's in the save), or null when not flying one.</summary>
    public FlightCraft? ActiveCraft =>
        _link.Game?.Active is { } active ? new FlightCraft(active.Name, SaveIdOf(active)) : null;

    /// <summary>The vessel targeted in KSP, or null.</summary>
    public FlightCraft? TargetCraft =>
        _link.Game?.Target is { } target ? new FlightCraft(target.Name, SaveIdOf(target)) : null;

    /// <summary>
    /// A craft's current orbit: live while connected and flying (from the save otherwise), found by save id, or by name
    /// for a craft not in the save yet. Null if it isn't in orbit or can't be found.
    /// </summary>
    public SavedOrbit? OrbitOf(FlightCraft craft) => PathOf(craft) is { IsClosed: true } orbit ? orbit : null;

    /// <summary>Like <see cref="OrbitOf"/>, but also a path that escapes the body (leaving it, or flying past a moon).</summary>
    public SavedOrbit? PathOf(FlightCraft craft)
    {
        SavedOrbit? orbit = null;
        if (craft.SaveVesselId is { } id) orbit = Save?.Vessels.FirstOrDefault(v => v.Id == id)?.Orbit;
        else if (_link.Game?.Vessels is { } vessels) orbit = vessels.FirstOrDefault(v => v.Name == craft.Name && Live?.SaveIds.ContainsKey(v) != true)?.Orbit;
        return orbit is { IsPath: true } ? orbit : null;
    }

    /// <summary>The save vessel a live vessel was matched to, or null when it's not in the save.</summary>
    private string? SaveIdOf(LiveVessel vessel)
    {
        if (Live is not { } live) return null;
        if (live.SaveIds.TryGetValue(vessel, out var id)) return id;
        // Not the merge's own object (from an earlier poll): fall back to a unique name.
        var byName = live.SaveIds.Where(p => p.Key.Name == vessel.Name && p.Key.Type == vessel.Type).Select(p => p.Value).ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    /// <summary>Saved ship designs (.craft) in the save's Ships/VAB and Ships/SPH folders, newest first.</summary>
    public IReadOnlyList<CraftFileInfo> Crafts { get => _crafts; private set => SetProperty(ref _crafts, value); }

    /// <summary>Surface gravity of the home planet (Kerbin), for launch TWR.</summary>
    public double HomeSurfaceGravity =>
        StockBodies().Values.FirstOrDefault(b => b.IsDefault) is { } home ? home.Mu / (home.EquatorialRadiusM * home.EquatorialRadiusM) : 9.81;

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

    /// <summary>
    /// Stores new settings and starts over with them. Another save swaps in its locations, missions, flights and
    /// constellations (offer to save the open mission first: it belongs to the old save).
    /// </summary>
    public void Apply(AppSettings settings)
    {
        _settingsRepository.Save(settings);
        _settings = _settingsRepository.Load();
        var saveChanged = _database.ActiveSave != _settings.KspSaveName;
        _database.ActiveSave = _settings.KspSaveName;
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(SaveName));
        if (saveChanged)
        {
            _events.RaiseLocationsChanged(this);
            _events.RaiseSaveChanged();
        }
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
            _fileSave = null;
            Save = null;
            Live = null;
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
            _fileSave = save;
            _readAt = DateTime.Now;
            Problem = null;
            ApplyLive(fileChanged: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            if (version != _readVersion) return;
            _fileSave = null;
            Save = null;
            Live = null;
            Problem = ex is FileNotFoundException or DirectoryNotFoundException
                ? $"Can't find {path}. Check the KSP folder and save in Settings."
                : $"Couldn't read the save: {ex.Message}";
            Status = $"{SaveName}: not read";
            NewVessels = 0;
        }
    }

    /// <summary>
    /// Publishes the save file with the live game over it (or the file alone when not connected, or when the file is newer).
    /// A live update only replaces <see cref="Save"/> when an orbit or situation changed or the game time moved on by
    /// <see cref="LiveTimeStepS"/>, so panes that re-plan from it don't churn every poll.
    /// </summary>
    private void ApplyLive(bool fileChanged)
    {
        if (_fileSave is not { } file) return;
        var previous = Save;
        if (_link.Game is not { } game || game.Ut < file.Ut)
        {
            Live = null;
            if (!fileChanged && ReferenceEquals(previous, file)) return;
            Save = file;
        }
        else
        {
            var bodies = StockBodies();
            double? mu(string body) => bodies.GetValueOrDefault(body)?.Mu;
            var merge = LiveMerge.Apply(file, game, previous, mu);
            // Keep the objects so the list doesn't look changed. (Their IsActive/IsTarget may then be stale: look those up
            // in the link's Game by name.)
            if (Live is { } last && SameVessels(last.Unmatched, merge.Unmatched, game.Ut, mu))
                merge = merge with { Unmatched = last.Unmatched };
            var changed = fileChanged || previous is null || Math.Abs(merge.Save.Ut - previous.Ut) >= LiveTimeStepS
                          || !ReferenceEquals(merge.Unmatched, Live?.Unmatched) || VesselsChanged(previous, merge.Save);
            Live = merge;
            if (!changed) return;
            Save = merge.Save;
        }
        UpdateStatus(RefreshLinkedLocations(Save!));
    }

    private static bool VesselsChanged(KspSave before, KspSave after) =>
        before.Vessels.Count != after.Vessels.Count
        || before.Vessels.Zip(after.Vessels).Any(p => p.First.Id != p.Second.Id || p.First.Situation != p.Second.Situation
                                                       || !ReferenceEquals(p.First.Orbit, p.Second.Orbit));

    private static bool SameVessels(IReadOnlyList<LiveVessel> a, IReadOnlyList<LiveVessel> b, double ut, Func<string, double?> mu) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Name == p.Second.Name && p.First.Situation == p.Second.Situation
            && (p.First.Orbit, p.Second.Orbit) switch
            {
                (null, null) => true,
                ({ } x, { } y) => LiveMerge.SamePath(x, y, ut, mu),
                _ => false,
            });

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
            IsLive ? "live from KSP" : $"read {_readAt:t}",
            linked.Count == 1 ? "1 linked location" : $"{linked.Count} linked locations",
        };
        if (updated > 0) parts.Add($"{updated} updated");
        if (missing > 0) parts.Add($"{missing} no longer in the save");
        if (_settings.WatchSave) parts.Add("watching");
        Status = string.Join(" · ", parts);
    }

    public Dictionary<string, CelestialBody> StockBodies()
    {
        var system = _bodies.GetSystems().FirstOrDefault(s => s.Name == BodyRepository.StockSystemName);
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
        if (_settings.WatchSave && _settings.KspInstallPath is { } install && _settings.KspSaveName is { } saveName
            && CraftReader.ShipsFolder(install, saveName) is var ships && Directory.Exists(ships))
        {
            _shipsWatcher = new FileSystemWatcher(ships, "*.craft")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            FileSystemEventHandler changed = (_, _) => DispatchShips();
            _shipsWatcher.Changed += changed;
            _shipsWatcher.Created += changed;
            _shipsWatcher.Deleted += changed;
            _shipsWatcher.Renamed += (_, _) => DispatchShips();
            _shipsWatcher.EnableRaisingEvents = true;
        }
        RefreshCrafts();
        _ = _parts.EnsureCurrentAsync(_settings.KspInstallPath);
        _ = ReloadAsync();
    }

    /// <summary>Watcher events arrive on a worker thread; restart the debounce on the UI thread.</summary>
    private void Dispatch() => _debounce.Dispatcher.BeginInvoke(() =>
    {
        _debounce.Stop();
        _debounce.Start();
    });

    private void DispatchShips() => _shipsDebounce.Dispatcher.BeginInvoke(() =>
    {
        _shipsDebounce.Stop();
        _shipsDebounce.Start();
    });

    /// <summary>Lists the design files again; always a new list, so listeners rebuild even when only a file's contents changed.</summary>
    public void RefreshCrafts() =>
        Crafts = _settings.KspInstallPath is { } install && _settings.KspSaveName is { } save ? CraftReader.FindCrafts(install, save) : [];

    private void StopWatching()
    {
        _debounce.Stop();
        _shipsDebounce.Stop();
        _watcher?.Dispose();
        _watcher = null;
        _shipsWatcher?.Dispose();
        _shipsWatcher = null;
    }

    public void Dispose() => StopWatching();
}
