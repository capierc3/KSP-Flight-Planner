using System.Collections.ObjectModel;
using System.Globalization;
using KspFlightPlanner.Core;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App.ViewModels;

/// <summary>A rocket phase in the mission builder.</summary>
public sealed class PhaseViewModel : ObservableObject
{
    private static readonly string[] Palette =
        ["#4FC3F7", "#FFB74D", "#81C784", "#BA68C8", "#E57373", "#FFD54F", "#4DB6AC", "#9575CD"];

    private readonly Action _changed;
    private string _name;
    private double _marginPercent;
    private double _extraDv;

    public PhaseViewModel(PhaseDefinition definition, int colorIndex, Action changed)
    {
        _name = definition.Name;
        _marginPercent = definition.MarginPercent;
        _extraDv = definition.ExtraDv;
        Color = Palette[colorIndex % Palette.Length];
        _changed = changed;
    }

    public string Name
    {
        get => _name;
        set { if (SetProperty(ref _name, value)) _changed(); }
    }

    public double MarginPercent
    {
        get => _marginPercent;
        set { if (SetProperty(ref _marginPercent, Math.Max(0, value))) _changed(); }
    }

    /// <summary>Fixed extra Δv on top of the percentage margin (e.g. landing wiggle room).</summary>
    public double ExtraDv
    {
        get => _extraDv;
        set { if (SetProperty(ref _extraDv, Math.Max(0, value))) _changed(); }
    }

    public string Color { get; }

    // Set by the mission after each recalculation.
    public double RequiredDv { get; private set; }
    public double MarginDv { get; private set; }
    public double TotalDv => RequiredDv + MarginDv;
    public string RequiredText => Format.Speed0(RequiredDv);
    public string MarginText => "+" + Format.Speed0(MarginDv);
    public string TotalText => Format.Speed0(TotalDv);

    /// <summary>Rocket diagram size: height ∝ Δv share, width narrows for upper phases.</summary>
    public double BarHeight { get; private set; }
    public double BarWidth { get; private set; }

    public PhaseDefinition ToDefinition() => new(Name, MarginPercent, ExtraDv);

    internal void Update(PhaseSummary summary, int index, int count, double missionTotal)
    {
        RequiredDv = summary.RequiredDv;
        MarginDv = summary.MarginDv;
        BarHeight = 48 + 300 * (missionTotal > 0 ? TotalDv / missionTotal : 1.0 / count);
        BarWidth = Math.Max(90, 190 - index * 26);
        foreach (var p in new[] { nameof(RequiredDv), nameof(MarginDv), nameof(TotalDv), nameof(RequiredText),
                     nameof(MarginText), nameof(TotalText), nameof(BarHeight), nameof(BarWidth) })
            OnPropertyChanged(p);
    }
}

/// <summary>A stop in the route (like a Google Maps waypoint). Identified by map node key.</summary>
public sealed class StopViewModel : ObservableObject
{
    private readonly Action _changed;
    private readonly Action _noteChanged;
    private string _nodeKey;
    private string _note;

    public StopViewModel(string nodeKey, string? note, Action changed, Action noteChanged)
    {
        _nodeKey = nodeKey;
        _note = note ?? "";
        _changed = changed;
        _noteChanged = noteChanged;
    }

    public string NodeKey
    {
        get => _nodeKey;
        set { if (value is not null && SetProperty(ref _nodeKey, value)) _changed(); }
    }

    /// <summary>Free text such as "Dock at station, pick up Bob". Doesn't affect the route.</summary>
    public string Note
    {
        get => _note;
        set { if (SetProperty(ref _note, value)) _noteChanged(); }
    }

    public int Number { get; private set; }
    public string Role { get; private set; } = "";

    internal void SetPosition(int index, int count)
    {
        Number = index + 1;
        Role = index == 0 ? "Start" : index == count - 1 ? "End" : "Stop";
        OnPropertyChanged(nameof(Number));
        OnPropertyChanged(nameof(Role));
    }

    public MissionStop ToDefinition() => new(NodeKey, string.IsNullOrWhiteSpace(Note) ? null : Note.Trim());
}

/// <summary>One leg of the route with its phase and options.</summary>
public sealed class LegRowViewModel : ObservableObject
{
    private readonly Action _changed;
    private readonly Action<string, double?> _editDv;
    private PhaseViewModel _phase;
    private bool _useAerobrake;
    private bool _includePlaneChange;

    public LegRowViewModel(RouteLeg leg, int number, string? sectionHeader, PhaseViewModel phase, LegSetting setting,
        Action changed, Action<string, double?> editDv)
    {
        Leg = leg;
        Number = number;
        SectionHeader = sectionHeader;
        _phase = phase;
        _useAerobrake = setting.UseAerobrake;
        _includePlaneChange = setting.IncludePlaneChange;
        _changed = changed;
        _editDv = editDv;
    }

    public RouteLeg Leg { get; }
    public int Number { get; }

    /// <summary>"To stop 2: Kerbin — Low orbit (Dock at station)" on the first leg of each segment.</summary>
    public string? SectionHeader { get; }
    public bool HasSectionHeader => SectionHeader is not null;

    public string FromText => Leg.From.DisplayName;
    public string ToText => Leg.To.DisplayName;
    public bool HasPlaneChange => Leg.PlaneChangeDv > 0;
    public string PlaneChangeText => $"+{Leg.PlaneChangeDv:N0}";
    public bool CanAerobrake => Leg.CanAerobrake;
    public bool IsEdited => Leg.IsOverridden;
    public string DvToolTip => Leg.IsOverridden
        ? $"Edited — map value is {Leg.MapDeltaV:N0} m/s. Clear to restore. Applies to all missions."
        : "Map value. Type a new value to adjust it for all missions.";

    /// <summary>Editable: writes a map override for this edge (both directions).</summary>
    public string DvText
    {
        get => Leg.DeltaV.ToString("0", CultureInfo.CurrentCulture);
        set
        {
            var text = value.Trim();
            if (text.Length == 0) { _editDv(Leg.EdgeKey, null); return; }
            if (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var dv)
                && dv >= 0 && dv != Leg.DeltaV)
                _editDv(Leg.EdgeKey, dv == Leg.MapDeltaV ? null : dv);
        }
    }

    public string Notes => string.Join(" · ", new[]
    {
        Leg.FlightTime is { } t ? $"Flight ≈ {t}" : null,
        Leg.CanAerobrake && Leg.AerobrakeDv > 0 ? $"Aerobraking possible (still needs a {Leg.AerobrakeDv:N0} m/s deorbit burn)"
            : Leg.CanAerobrake ? "Aerobraking possible" : null,
        Leg.To.JetEnginesPossible ? "Jet engines work here" : null,
    }.Where(s => s is not null));

    public PhaseViewModel Phase
    {
        get => _phase;
        set { if (value is not null && SetProperty(ref _phase, value)) _changed(); }
    }

    public bool UseAerobrake
    {
        get => _useAerobrake;
        set { if (SetProperty(ref _useAerobrake, value)) Changed(); }
    }

    public bool IncludePlaneChange
    {
        get => _includePlaneChange;
        set { if (SetProperty(ref _includePlaneChange, value)) Changed(); }
    }

    public LegSetting Setting(IList<PhaseViewModel> phases) => new(phases.IndexOf(Phase), UseAerobrake, IncludePlaneChange);

    public double EffectiveDv => MissionPlanner.EffectiveDv(Leg, new LegSetting(0, UseAerobrake, IncludePlaneChange));
    public string EffectiveText => Format.Speed0(EffectiveDv);

    private void Changed()
    {
        OnPropertyChanged(nameof(EffectiveDv));
        OnPropertyChanged(nameof(EffectiveText));
        _changed();
    }
}

public sealed class MissionBuilderViewModel : ObservableObject
{
    private readonly DvMapRepository _maps;
    private readonly MissionRepository _missions;
    private readonly CustomLocationRepository _locations;
    private readonly PlannerEvents _events;
    private DeltaVMap? _map;

    private string? _missionStatus;

    private bool _useAerobraking = true;
    private bool _includePlaneChanges;
    private string _missionName = "";
    private SavedMission? _selectedMission;
    private bool _suspend;

    public MissionBuilderViewModel(DvMapRepository maps, MissionRepository missions, CustomLocationRepository locations,
        PlannerEvents events)
    {
        _maps = maps;
        _missions = missions;
        _locations = locations;
        _events = events;
        _map = maps.GetMap();

        // Custom locations are managed on the Locations tab; keep the stop pickers and map in step with it.
        _events.LocationsChanged += _ => OnLocationsChanged();

        AddStopCommand = new RelayCommand(AddStop, () => _map is not null);
        MoveStopUpCommand = new RelayCommand<StopViewModel>(s => MoveStop(s, -1), s => Stops.IndexOf(s) > 0);
        MoveStopDownCommand = new RelayCommand<StopViewModel>(s => MoveStop(s, +1), s => Stops.IndexOf(s) < Stops.Count - 1);
        RemoveStopCommand = new RelayCommand<StopViewModel>(RemoveStop, _ => Stops.Count > 2);
        ReverseStopsCommand = new RelayCommand(ReverseStops, () => Stops.Count > 1);
        AddPhaseCommand = new RelayCommand(AddPhase);
        RemovePhaseCommand = new RelayCommand<PhaseViewModel>(RemovePhase, _ => Phases.Count > 1);
        ResetPhasesCommand = new RelayCommand(() => RebuildRoute(keepPhaseTuning: false));
        ResetMapEditsCommand = new RelayCommand(ResetMapEdits, () => EditedCount > 0);
        SaveCommand = new RelayCommand(Save, () => !string.IsNullOrWhiteSpace(MissionName) && Stops.Count >= 2);
        LoadCommand = new RelayCommand(Load, () => SelectedMission is not null);
        DeleteCommand = new RelayCommand(Delete, () => SelectedMission is not null);

        if (_map is not null)
        {
            SyncNodes();
            SetStops([new("kerbin.surface"), new("mun.surface"), new("kerbin.surface")]);
        }
        RefreshMissions();
    }

    public ObservableCollection<DvMapNode> Nodes { get; } = [];
    public ObservableCollection<StopViewModel> Stops { get; } = [];
    public ObservableCollection<PhaseViewModel> Phases { get; } = [];
    public ObservableCollection<LegRowViewModel> Legs { get; } = [];
    public ObservableCollection<SavedMission> SavedMissions { get; } = [];

    /// <summary>Feedback from loading/saving missions, e.g. a mission that uses a deleted location.</summary>
    public string? MissionStatus
    {
        get => _missionStatus;
        private set => SetProperty(ref _missionStatus, value);
    }

    // ---------------- Custom locations (managed on the Locations tab) ----------------

    /// <summary>Custom locations changed: refresh the map and stop pickers, and move stops off deleted locations.</summary>
    private void OnLocationsChanged()
    {
        MoveStopsOffMissingLocations();
        ReloadMap();
    }

    /// <summary>Stops pointing at a deleted custom location move to the location's parent orbit.</summary>
    private void MoveStopsOffMissingLocations()
    {
        if (_map is null) return;
        var remaining = _locations.GetAll().Select(l => l.Key).ToHashSet();
        _suspend = true;
        foreach (var stop in Stops.Where(s => s.NodeKey.StartsWith("custom.") && !remaining.Contains(s.NodeKey)))
        {
            var fallback = _map.TryGet(stop.NodeKey, out var node) ? _map.ParentOf(node)?.Key : null;
            stop.NodeKey = fallback ?? "kerbin.lo";
        }
        _suspend = false;
    }

    /// <summary>Keeps the stop-picker list in step with the map without clearing it (clearing would blank the pickers).</summary>
    private void SyncNodes()
    {
        if (_map is null) return;
        var desired = new List<DvMapNode>();
        foreach (var n in _map.Nodes.Where(n => !n.IsCustom))
        {
            desired.Add(n);
            desired.AddRange(_map.ChildrenOf(n).Where(c => c.IsCustom));
        }
        var keys = desired.Select(n => n.Key).ToHashSet();
        foreach (var stale in Nodes.Where(n => !keys.Contains(n.Key)).ToList()) Nodes.Remove(stale);
        for (var i = 0; i < desired.Count; i++)
            if (i >= Nodes.Count || Nodes[i].Key != desired[i].Key) Nodes.Insert(i, desired[i]);
    }

    /// <summary>Phases for the rocket diagram: last phase on top, launch at the bottom.</summary>
    public IEnumerable<PhaseViewModel> PhasesTopDown => Phases.Reverse().ToList();

    public RelayCommand AddStopCommand { get; }
    public RelayCommand<StopViewModel> MoveStopUpCommand { get; }
    public RelayCommand<StopViewModel> MoveStopDownCommand { get; }
    public RelayCommand<StopViewModel> RemoveStopCommand { get; }
    public RelayCommand ReverseStopsCommand { get; }
    public RelayCommand AddPhaseCommand { get; }
    public RelayCommand<PhaseViewModel> RemovePhaseCommand { get; }
    public RelayCommand ResetPhasesCommand { get; }
    public RelayCommand ResetMapEditsCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand LoadCommand { get; }
    public RelayCommand DeleteCommand { get; }

    // ---------------- Map view bindings ----------------

    public DeltaVMap? Map => _map;
    public IReadOnlyCollection<string> RouteEdgeKeys => Legs.Select(l => l.Leg.EdgeKey).ToHashSet();
    public IReadOnlyList<string> StopKeys => Stops.Select(s => s.NodeKey).ToList();
    public int EditedCount => _map?.Nodes.Count(n => n.IsOverridden) ?? 0;
    public string EditedText => EditedCount switch
    {
        0 => "All values are the published map values.",
        1 => "1 value edited (shown in amber).",
        var n => $"{n} values edited (shown in amber).",
    };

    /// <summary>Clicking a stop on the map adds it before the final stop.</summary>
    public void AddStopFromMap(DvMapNode node)
    {
        var insertAt = Math.Max(1, Stops.Count - 1);
        Stops.Insert(insertAt, new StopViewModel(node.Key, null, StopChanged, RefreshLegs));
        StopChanged();
    }

    /// <summary>Called by the map or the legs list when the user edits a value.</summary>
    public void EditEdge(string nodeKey, double? dv)
    {
        _maps.SetOverride(nodeKey, dv);
        ReloadMap();
    }

    // ---------------- Options ----------------

    /// <summary>Applies to every leg where aerobraking is possible.</summary>
    public bool UseAerobraking
    {
        get => _useAerobraking;
        set
        {
            if (!SetProperty(ref _useAerobraking, value)) return;
            foreach (var leg in Legs) leg.UseAerobrake = value;
        }
    }

    /// <summary>Adds the map's worst-case plane change Δv to every leg that has one.</summary>
    public bool IncludePlaneChanges
    {
        get => _includePlaneChanges;
        set
        {
            if (!SetProperty(ref _includePlaneChanges, value)) return;
            foreach (var leg in Legs) leg.IncludePlaneChange = value;
        }
    }

    public string MissionName
    {
        get => _missionName;
        set => SetProperty(ref _missionName, value);
    }

    public SavedMission? SelectedMission
    {
        get => _selectedMission;
        set => SetProperty(ref _selectedMission, value);
    }

    public string RouteSummary => _map is null ? "" : string.Join(" → ", Stops.Select(s => _map[s.NodeKey].DisplayName));

    public string TotalRequiredText => Format.Speed0(Phases.Sum(p => p.RequiredDv));
    public string TotalMarginText => "+" + Format.Speed0(Phases.Sum(p => p.MarginDv));
    public string TotalText => Format.Speed0(Phases.Sum(p => p.TotalDv));

    // ---------------- Stops ----------------

    private void SetStops(IEnumerable<MissionStop> stops)
    {
        if (_map is null) return;
        _suspend = true;
        Stops.Clear();
        foreach (var s in stops)
            if (_map.TryGet(s.NodeKey, out _)) Stops.Add(new StopViewModel(s.NodeKey, s.Note, StopChanged, RefreshLegs));
        _suspend = false;
        StopChanged();
    }

    private void StopChanged()
    {
        if (_suspend) return;
        for (var i = 0; i < Stops.Count; i++) Stops[i].SetPosition(i, Stops.Count);
        RebuildRoute(keepPhaseTuning: true);
    }

    private void AddStop()
    {
        // Like adding a waypoint: goes before the final stop, starting at the previous stop's location.
        var insertAt = Math.Max(1, Stops.Count - 1);
        Stops.Insert(insertAt, new StopViewModel(Stops[insertAt - 1].NodeKey, null, StopChanged, RefreshLegs));
        StopChanged();
    }

    private void MoveStop(StopViewModel stop, int delta)
    {
        var from = Stops.IndexOf(stop);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= Stops.Count) return;
        Stops.Move(from, to);
        StopChanged();
    }

    private void RemoveStop(StopViewModel stop)
    {
        if (Stops.Count <= 2) return;
        Stops.Remove(stop);
        StopChanged();
    }

    private void ReverseStops()
    {
        var reversed = Stops.Reverse().Select(s => s.ToDefinition()).ToList();
        SetStops(reversed);
    }

    // ---------------- Route & phases ----------------

    /// <summary>Recomputes legs from the stops with default phases. Keeps margins of phases whose names survive.</summary>
    private void RebuildRoute(bool keepPhaseTuning)
    {
        if (_suspend || _map is null || Stops.Count < 2) return;
        var stops = Stops.Select(s => _map[s.NodeKey]).ToList();
        var segments = MissionPlanner.BuildRoute(_map, stops);
        var (phases, settings) = MissionPlanner.DefaultPhases(segments, stops, UseAerobraking);

        if (keepPhaseTuning)
        {
            var previous = Phases.ToDictionary(p => p.Name, p => p.ToDefinition());
            phases = phases.Select(p => previous.TryGetValue(p.Name, out var old) ? p with { MarginPercent = old.MarginPercent, ExtraDv = old.ExtraDv } : p).ToList();
        }
        settings = settings.Select(s => s with { IncludePlaneChange = IncludePlaneChanges }).ToList();
        Apply(segments, phases, settings);
    }

    /// <summary>Reloads map values (after an edit) without touching the user's phase and leg choices.</summary>
    private void ReloadMap()
    {
        _map = _maps.GetMap();
        SyncNodes();
        RefreshLegs();
        OnPropertyChanged(nameof(Map));
        OnPropertyChanged(nameof(EditedCount));
        OnPropertyChanged(nameof(EditedText));
    }

    /// <summary>Rebuilds the leg rows from the current stops and map, keeping phases and per-leg choices.</summary>
    private void RefreshLegs()
    {
        if (_suspend || _map is null || Stops.Count < 2) return;
        var phases = Phases.Select(p => p.ToDefinition()).ToList();
        var settings = Legs.Select(l => l.Setting(Phases)).ToList();
        var segments = MissionPlanner.BuildRoute(_map, Stops.Select(s => _map[s.NodeKey]).ToList());
        Apply(segments, phases, settings);
    }

    private void Apply(IReadOnlyList<RouteSegment> segments, IReadOnlyList<PhaseDefinition> phases, IReadOnlyList<LegSetting> settings)
    {
        Phases.Clear();
        for (var i = 0; i < phases.Count; i++) Phases.Add(new PhaseViewModel(phases[i], i, Recalculate));

        Legs.Clear();
        var number = 0;
        foreach (var segment in segments)
        {
            for (var j = 0; j < segment.Legs.Count; j++)
            {
                var stop = Stops[segment.ToStop];
                var header = j == 0
                    ? $"To stop {segment.ToStop + 1}: {_map![stop.NodeKey].DisplayName}" + (string.IsNullOrWhiteSpace(stop.Note) ? "" : $"  —  {stop.Note}")
                    : null;
                var setting = number < settings.Count ? settings[number] : new LegSetting(0, UseAerobraking, IncludePlaneChanges);
                var phase = Phases[Math.Clamp(setting.PhaseIndex, 0, Phases.Count - 1)];
                // Edits rebuild the leg rows, so defer them until the editing TextBox has finished losing focus.
                Legs.Add(new LegRowViewModel(segment.Legs[j], number + 1, header, phase, setting, Recalculate,
                    (key, dv) => System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(() => EditEdge(key, dv))));
                number++;
            }
        }

        OnPropertyChanged(nameof(RouteSummary));
        OnPropertyChanged(nameof(RouteEdgeKeys));
        OnPropertyChanged(nameof(StopKeys));
        Recalculate();
    }

    private void Recalculate()
    {
        var legs = Legs.Select(l => l.Leg).ToList();
        var settings = Legs.Select(l => l.Setting(Phases)).ToList();
        var summaries = MissionPlanner.Summarize(Phases.Select(p => p.ToDefinition()).ToList(), legs, settings);
        var missionTotal = summaries.Sum(s => s.TotalDv);
        for (var i = 0; i < Phases.Count; i++) Phases[i].Update(summaries[i], i, Phases.Count, missionTotal);

        OnPropertyChanged(nameof(PhasesTopDown));
        OnPropertyChanged(nameof(TotalRequiredText));
        OnPropertyChanged(nameof(TotalMarginText));
        OnPropertyChanged(nameof(TotalText));
    }

    private void AddPhase()
    {
        Phases.Add(new PhaseViewModel(new PhaseDefinition($"Phase {Phases.Count + 1}"), Phases.Count, Recalculate));
        Recalculate();
    }

    private void RemovePhase(PhaseViewModel phase)
    {
        var index = Phases.IndexOf(phase);
        if (index < 0 || Phases.Count <= 1) return;
        var fallback = Phases[index > 0 ? index - 1 : 1];
        foreach (var leg in Legs.Where(l => l.Phase == phase)) leg.Phase = fallback;
        Phases.Remove(phase);
        Recalculate();
    }

    private void ResetMapEdits()
    {
        _maps.ClearOverrides();
        ReloadMap();
    }

    // ---------------- Saved missions ----------------

    private void Save()
    {
        var definition = new MissionDefinition(
            Stops.Select(s => s.ToDefinition()).ToList(),
            Phases.Select(p => p.ToDefinition()).ToList(),
            Legs.Select(l => l.Setting(Phases)).ToList());
        _missions.Add(MissionName.Trim(), definition);
        MissionStatus = $"Saved \"{MissionName.Trim()}\".";
        MissionName = "";
        RefreshMissions();
    }

    private void Load()
    {
        if (_map is null || SelectedMission is not { } mission) return;
        var d = mission.Definition;
        var missing = d.Stops.Count(s => !_map.TryGet(s.NodeKey, out _));
        if (missing > 0)
        {
            MissionStatus = $"Can't load \"{mission.Name}\": {missing} of its stops use a custom location that was deleted.";
            return;
        }
        MissionStatus = null;

        _suspend = true;
        Stops.Clear();
        foreach (var s in d.Stops) Stops.Add(new StopViewModel(s.NodeKey, s.Note, StopChanged, RefreshLegs));
        for (var i = 0; i < Stops.Count; i++) Stops[i].SetPosition(i, Stops.Count);
        _suspend = false;

        var segments = MissionPlanner.BuildRoute(_map, Stops.Select(s => _map[s.NodeKey]).ToList());
        if (MissionPlanner.AllLegs(segments).Count == d.Legs.Count && d.Phases.Count > 0)
            Apply(segments, d.Phases, d.Legs);
        else
            RebuildRoute(keepPhaseTuning: false); // map changed since saving; fall back to defaults
        MissionName = mission.Name;
    }

    private void Delete()
    {
        if (SelectedMission is not { } mission) return;
        _missions.Delete(mission.Id);
        RefreshMissions();
    }

    private void RefreshMissions()
    {
        SavedMissions.Clear();
        foreach (var m in _missions.GetAll()) SavedMissions.Add(m);
    }
}
