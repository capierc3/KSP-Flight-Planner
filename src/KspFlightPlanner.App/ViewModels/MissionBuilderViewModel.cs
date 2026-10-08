using System.Collections.ObjectModel;
using System.Globalization;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;
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

    /// <param name="scaleTotal">Δv that fills the full diagram height: the mission total, or the design's if larger.</param>
    /// <param name="sideBySide">A design is shown next to the phases, so the bars are narrower.</param>
    internal void Update(PhaseSummary summary, int index, int count, double scaleTotal, bool sideBySide)
    {
        RequiredDv = summary.RequiredDv;
        MarginDv = summary.MarginDv;
        BarHeight = sideBySide ? RocketDiagram.Height(TotalDv, scaleTotal)
            : 48 + 300 * (scaleTotal > 0 ? TotalDv / scaleTotal : 1.0 / count);
        BarWidth = sideBySide ? 104 : Math.Max(90, 190 - index * 26);
        foreach (var p in new[] { nameof(RequiredDv), nameof(MarginDv), nameof(TotalDv), nameof(RequiredText),
                     nameof(MarginText), nameof(TotalText), nameof(BarHeight), nameof(BarWidth) })
            OnPropertyChanged(p);
    }
}

/// <summary>Sizes for the rocket diagram when a design is compared: both columns share one Δv scale.</summary>
internal static class RocketDiagram
{
    public static double Height(double deltaV, double scaleTotal) => 30 + 200 * (scaleTotal > 0 ? deltaV / scaleTotal : 0);
}

/// <summary>A stage of the chosen design in the rocket diagram, coloured like the phase it mostly burns for.</summary>
public sealed record StageBlock(string Label, string DeltaVText, string Color, double BarHeight, string ToolTip);

/// <summary>One row of the design check: a phase, what it needs and how well the stages cover it.</summary>
public sealed record PhaseCheckRow(string Name, string Color, string Needed, string Detail, Coverage Status)
{
    public string Mark => Status switch { Coverage.Comfortable => "✓", Coverage.InsideMargin => "✓", _ => "✗" };
}

/// <summary>An entry in the design picker; <see cref="Design"/> is null for "no design".</summary>
public sealed record DesignChoice(DesignEntry? Design, string Title)
{
    public string Path => Design?.File.Path ?? "";
    public override string ToString() => Title;
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
    private readonly CustomLocationRepository _locations;
    private readonly PlannerEvents _events;
    private readonly DesignLibrary? _designs;
    private DeltaVMap? _map;
    private DesignChoice? _selectedDesign;
    private DesignCheckResult? _check;

    private bool _useAerobraking = true;
    private bool _includePlaneChanges;
    private bool _suspend;

    public MissionBuilderViewModel(DvMapRepository maps, CustomLocationRepository locations,
        PlannerEvents events, DesignLibrary? designs = null)
    {
        _designs = designs;
        if (designs is not null) designs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DesignLibrary.Entries)) RefreshDesignChoices();
        };
        RefreshDesignChoices();
        _maps = maps;
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

        if (_map is not null)
        {
            SyncNodes();
            SetStops([new("kerbin.surface"), new("mun.surface"), new("kerbin.surface")]);
        }
    }

    public ObservableCollection<DvMapNode> Nodes { get; } = [];
    public ObservableCollection<StopViewModel> Stops { get; } = [];
    public ObservableCollection<PhaseViewModel> Phases { get; } = [];
    public ObservableCollection<LegRowViewModel> Legs { get; } = [];

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

    public string RouteSummary => _map is null ? "" : string.Join(" → ", Stops.Select(s => _map[s.NodeKey].DisplayName));

    // ---------------- Checking a saved design ----------------

    /// <summary>"No design" plus every saved design in the linked save.</summary>
    public ObservableCollection<DesignChoice> DesignChoices { get; } = [];

    /// <summary>The design to check against the mission; refreshed (by file) when KSP saves it again.</summary>
    public DesignChoice? SelectedDesign
    {
        get => _selectedDesign;
        set { if (SetProperty(ref _selectedDesign, value)) Recalculate(); }
    }

    public bool HasDesign => _check is not null;

    /// <summary>Stage blocks for the design column, last stage on top (like the phases).</summary>
    public IReadOnlyList<StageBlock> StageBlocksTopDown { get; private set; } = [];

    public IReadOnlyList<PhaseCheckRow> PhaseChecks { get; private set; } = [];

    /// <summary>
    /// Margins are wiggle room, not a requirement: a design that flies the route but not every margin is still enough,
    /// just with less room for error.
    /// </summary>
    public string DesignVerdict => _check switch
    {
        null => "",
        { Status: Coverage.Comfortable } c => $"Enough — {Format.Speed0(c.SpareOverMargins)} to spare beyond your margins",
        { Status: Coverage.InsideMargin } c =>
            $"Enough — {Format.Speed0(c.SpareOverRequired)} to spare, inside your {Format.Speed0(c.MarginDeltaV)} of margins",
        var c => $"Not enough — short by {Format.Speed0(c.Phases.Sum(p => p.Shortfall))} before any margin",
    };

    /// <summary>Comfortable / InsideMargin / Short, for the verdict's colour.</summary>
    public Coverage? DesignStatus => _check?.Status;

    public string DesignDeltaVText => _check is { } c ? Format.Speed0(c.DesignDeltaV) : "";

    public string LaunchTwrText => _check is not { } c ? ""
        : c.CanLiftOff ? $"{c.LaunchTwr:0.00}" : $"{c.LaunchTwr:0.00} — can't lift off";

    public bool CanLiftOff => _check?.CanLiftOff != false;

    public string DesignHint => _designs is null || DesignChoices.Count <= 1
        ? "Link a KSP save (Tools › Settings) to check one of your saved designs."
        : "Stages fill the phases in order; vacuum Δv (the Δv map already counts atmospheric losses).";

    private void RefreshDesignChoices()
    {
        var keep = _selectedDesign?.Path;
        DesignChoices.Clear();
        DesignChoices.Add(new DesignChoice(null, "— No design —"));
        foreach (var d in _designs?.Entries ?? [])
            if (d.Report is { Stages.Count: > 0 }) DesignChoices.Add(new DesignChoice(d, d.Title));
        // Same file again (KSP re-saved it): keep it selected with its new numbers.
        _selectedDesign = DesignChoices.FirstOrDefault(c => c.Path == keep) ?? DesignChoices[0];
        OnPropertyChanged(nameof(SelectedDesign));
        OnPropertyChanged(nameof(DesignHint));
        if (Phases.Count > 0) Recalculate();
    }

    private void CheckDesign(double missionTotal)
    {
        _check = SelectedDesign?.Design?.Report is { } report
            ? DesignCheck.Check(Phases.Select(p => new PhaseNeed(p.Name, p.RequiredDv, p.MarginDv)).ToList(), report)
            : null;

        if (_check is { } c && SelectedDesign?.Design?.Report is { } design)
        {
            var scale = Math.Max(missionTotal, c.DesignDeltaV);
            StageBlocksTopDown = c.Stages.Select(s =>
            {
                var main = s.Phases.OrderByDescending(p => p.DeltaV).Select(p => (int?)p.Phase).FirstOrDefault();
                var label = s.Spare > 0.5 ? $"S{s.Stage} · spare {s.Spare:N0}" : $"Stage {s.Stage}";
                var tip = string.Join("\n", s.Phases.Select(p => $"{Phases[p.Phase].Name}: {Format.Speed0(p.DeltaV)}")
                    .Append(s.Spare > 0.5 ? $"Spare: {Format.Speed0(s.Spare)}" : null).OfType<string>());
                return new StageBlock(label, Format.Speed0(s.DeltaV), main is { } m ? Phases[m].Color : "#90A4AE",
                    RocketDiagram.Height(s.DeltaV, scale), tip);
            }).Reverse().ToList();
            PhaseChecks = c.Phases.Select((p, i) =>
            {
                var stages = $"stage{(p.Stages.Distinct().Count() == 1 ? "" : "s")} {string.Join(", ", p.Stages.Distinct())}";
                var detail = p.Required + p.Margin <= 0 ? "nothing needed"
                    : p.Status switch
                    {
                        Coverage.Comfortable => stages,
                        Coverage.InsideMargin => $"{stages} · uses part of its margin",
                        _ => $"short {Format.Speed0(p.Shortfall)}",
                    };
                return new PhaseCheckRow(p.Name, Phases[i].Color, Format.Speed0(p.Required + p.Margin), detail, p.Status);
            }).ToList();
        }
        else
        {
            StageBlocksTopDown = [];
            PhaseChecks = [];
        }
        foreach (var p in new[] { nameof(HasDesign), nameof(StageBlocksTopDown), nameof(PhaseChecks), nameof(DesignVerdict),
                     nameof(DesignStatus), nameof(DesignDeltaVText), nameof(LaunchTwrText), nameof(CanLiftOff) })
            OnPropertyChanged(p);
    }

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
        var design = SelectedDesign?.Design?.Report;
        var scale = design is null ? missionTotal : Math.Max(missionTotal, design.TotalDeltaVVacuum);
        for (var i = 0; i < Phases.Count; i++) Phases[i].Update(summaries[i], i, Phases.Count, scale, design is not null);
        CheckDesign(missionTotal);

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
}
