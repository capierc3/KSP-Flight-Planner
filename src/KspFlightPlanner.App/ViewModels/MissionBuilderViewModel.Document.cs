using System.Collections.ObjectModel;
using KspFlightPlanner.Core;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App.ViewModels;

/// <summary>A saved mission in the Missions list.</summary>
public sealed record SavedMissionItem(SavedMission Mission, string Route, string Saved, bool IsOpen)
{
    public string Name => Mission.Name;
}

/// <summary>
/// The mission as a document: which saved mission is open, its name and notes, whether it has unsaved changes, and
/// New / Open / Save / Save As / Delete. Unsaved changes are found by comparing what would be saved now with what was
/// saved (or opened) last.
/// </summary>
public sealed partial class MissionBuilderViewModel
{
    private const string DefaultName = "New mission";

    private readonly MissionRepository? _missions;
    private readonly SettingsRepository? _settings;
    private long? _missionId;
    private string _missionName = DefaultName;
    private string _notes = "";
    private string? _designPath;
    private string _savedName = DefaultName;
    private string _savedJson = "";
    private bool _isDirty;
    private SavedMissionItem? _selectedSavedMission;

    /// <summary>Set by the window: asks whether to save changes first. True = save, false = discard, null = cancel.</summary>
    public Func<string, bool?>? AskToSave { get; set; }

    /// <summary>Set by the window: a yes/no question.</summary>
    public Func<string, bool>? Confirm { get; set; }

    public RelayCommand NewMissionCommand { get; }
    public RelayCommand SaveMissionCommand { get; }
    public RelayCommand SaveMissionAsCommand { get; }
    public RelayCommand<SavedMissionItem> OpenMissionCommand { get; }
    public RelayCommand<SavedMissionItem> DeleteMissionCommand { get; }

    /// <summary>The open mission's database id; null until it's first saved.</summary>
    public long? MissionId => _missionId;

    public string MissionName
    {
        get => _missionName;
        set
        {
            if (!SetProperty(ref _missionName, value)) return;
            OnPropertyChanged(nameof(WindowTitle));
            UpdateDirty();
        }
    }

    /// <summary>Free-form notes saved with the mission.</summary>
    public string Notes
    {
        get => _notes;
        set
        {
            if (SetProperty(ref _notes, value)) UpdateDirty();
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value)) OnPropertyChanged(nameof(WindowTitle));
        }
    }

    public string WindowTitle => $"{_missionName}{(IsDirty ? " *" : "")} — KSP Flight Planner";

    /// <summary>Every saved mission, most recently saved first.</summary>
    public ObservableCollection<SavedMissionItem> SavedMissions { get; } = [];

    public SavedMissionItem? SelectedSavedMission
    {
        get => _selectedSavedMission;
        set => SetProperty(ref _selectedSavedMission, value);
    }

    public bool HasNoSavedMissions => SavedMissions.Count == 0;

    /// <summary>What Save would store right now.</summary>
    public MissionDefinition ToDefinition() => new(
        Stops.Select(s => s.ToDefinition()).ToList(),
        Phases.Select(p => p.ToDefinition()).ToList(),
        Legs.Select(l => l.Setting(Phases)).ToList(),
        UseAerobraking,
        IncludePlaneChanges,
        _designPath,
        string.IsNullOrWhiteSpace(_notes) ? null : _notes.TrimEnd(),
        UsedPlaces());

    /// <summary>
    /// Before something replaces the open mission (New, Open, closing the app): offers to save unsaved changes.
    /// False if the user cancelled.
    /// </summary>
    public bool ConfirmDiscard()
    {
        if (!IsDirty || AskToSave is null) return true;
        switch (AskToSave($"Save changes to “{DisplayName}”?"))
        {
            case true: return SaveMission();
            case false: return true;
            default: return false;
        }
    }

    /// <summary>Picks up where the user left off in this save; otherwise a fresh mission on Kerbin.</summary>
    private void OpenLastMission()
    {
        if (_map is null) return;
        if (_settings?.LoadLastMissionId() is { } lastId && _missions?.Get(lastId) is { } last) OpenMission(last);
        else NewMission();
    }

    /// <summary>A fresh, unsaved mission: a clean slate on Kerbin's surface, waiting for its first stop.</summary>
    private void NewMission()
    {
        Load(null, DefaultName, new MissionDefinition([new("kerbin.surface")], [], []));
    }

    private void OpenMission(SavedMission mission)
    {
        Load(mission.Id, mission.Name, mission.Definition);
        _settings?.SaveLastMissionId(mission.Id);
    }

    private void Load(long? id, string name, MissionDefinition definition)
    {
        if (_map is null) return;
        _missionId = id;
        _missionName = name;
        _notes = definition.Notes ?? "";
        _useAerobraking = definition.UseAerobraking;
        _includePlaneChanges = definition.IncludePlaneChanges;
        _designPath = definition.DesignPath;
        _selectedDesign = SelectDesign(_designPath);
        SetPlaces(definition.Places ?? []);

        // Stops on locations deleted since the mission was saved are dropped; none left and it starts over.
        var stops = definition.Stops.Where(s => _map.TryGet(s.NodeKey, out _)).ToList();
        if (stops.Count == 0) stops = [new("kerbin.surface")];
        _suspend = true;
        Stops.Clear();
        foreach (var s in stops) Stops.Add(new StopViewModel(s, StopChanged, RefreshLegs));
        for (var i = 0; i < Stops.Count; i++) Stops[i].SetPosition(i, Stops.Count);
        _suspend = false;

        var nodes = Stops.Select(s => _map[s.NodeKey]).ToList();
        var segments = Route(nodes);
        var defaults = MissionPlanner.DefaultPhases(segments, nodes, _useAerobraking);
        // A mission without saved phases (a new one) gets the default split, like a route change does.
        IReadOnlyList<LegSetting> defaultLegs = defaults.Legs.Select(s => s with { IncludePlaneChange = _includePlaneChanges }).ToList();
        _defaultSplit = (defaults.Phases, defaultLegs);
        var (phases, settings) = definition.Phases.Count > 0
            ? (definition.Phases, definition.Legs)
            : (defaults.Phases, defaultLegs);
        Apply(segments, phases, settings);

        foreach (var property in new[] { nameof(MissionId), nameof(MissionName), nameof(Notes), nameof(UseAerobraking),
                     nameof(IncludePlaneChanges), nameof(SelectedDesign) })
            OnPropertyChanged(property);
        MarkSaved();
        RefreshSavedMissions();
        RouteChanged?.Invoke();
    }

    /// <summary>Saves over the open mission, or as a new one if it was never saved. False if there's nowhere to save.</summary>
    private bool SaveMission()
    {
        if (_missions is null || _map is null) return false;
        var name = DisplayName;
        var definition = ToDefinition();
        if (_missionId is not { } id || !_missions.Update(id, name, definition)) _missionId = _missions.Add(name, definition);
        _missionName = name;
        OnPropertyChanged(nameof(MissionName));
        OnPropertyChanged(nameof(MissionId));
        _settings?.SaveLastMissionId(_missionId);
        MarkSaved();
        RefreshSavedMissions();
        return true;
    }

    /// <summary>Saves a copy under a new name and carries on editing the copy (rename it in the Mission pane).</summary>
    private void SaveMissionAs()
    {
        if (_missions is null || _map is null) return;
        _missionName = $"{DisplayName} (copy)";
        _missionId = null;
        SaveMission();
    }

    private void DeleteMission(SavedMissionItem item)
    {
        if (_missions is null) return;
        if (Confirm is not null && !Confirm($"Delete the mission “{item.Name}”? This can't be undone.")) return;
        _missions.Delete(item.Mission.Id);
        if (item.Mission.Id == _missionId)
        {
            // Keep what's on screen, now as an unsaved mission.
            _missionId = null;
            _savedJson = "";
            _settings?.SaveLastMissionId(null);
            OnPropertyChanged(nameof(MissionId));
            UpdateDirty();
        }
        RefreshSavedMissions();
    }

    private string DisplayName => string.IsNullOrWhiteSpace(_missionName) ? "Untitled mission" : _missionName.Trim();

    private void MarkSaved()
    {
        _savedName = _missionName;
        _savedJson = MissionRepository.Serialize(ToDefinition());
        IsDirty = false;
        OnPropertyChanged(nameof(WindowTitle));
    }

    private void UpdateDirty()
    {
        if (_suspend) return;
        IsDirty = _missionName != _savedName || MissionRepository.Serialize(ToDefinition()) != _savedJson;
    }

    private DesignChoice SelectDesign(string? path) =>
        DesignChoices.FirstOrDefault(c => c.Design is not null && c.Path == path) ?? DesignChoices[0];

    private void RefreshSavedMissions()
    {
        if (_missions is null) return;
        var selectedId = _selectedSavedMission?.Mission.Id;
        SavedMissions.Clear();
        foreach (var m in _missions.GetAll())
            SavedMissions.Add(new SavedMissionItem(m, RouteText(m.Definition), $"Saved {m.UpdatedUtc.ToLocalTime():g}", m.Id == _missionId));
        _selectedSavedMission = SavedMissions.FirstOrDefault(i => i.Mission.Id == selectedId);
        OnPropertyChanged(nameof(SelectedSavedMission));
        OnPropertyChanged(nameof(HasNoSavedMissions));
    }

    /// <summary>The route by stop names; a mission's own places (not on this mission's map) by their saved name.</summary>
    private string RouteText(MissionDefinition definition) => string.Join(" → ", definition.Stops.Select(s =>
        definition.Places?.FirstOrDefault(p => p.Key == s.NodeKey) is { } place ? $"{place.BodyName} — {place.Name} ({CustomLocations.AltitudeText(place)})"
        : _map is not null && _map.TryGet(s.NodeKey, out var node) ? node.DisplayName : s.NodeKey));
}
