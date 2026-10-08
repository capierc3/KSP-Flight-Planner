using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App.ViewModels;

/// <summary>Something in orbit that can be met, or start the trip: a vessel in the linked save or a placed custom location.</summary>
public sealed record CraftChoice(string Key, string Name, string Detail, CelestialBody Body, OrbitalElements Orbit, SavedVessel? Vessel)
{
    public string Title => $"{Name}  ·  {Detail}";
    public override string ToString() => Title;
}

/// <summary>A launch opportunity as a row in the list.</summary>
public sealed record LaunchRow(string When, string Detail);

/// <summary>A phasing choice for a leg's picker.</summary>
public sealed record PhasingRow(PhasingChoice? Choice, string Title)
{
    public override string ToString() => Title;
}

/// <summary>One burn (or the lift-off): what to do by feel, and the exact node values.</summary>
public sealed record StepRow(string Number, string Title, string When, string Countdown, string DeltaV, string Feel, IReadOnlyList<Fact> Exact);

/// <summary>One leg of the route: the burns, the phasing picker and the rule-of-thumb summary.</summary>
public sealed class LegRow : ObservableObject
{
    private readonly Action<int, PhasingChoice?> _phasingChanged;
    private PhasingRow? _selectedPhasing;

    public LegRow(int index, string title, string guide, IReadOnlyList<PhasingRow> phasingRows, PhasingRow? selected, IReadOnlyList<StepRow> steps,
        string arrival, string total, Action<int, PhasingChoice?> phasingChanged)
    {
        Index = index;
        Title = title;
        Guide = guide;
        PhasingRows = phasingRows;
        _selectedPhasing = selected;
        Steps = steps;
        Arrival = arrival;
        Total = total;
        _phasingChanged = phasingChanged;
    }

    public int Index { get; }
    public string Title { get; }
    public string Guide { get; }
    public IReadOnlyList<PhasingRow> PhasingRows { get; }
    public bool HasPhasingChoice => PhasingRows.Count > 1;
    public IReadOnlyList<StepRow> Steps { get; }

    /// <summary>The flight plan's number of this leg's first burn.</summary>
    public int FirstStep => Steps.Count > 0 && int.TryParse(Steps[0].Number, out var n) ? n : 1;
    public string Arrival { get; }
    public string Total { get; }

    public PhasingRow? SelectedPhasing
    {
        get => _selectedPhasing;
        set
        {
            if (value is null || !SetProperty(ref _selectedPhasing, value)) return;
            _phasingChanged(Index, value.Choice);
        }
    }

    public override string ToString() => Title;
}

/// <summary>
/// A mission leg the Rendezvous tab is planning: where it starts (a KSC launch or a craft) and the craft it meets come
/// from the leg, and every plan made is saved back into the mission through <see cref="Save"/>.
/// </summary>
/// <param name="Owner">The mission stop the leg arrives at, to find the link again when the route changes.</param>
/// <param name="ParkingAltitudeM">The low orbit a launch climbs to (the map's, so the leg's launch step matches).</param>
public sealed record RendezvousLegLink(object Owner, string Title, RendezvousSpan Span, double ParkingAltitudeM, RendezvousLeg? Choice,
    Action<RendezvousLeg> Save)
{
    /// <summary>When the mission reaches the start of the leg, if known: a new plan starts then.</summary>
    public double? EarliestUt { get; init; }
}

/// <summary>
/// The Rendezvous tab: meet one or more craft in orbit (relays to repair, debris to deorbit), starting from a launch at
/// KSC or from a craft already in orbit. Burns are shown as exact maneuver-node values or as instructions to fly by feel.
/// </summary>
public sealed class RendezvousViewModel : ObservableObject
{
    private readonly SaveSync _sync;
    private readonly CustomLocationRepository _locations;
    private readonly DesignLibrary? _designs;
    private readonly FlightRepository? _flights;
    private readonly PlannerEvents _events;
    private readonly GameCalendar _calendar = GameCalendar.Kerbin;
    private readonly LaunchSite _site = LaunchSite.Ksc;

    private bool _startFromLaunch = true;
    private CraftChoice? _selectedChaser;
    private CraftChoice? _targetToAdd;
    private string _parkingAltitudeText = "80";
    private string _ascentMinutesText = "6";
    private string _downrangeText = "30";
    private bool _followSaveTime = true;
    private int _leadMinutes = 5;
    private int _startYear = 1;
    private int _startDay = 1;
    private string _startTimeText = "0:00";
    private int _stayMinutes = 60;
    private bool _deorbit;
    private string _deorbitAltitudeText = "30";
    private string? _deorbitBody = "Kerbin";
    private bool _showExact;
    private DesignChoice? _selectedDesign;

    private int? _launchChoice;
    private readonly List<PhasingChoice?> _phasing = [];
    private readonly List<InterceptPoint?> _intercepts = [];

    private RendezvousRequest? _request;
    private RendezvousPlan? _plan;
    private int _version;
    private int _busyCount;
    private string? _errorText;
    private bool _suspend;
    private int _plotLeg;
    private bool _showDiagram = true;
    private RendezvousLegLink? _leg;

    public RendezvousViewModel(SaveSync sync, CustomLocationRepository locations, PlannerEvents events, DesignLibrary? designs = null,
        FlightRepository? flights = null)
    {
        _sync = sync;
        _locations = locations;
        _designs = designs;
        _flights = flights;
        _events = events;
        sync.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SaveSync.Save)) RebuildCrafts();
        };
        sync.Parts.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PartData.Catalog)) RefreshCapacity();
        };
        sync.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SaveSync.Live)) LiveChanged();
        };
        sync.Link.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(KspLink.IsConnected) or nameof(KspLink.InFlight)) LiveChanged();
        };
        events.LocationsChanged += _ => RebuildCrafts();
        if (designs is not null) designs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DesignLibrary.Entries)) RefreshDesignChoices();
        };

        AddTargetCommand = new RelayCommand(AddTarget, () => TargetToAdd is not null);
        RemoveTargetCommand = new RelayCommand<CraftChoice>(t => { Targets.Remove(t); TargetsChanged(); });
        MoveTargetUpCommand = new RelayCommand<CraftChoice>(MoveUp, t => Targets.IndexOf(t) > 0);
        UseSaveTimeCommand = new RelayCommand(() => FollowSaveTime = true, () => _sync.HasSave);
        ResetInterceptCommand = new RelayCommand(ResetIntercept, () => PlotLegPlan?.IsCustomIntercept == true);
        UseActiveVesselCommand = new RelayCommand(UseActiveVessel, () => CraftFor(ActiveKey) is not null);
        AddGameTargetCommand = new RelayCommand(AddGameTarget, () => AvailableTargets.Any(c => c.Key == TargetKey));
        FlyCommand = new RelayCommand(Fly, () => _plan is not null && _flights is not null);
        StopEditingLegCommand = new RelayCommand(StopEditingLeg, () => IsEditingLeg);

        RefreshDesignChoices();
        RebuildCrafts();
    }

    // ---------------- Leg mode ----------------

    public RelayCommand StopEditingLegCommand { get; }

    /// <summary>The mission leg being planned; null when the tab is used on its own.</summary>
    public RendezvousLegLink? Leg => _leg;

    public bool IsEditingLeg => _leg is not null;

    /// <summary>The start, targets and what happens afterwards can be changed: not while they come from a mission leg.</summary>
    public bool IsRouteEditable => _leg is null;

    public string LegTitle => _leg?.Title ?? "";

    /// <summary>
    /// Plans a mission leg: start and target from the leg, and the start time and picks it was saved with (or the game's
    /// time and the planner's picks). The start time is fixed rather than following the game, so the plan stays put; every
    /// plan made afterwards is saved into the mission.
    /// </summary>
    public void EditLeg(RendezvousLegLink link)
    {
        var span = link.Span;
        var choice = link.Choice is { } c && c.TargetLocationId == span.TargetLocationId ? c : null;
        _leg = null; // don't save anything while it's being set up
        _suspend = true;
        _startFromLaunch = span.IsLaunch;
        if (span.ChaserLocationId is { } chaser) _selectedChaser = CraftForLocation(chaser);
        Targets.Clear();
        if (CraftForLocation(span.TargetLocationId) is { } target) Targets.Add(target);
        _deorbit = false;
        _parkingAltitudeText = Km(link.ParkingAltitudeM);
        _followSaveTime = false;
        // The start is kept to the minute: round the arrival up so the leg never starts before the mission gets there.
        SetStartFields(choice?.StartUt ?? (link.EarliestUt is { } arrival ? Math.Ceiling(arrival / 60) * 60 : (double?)null)
            ?? (_sync.Save is { } save ? save.Ut + _leadMinutes * 60 : _calendar.ToUniversalTime(StartYear, StartDay)));
        _launchChoice = choice?.LaunchIndex;
        _phasing.Clear();
        _phasing.Add(choice?.Phasing);
        _intercepts.Clear();
        _intercepts.Add(choice?.Intercept);
        _suspend = false;

        _leg = link;
        foreach (var name in new[] { nameof(StartFromLaunch), nameof(StartFromCraft), nameof(SelectedChaser), nameof(Deorbit), nameof(ParkingAltitudeText),
                     nameof(FollowSaveTime), nameof(ManualStart), nameof(DeorbitLabel), nameof(CapacityHint) })
            OnPropertyChanged(name);
        foreach (var name in LegProperties) OnPropertyChanged(name);
        RefreshAvailableTargets();
        if (Targets.Count == 0) ErrorText = "The craft this leg meets isn't in the list: check it's placed on the Locations tab (or in the linked save).";
        else Recalculate();
    }

    /// <summary>Back to using the tab on its own; the mission keeps the plan last made.</summary>
    public void StopEditingLeg()
    {
        _leg = null;
        foreach (var name in LegProperties) OnPropertyChanged(name);
    }

    private static readonly string[] LegProperties = [nameof(Leg), nameof(IsEditingLeg), nameof(IsRouteEditable), nameof(LegTitle)];

    /// <summary>A custom location in the craft list: the save's vessel while it's linked to this save, else the location itself.</summary>
    private CraftChoice? CraftForLocation(long id)
    {
        var location = _locations.Get(id);
        var vesselKey = location is { IsLinked: true } && location.KspSave == _sync.SaveName ? $"vessel:{location.KspVesselId}" : null;
        return Crafts.FirstOrDefault(c => c.Key == vesselKey) ?? Crafts.FirstOrDefault(c => c.Key == $"location:{id}");
    }

    /// <summary>Saves the plan into the mission leg being planned (the first leg of the plan is the mission leg).</summary>
    private void SaveLeg(RendezvousRequest request, RendezvousPlan plan)
    {
        if (_leg is not { } link || plan.Legs.Count == 0) return;
        var leg = plan.Legs[0];
        link.Save(new RendezvousLeg(link.Span.TargetLocationId, request.EarliestUt, leg.ArrivalUt, leg.TotalDv, _launchChoice,
            _phasing.Count > 0 ? _phasing[0] : null, _intercepts.Count > 0 ? _intercepts[0] : null));
    }

    // ---------------- Inputs ----------------

    /// <summary>Every craft and placed location in orbit, from the linked save and the Locations tab.</summary>
    public ObservableCollection<CraftChoice> Crafts { get; } = [];

    /// <summary>Crafts that can be added as the next target: same body as the start, not already on the route.</summary>
    public ObservableCollection<CraftChoice> AvailableTargets { get; } = [];

    public ObservableCollection<CraftChoice> Targets { get; } = [];

    public RelayCommand AddTargetCommand { get; }
    public RelayCommand<CraftChoice> RemoveTargetCommand { get; }
    public RelayCommand<CraftChoice> MoveTargetUpCommand { get; }
    public RelayCommand UseSaveTimeCommand { get; }
    public RelayCommand ResetInterceptCommand { get; }
    public RelayCommand UseActiveVesselCommand { get; }
    public RelayCommand AddGameTargetCommand { get; }
    public RelayCommand FlyCommand { get; }

    public bool StartFromLaunch
    {
        get => _startFromLaunch;
        set
        {
            if (!SetProperty(ref _startFromLaunch, value)) return;
            OnPropertyChanged(nameof(StartFromCraft));
            StartChanged();
        }
    }

    public bool StartFromCraft
    {
        get => !_startFromLaunch;
        set => StartFromLaunch = !value;
    }

    public CraftChoice? SelectedChaser
    {
        get => _selectedChaser;
        set
        {
            if (!SetProperty(ref _selectedChaser, value)) return;
            StartChanged();
        }
    }

    public CraftChoice? TargetToAdd
    {
        get => _targetToAdd;
        set => SetProperty(ref _targetToAdd, value);
    }

    public string ParkingAltitudeText
    {
        get => _parkingAltitudeText;
        set { if (SetProperty(ref _parkingAltitudeText, value)) InputsChanged(); }
    }

    public string AscentMinutesText
    {
        get => _ascentMinutesText;
        set { if (SetProperty(ref _ascentMinutesText, value)) InputsChanged(); }
    }

    public string DownrangeText
    {
        get => _downrangeText;
        set { if (SetProperty(ref _downrangeText, value)) InputsChanged(); }
    }

    /// <summary>Plan from the game's time: the live game while connected, else the save (updates whenever KSP saves).</summary>
    public bool FollowSaveTime
    {
        get => _followSaveTime && _sync.HasSave;
        set
        {
            if (value && !_sync.HasSave) return;
            if (!value && _sync.Save is { } save) SetStartFields(save.Ut + _leadMinutes * 60);
            _followSaveTime = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ManualStart));
            InputsChanged();
        }
    }

    public bool ManualStart => !FollowSaveTime;

    /// <summary>Minutes after the save's time before the first burn (time to switch to the craft and set up the node).</summary>
    public int LeadMinutes
    {
        get => _leadMinutes;
        set { if (SetProperty(ref _leadMinutes, Math.Max(0, value))) InputsChanged(); }
    }

    public int StartYear
    {
        get => _startYear;
        set { if (SetProperty(ref _startYear, Math.Max(1, value))) InputsChanged(); }
    }

    public int StartDay
    {
        get => _startDay;
        set { if (SetProperty(ref _startDay, Math.Clamp(value, 1, _calendar.DaysPerYear))) InputsChanged(); }
    }

    /// <summary>Time of day, "h:mm" or "h:mm:ss".</summary>
    public string StartTimeText
    {
        get => _startTimeText;
        set { if (SetProperty(ref _startTimeText, value)) InputsChanged(); }
    }

    public string SaveTimeText => _sync.Save is not { } save ? "No save linked — enter a date."
        : _sync.IsLive ? $"Game time: {_calendar.FormatDate(save.Ut)} · live from KSP" : $"Save time: {_calendar.FormatDate(save.Ut)}";

    // ---------------- Live game ----------------

    /// <summary>The live link is up, so the game-related buttons are shown.</summary>
    public bool IsLinked => _sync.Link.IsConnected;

    /// <summary>What the player is doing in KSP: the craft flown and the target.</summary>
    public string GameText
    {
        get
        {
            var link = _sync.Link;
            if (!link.IsConnected) return "";
            if (!link.InFlight || link.Game?.Vessels is not { } vessels) return "In KSP: not flying a craft. Switch to one to use it here.";
            var active = vessels.FirstOrDefault(v => v.IsActive)?.Name ?? "nothing";
            var target = vessels.FirstOrDefault(v => v.IsTarget)?.Name ?? link.Game.TargetBody;
            return $"In KSP: flying {active}" + (target is null ? " · no target set" : $" · target {target}");
        }
    }

    /// <summary>The craft list's key for the vessel being flown in KSP (null when not flying one).</summary>
    private string? ActiveKey => KeyFor(_sync.ActiveCraft);

    /// <summary>The craft list's key for the vessel targeted in KSP.</summary>
    private string? TargetKey => KeyFor(_sync.TargetCraft);

    /// <summary>A craft's key in the list: its save vessel's, or by name for one not in the save yet.</summary>
    private static string? KeyFor(FlightCraft? craft) =>
        craft is null ? null : craft.SaveVesselId is { } id ? $"vessel:{id}" : $"live:{craft.Name}";

    private CraftChoice? CraftFor(string? key) => key is null ? null : Crafts.FirstOrDefault(c => c.Key == key);

    private void LiveChanged()
    {
        foreach (var name in new[] { nameof(IsLinked), nameof(GameText), nameof(SaveTimeText) }) OnPropertyChanged(name);
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Plan from the craft being flown in KSP.</summary>
    private void UseActiveVessel()
    {
        if (CraftFor(ActiveKey) is not { } craft) return;
        _suspend = true;
        _selectedChaser = craft;
        _suspend = false;
        OnPropertyChanged(nameof(SelectedChaser));
        if (StartFromLaunch) StartFromLaunch = false; // re-plans
        else StartChanged();
    }

    /// <summary>Adds the craft targeted in KSP as the next target.</summary>
    private void AddGameTarget()
    {
        if (AvailableTargets.FirstOrDefault(c => c.Key == TargetKey) is not { } craft) return;
        Targets.Add(craft);
        TargetsChanged();
    }

    /// <summary>Hands the plan to the Flight tab, where its burns are sent to the game and re-planned as it's flown.</summary>
    private void Fly()
    {
        if (_request is not { } request || _plan is not { } plan) return;
        FlightCraft? craft = null;
        if (!StartFromLaunch && SelectedChaser is { } chaser)
            craft = new FlightCraft(chaser.Name, chaser.Vessel?.Id);
        var flight = new Flight
        {
            Name = $"Meet {string.Join(", ", Targets.Select(t => t.Name))}",
            Kind = FlightKind.Rendezvous,
            BodyName = request.Body.Name,
            Craft = craft,
            StartOrbit = plan.Launch?.ParkingOrbit ?? request.Chaser?.Orbit,
            Burns = FlightViewModel.RendezvousBurns(plan),
            PlannedUt = request.EarliestUt,
            LeadS = LeadMinutes * 60,
            Note = plan.Launch is { } launch
                ? $"Starts with a launch at {_calendar.FormatDate(launch.LiftoffUt)}, heading {launch.HeadingDeg:0}°. Once in orbit, use the craft you're " +
                  "flying to re-plan from its real orbit."
                : null,
            Targets = plan.Legs.Select((leg, i) => new FlightTarget(leg.Target.Name, Targets[i].Vessel?.Id, leg.Target.Orbit, leg.ArrivalUt)).ToList(),
            StayS = StayMinutes * 60,
            DeorbitPeriapsisAltitudeM = request.DeorbitPeriapsisAltitudeM,
        };
        _events.RequestOpenFlight(_flights!.Add(flight));
    }

    /// <summary>Time spent at each target (repairs, refuelling) before heading to the next.</summary>
    public int StayMinutes
    {
        get => _stayMinutes;
        set { if (SetProperty(ref _stayMinutes, Math.Max(0, value))) InputsChanged(); }
    }

    public bool Deorbit
    {
        get => _deorbit;
        set { if (SetProperty(ref _deorbit, value)) InputsChanged(); }
    }

    public string DeorbitAltitudeText
    {
        get => _deorbitAltitudeText;
        set { if (SetProperty(ref _deorbitAltitudeText, value)) InputsChanged(); }
    }

    public string DeorbitLabel => Targets.Count > 0 ? $"Deorbit {Targets[^1].Name} at the end (docked)" : "Deorbit the last target at the end (docked)";

    /// <summary>Exact maneuver-node values (true) or instructions to fly by feel (false).</summary>
    public bool ShowExact
    {
        get => _showExact;
        set
        {
            if (!SetProperty(ref _showExact, value)) return;
            OnPropertyChanged(nameof(ShowFeel));
        }
    }

    public bool ShowFeel
    {
        get => !_showExact;
        set => ShowExact = !value;
    }

    // ---------------- State ----------------

    public bool IsBusy => _busyCount > 0;

    public string? ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    public bool HasPlan => _plan is not null;
    public bool HasNoPlan => _plan is null;

    public string EmptyText => Targets.Count == 0
        ? Crafts.Count == 0
            ? "Nothing in orbit to meet yet. Link your KSP save (Tools › Settings) or place a custom location on the Locations tab."
            : "Add a target (left) to plan meeting it."
        : "";

    // ---------------- Outputs ----------------

    public ObservableCollection<LaunchRow> LaunchRows { get; } = [];
    public bool HasLaunch => LaunchRows.Count > 0;

    /// <summary>The launch flown; picking another row re-plans from it.</summary>
    public int LaunchIndex
    {
        get => _plan?.LaunchIndex ?? -1;
        set
        {
            if (_suspend || value < 0 || value == _plan?.LaunchIndex) return;
            _launchChoice = value;
            _phasing.Clear();
            _intercepts.Clear();
            Recalculate();
        }
    }

    public StepRow? LaunchStep { get; private set; }

    public ObservableCollection<LegRow> Legs { get; } = [];

    public StepRow? DeorbitStep { get; private set; }
    public bool HasDeorbit => DeorbitStep is not null;

    public ObservableCollection<BudgetLine> BudgetLines { get; } = [];
    public string BudgetTotalText => BudgetLines.Count > 0 ? Format.Speed0(MissionBudget.Total(BudgetLines)) : "—";

    // Capacity: a saved design (launch) or the chosen vessel's remaining Δv.

    public ObservableCollection<DesignChoice> DesignChoices { get; } = [];

    public DesignChoice? SelectedDesign
    {
        get => _selectedDesign;
        set { if (SetProperty(ref _selectedDesign, value)) RefreshCapacity(); }
    }

    public string? CapacityVerdict { get; private set; }
    public Coverage? CapacityStatus { get; private set; }
    public bool HasCapacity => CapacityVerdict is not null;
    public IReadOnlyList<Fact> CapacityLines { get; private set; } = [];

    public string CapacityHint => StartFromLaunch
        ? _designs is null || DesignChoices.Count <= 1 ? "Link a KSP save (Tools › Settings) to check one of your saved designs." : "Vacuum Δv of each stage, poured into launch, then each leg."
        : SelectedChaser?.Vessel is null ? "Pick a vessel from your save to compare with what's left in its tanks." : "What's left in its tanks, as on the Vessels tab.";

    // Porkchop for one leg.

    public ObservableCollection<LegRow> PlotLegs { get; } = [];

    public LegRow? SelectedPlotLeg
    {
        get => PlotLegs.FirstOrDefault(l => l.Index == _plotLeg);
        set
        {
            if (value is null || value.Index == _plotLeg) return;
            _plotLeg = value.Index;
            RefreshPlot();
        }
    }

    private LegPlan? PlotLegPlan => _plan is { } p && _plotLeg < p.Legs.Count ? p.Legs[_plotLeg] : null;

    /// <summary>The leg picked in the right-hand panel, for the orbit diagram.</summary>
    public LegPlan? SelectedLegPlan => PlotLegPlan;
    public CelestialBody? DiagramBody => _request?.Body;
    public int DiagramFirstStep => SelectedPlotLeg?.FirstStep ?? 1;

    /// <summary>The right-hand panel shows the orbit diagram (true) or the porkchop plot (false).</summary>
    public bool ShowDiagram
    {
        get => _showDiagram;
        set
        {
            if (!SetProperty(ref _showDiagram, value)) return;
            OnPropertyChanged(nameof(ShowPorkchop));
        }
    }

    public bool ShowPorkchop
    {
        get => !_showDiagram;
        set => ShowDiagram = !value;
    }

    public PorkchopPlot? CurrentPlot => PlotLegPlan?.Porkchop;
    public IPorkchopPoint? CurrentPlotSelection => PlotLegPlan?.Intercept;
    public bool HasPlot => CurrentPlot is not null;
    public bool IsCustomIntercept => PlotLegPlan?.IsCustomIntercept == true;

    public string PlotNote => PlotLegs.Count == 0 ? "Each leg's orbits and transfer show here once there is a plan." : "";

    public string PorkchopStartLabel => CurrentPlot is { } p ? _calendar.FormatDate(p.DepartureStartUt) : "";
    public string PorkchopEndLabel => CurrentPlot is { } p ? _calendar.FormatDate(p.DepartureEndUt) : "";
    public string PorkchopMinFlightLabel => CurrentPlot is { } p ? Minutes(p.FlightTimeMinS) : "";
    public string PorkchopMaxFlightLabel => CurrentPlot is { } p ? Minutes(p.FlightTimeMaxS) : "";
    public string PorkchopScaleLabel => CurrentPlot is { } p && p.TotalDv.Any(double.IsFinite)
        ? $"{Format.Speed0(p.TotalDv.Where(double.IsFinite).Min())} → 3×" : "";

    /// <summary>The user clicked the porkchop plot of the leg shown.</summary>
    public void SelectPoint(double departureUt, double flightTimeS)
    {
        if (_plan is null || PlotLegPlan is null) return;
        Ensure(_intercepts, _plotLeg + 1);
        _intercepts[_plotLeg] = new InterceptPoint(departureUt, flightTimeS);
        for (var i = _plotLeg + 1; i < _intercepts.Count; i++) _intercepts[i] = null; // later legs start later now
        Recalculate();
    }

    // ---------------- Crafts and targets ----------------

    private void RebuildCrafts()
    {
        var bodies = _sync.StockBodies();
        var home = bodies.Values.FirstOrDefault(b => b.IsDefault);
        var star = home?.ParentId is { } sid ? bodies.Values.FirstOrDefault(b => b.Id == sid) : null;
        var list = new List<CraftChoice>();
        var inSave = new HashSet<string>();

        if (_sync.Save is { } save)
        {
            foreach (var v in save.Vessels)
            {
                if (!v.IsOrbiting || v.Orbit is not { } o || o.BodyName is not { } name || !bodies.TryGetValue(name, out var body)) continue;
                if (!double.IsFinite(o.SemiMajorAxisM) || o.SemiMajorAxisM <= 0 || o.Eccentricity is < 0 or >= 1) continue;
                var orbit = o.ToElements();
                inSave.Add(v.Id);
                list.Add(new CraftChoice($"vessel:{v.Id}", v.Name, $"{SaveImport.TypeText(v.Type)}, {body.Name} {OrbitText(orbit, body)}", body, orbit, v));
            }
        }

        // Launched or undocked since KSP last saved: only the live game knows them (no parts, so no Δv check).
        foreach (var v in _sync.Live?.Unmatched ?? [])
        {
            if (!v.IsOrbiting || v.Orbit is not { } o || o.BodyName is not { } name || !bodies.TryGetValue(name, out var body)) continue;
            if (!double.IsFinite(o.SemiMajorAxisM) || o.SemiMajorAxisM <= 0 || o.Eccentricity is < 0 or >= 1) continue;
            if (list.Any(c => c.Key == $"live:{v.Name}")) continue;
            var orbit = o.ToElements();
            list.Add(new CraftChoice($"live:{v.Name}", v.Name, $"{SaveImport.TypeText(v.Type)}, {body.Name} {OrbitText(orbit, body)} · not saved yet",
                body, orbit, null));
        }

        foreach (var location in _locations.GetAll())
        {
            if (!location.IsPlaced || (location.IsLinked && location.KspSave == _sync.SaveName && inSave.Contains(location.KspVesselId!))) continue;
            if (!bodies.TryGetValue(location.BodyName, out var body) || body.ParentId is null) continue; // around a planet or moon only
            try
            {
                var orbit = CustomLocationOrbits.ToElements(location, body, home, star);
                list.Add(new CraftChoice($"location:{location.Id}", location.Name, $"custom location, {body.Name} {OrbitText(orbit, body)}", body, orbit, null));
            }
            catch (ArgumentException)
            {
                // Position inputs invalid; fix it on the Locations tab.
            }
        }

        var ordered = list.OrderBy(c => c.Body.IsDefault ? 0 : 1).ThenBy(c => c.Body.Name)
            .ThenBy(c => TypeOrder(c.Vessel?.Type)).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        _suspend = true;
        var chaserKey = _selectedChaser?.Key;
        var targetKeys = Targets.Select(t => t.Key).ToList();
        Crafts.Clear();
        foreach (var c in ordered) Crafts.Add(c);
        _selectedChaser = Crafts.FirstOrDefault(c => c.Key == chaserKey)
            ?? Crafts.FirstOrDefault(c => c.Vessel is { Type: "Ship" or "Station" or "Probe" } && c.Body.IsDefault) ?? Crafts.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedChaser));
        Targets.Clear();
        foreach (var key in targetKeys)
            if (Crafts.FirstOrDefault(c => c.Key == key) is { } t) Targets.Add(t);
        _suspend = false;

        OnPropertyChanged(nameof(FollowSaveTime));
        OnPropertyChanged(nameof(ManualStart));
        OnPropertyChanged(nameof(SaveTimeText));
        RefreshAvailableTargets();
        OnPropertyChanged(nameof(CapacityHint));
        Recalculate();
    }

    /// <summary>Relays first (the usual reason to visit), then stations, probes, ships…, debris last.</summary>
    private static int TypeOrder(string? type) => type switch
    {
        "Relay" => 0, "Station" => 1, "Probe" => 2, "Ship" => 3, "Base" or "Lander" or "Rover" or "Plane" => 4, null => 5,
        "Debris" => 7, "SpaceObject" => 8, _ => 6,
    };

    /// <summary>The body the route happens at: Kerbin for a launch, else the starting craft's.</summary>
    private string? RouteBody => StartFromLaunch ? _site.BodyName : SelectedChaser?.Body.Name;

    private void RefreshAvailableTargets()
    {
        var keep = TargetToAdd?.Key;
        AvailableTargets.Clear();
        foreach (var c in Crafts)
            if (c.Body.Name == RouteBody && (StartFromLaunch || c.Key != SelectedChaser?.Key) && !Targets.Contains(c))
                AvailableTargets.Add(c);
        TargetToAdd = AvailableTargets.FirstOrDefault(c => c.Key == keep) ?? AvailableTargets.FirstOrDefault();
    }

    private void AddTarget()
    {
        if (TargetToAdd is not { } t) return;
        Targets.Add(t);
        TargetsChanged();
    }

    private void MoveUp(CraftChoice target)
    {
        var i = Targets.IndexOf(target);
        if (i <= 0) return;
        Targets.Move(i, i - 1);
        TargetsChanged();
    }

    private void StartChanged()
    {
        if (_suspend) return;
        // Targets must orbit the same body as the start.
        foreach (var t in Targets.Where(t => t.Body.Name != RouteBody || (!StartFromLaunch && t.Key == SelectedChaser?.Key)).ToList()) Targets.Remove(t);
        OnPropertyChanged(nameof(CapacityHint));
        TargetsChanged();
    }

    private void TargetsChanged()
    {
        RefreshAvailableTargets();
        OnPropertyChanged(nameof(DeorbitLabel));
        // A new body: suggest a periapsis deep enough in its atmosphere (30 km on Kerbin), or the ground if it has none.
        if (Targets.FirstOrDefault()?.Body is { } body && body.Name != _deorbitBody)
        {
            _deorbitBody = body.Name;
            _deorbitAltitudeText = Km(Math.Round(body.AtmosphereHeightM * 0.43 / 1000) * 1000);
            OnPropertyChanged(nameof(DeorbitAltitudeText));
        }
        InputsChanged();
    }

    // ---------------- Planning ----------------

    /// <summary>Anything that changes the route forgets the user's picks (launch, phasing, porkchop points).</summary>
    private void InputsChanged()
    {
        if (_suspend) return;
        _launchChoice = null;
        _phasing.Clear();
        _intercepts.Clear();
        Recalculate();
    }

    private void PhasingChanged(int leg, PhasingChoice? choice)
    {
        Ensure(_phasing, leg + 1);
        _phasing[leg] = choice ?? PhasingChoice.None;
        Ensure(_intercepts, leg + 1);
        for (var i = leg; i < _intercepts.Count; i++) _intercepts[i] = null;
        for (var i = leg + 1; i < _phasing.Count; i++) _phasing[i] = null;
        Recalculate();
    }

    private void ResetIntercept()
    {
        if (_plotLeg >= _intercepts.Count) return;
        for (var i = _plotLeg; i < _intercepts.Count; i++) _intercepts[i] = null;
        Recalculate();
    }

    private async void Recalculate()
    {
        if (_suspend) return;
        var version = ++_version;
        ErrorText = null;
        OnPropertyChanged(nameof(EmptyText));

        var request = BuildRequest(out var error);
        if (request is null)
        {
            ErrorText = error;
            SetPlan(null, null);
            return;
        }

        var choices = new RendezvousChoices(_launchChoice, _phasing.ToList(), _intercepts.ToList());
        SetBusy(+1);
        try
        {
            var plan = await Task.Run(() => Rendezvous.Plan(request, choices));
            if (version != _version) return;
            SetPlan(request, plan);
            SaveLeg(request, plan);
        }
        catch (ArgumentException ex)
        {
            if (version != _version) return;
            ErrorText = ex.Message;
            SetPlan(null, null);
        }
        finally
        {
            SetBusy(-1);
        }
    }

    private RendezvousRequest? BuildRequest(out string? error)
    {
        error = null;
        if (Targets.Count == 0) return null;
        var bodies = _sync.StockBodies();
        if (RouteBody is not { } bodyName || !bodies.TryGetValue(bodyName, out var body)) { error = "Pick where to start from."; return null; }

        double start;
        if (FollowSaveTime) start = _sync.Save!.Ut + LeadMinutes * 60;
        else if (TryClock(StartTimeText, out var hours)) start = _calendar.ToUniversalTime(StartYear, StartDay, hours);
        else { error = "Enter the start time as h:mm or h:mm:ss."; return null; }

        var targets = Targets.Select(t => new RendezvousTarget(t.Name, t.Orbit)).ToList();
        double? deorbit = null;
        if (Deorbit)
        {
            if (!TryNumber(DeorbitAltitudeText, out var km)) { error = "Enter the deorbit periapsis in km."; return null; }
            deorbit = km * 1000;
        }

        if (!StartFromLaunch)
        {
            if (SelectedChaser is not { } chaser) { error = "Pick the craft that will fly to the targets."; return null; }
            return new RendezvousRequest(body, targets, start, StayMinutes * 60, new RendezvousTarget(chaser.Name, chaser.Orbit),
                DeorbitPeriapsisAltitudeM: deorbit);
        }

        if (!TryNumber(ParkingAltitudeText, out var parkingKm) || parkingKm <= 0) { error = "Enter the parking orbit altitude in km."; return null; }
        if (!TryNumber(AscentMinutesText, out var ascentMin) || ascentMin < 0) { error = "Enter how many minutes the climb to orbit takes."; return null; }
        if (!TryNumber(DownrangeText, out var downrange)) { error = "Enter how far around the planet you reach orbit, in degrees."; return null; }
        return new RendezvousRequest(body, targets, start, StayMinutes * 60, Site: _site,
            Ascent: new AscentProfile(parkingKm * 1000, ascentMin * 60, downrange), DeorbitPeriapsisAltitudeM: deorbit);
    }

    private void SetPlan(RendezvousRequest? request, RendezvousPlan? plan)
    {
        _request = request;
        _plan = plan;
        _suspend = true;

        LaunchRows.Clear();
        LaunchStep = null;
        Legs.Clear();
        PlotLegs.Clear();
        DeorbitStep = null;

        if (request is not null && plan is not null)
        {
            var now = request.EarliestUt;
            var number = 1;
            if (plan.Launch is { } launch)
            {
                foreach (var option in plan.LaunchOptions) LaunchRows.Add(LaunchRowFor(request, option));
                LaunchStep = LaunchStepFor(request, launch, now, number++);
            }
            for (var i = 0; i < plan.Legs.Count; i++)
            {
                var leg = LegRowFor(plan.Legs[i], i, now, ref number);
                Legs.Add(leg);
                PlotLegs.Add(leg);
            }
            if (plan.Deorbit is { } d) DeorbitStep = StepFor(d, request.Body, request.Targets[^1].Name, now, number);
        }
        if (PlotLegs.All(l => l.Index != _plotLeg)) _plotLeg = PlotLegs.FirstOrDefault()?.Index ?? 0;

        _suspend = false;
        foreach (var name in PlanProperties) OnPropertyChanged(name);
        RefreshBudget();
        RefreshPlot();
    }

    private static readonly string[] PlanProperties =
    [
        nameof(HasPlan), nameof(HasNoPlan), nameof(EmptyText), nameof(HasLaunch), nameof(LaunchIndex), nameof(LaunchStep), nameof(DeorbitStep), nameof(HasDeorbit),
    ];

    private void RefreshPlot()
    {
        foreach (var name in new[]
                 {
                     nameof(SelectedPlotLeg), nameof(SelectedLegPlan), nameof(DiagramBody), nameof(DiagramFirstStep), nameof(CurrentPlot), nameof(CurrentPlotSelection), nameof(HasPlot), nameof(IsCustomIntercept), nameof(PlotNote),
                     nameof(PorkchopStartLabel), nameof(PorkchopEndLabel), nameof(PorkchopMinFlightLabel), nameof(PorkchopMaxFlightLabel),
                     nameof(PorkchopScaleLabel),
                 })
            OnPropertyChanged(name);
    }

    // ---------------- Formatting the plan ----------------

    private LaunchRow LaunchRowFor(RendezvousRequest request, LaunchOption o)
    {
        var kind = o.Kind switch
        {
            LaunchKind.AscendingPass => "into its plane (pad passing under it northbound)",
            LaunchKind.DescendingPass => "into its plane (pad passing under it southbound)",
            _ => "due east, plane fixed in orbit",
        };
        return new LaunchRow($"{_calendar.FormatDate(o.LiftoffUt)}  ·  heading {o.HeadingDeg:0.0}°",
            $"{Wait(o.LiftoffUt - request.EarliestUt)} · {kind}");
    }

    private StepRow LaunchStepFor(RendezvousRequest request, LaunchOption o, double now, int number)
    {
        var ascent = request.Ascent!;
        var alt = Km(ascent.ParkingAltitudeM);
        var feel = $"Lift off at {_calendar.FormatDate(o.LiftoffUt)} and pitch over toward heading {o.HeadingDeg:0}° " +
                   (o.Kind == LaunchKind.DueEast ? "(due east)" : $"so you climb straight into {request.Targets[0].Name}'s plane") +
                   $". Circularize at {alt} km, about {ascent.AscentTimeS / 60:0.#} minutes after lift-off.";
        var exact = new List<Fact>
        {
            new("Lift-off", _calendar.FormatDate(o.LiftoffUt)),
            new("UT", Ut(o.LiftoffUt)),
            new("Heading", $"{o.HeadingDeg:0.0}°"),
            new("Parking orbit", $"{alt} km circular, inclination {o.ParkingOrbit.InclinationDeg:0.##}°"),
            new("In orbit by", _calendar.FormatDate(o.InsertionUt)),
        };
        return new StepRow($"{number}", $"Launch from {_site.Name}", _calendar.FormatDate(o.LiftoffUt), Wait(o.LiftoffUt - now),
            request.Body.SurfaceToLowOrbitDv is { } dv ? $"≈ {Format.Speed0(dv)}" : "", feel, exact);
    }

    private LegRow LegRowFor(LegPlan leg, int index, double now, ref int number)
    {
        var body = _request!.Body;
        var g = leg.Guide;
        var guide = g.SameOrbit
            ? $"Same orbit as {leg.Target.Name}: it's {g.CurrentLeadDeg:0}° ahead. Drop lower to catch up (or go higher to let it come round) for a " +
              "whole number of laps, then hop across."
            : $"Rule of thumb: burn when {leg.Target.Name} is {g.RequiredLeadDeg:0.0}° ahead of you (now {g.CurrentLeadDeg:0}°). " +
              $"Transfer takes {Minutes(g.TransferTimeS)}; windows come every {(double.IsFinite(g.SynodicPeriodS) ? Minutes(g.SynodicPeriodS) : "—")}" +
              $" (your orbit {Minutes(g.ChaserPeriodS)}, its orbit {Minutes(g.TargetPeriodS)}).";

        var rows = new List<PhasingRow>();
        if (double.IsFinite(g.WaitS)) rows.Add(new PhasingRow(PhasingChoice.None, $"Wait in your orbit · window {Wait(g.WaitS)}"));
        foreach (var o in leg.PhasingOptions)
        {
            var orbit = o.Higher ? $"Ap {Km(o.ApoapsisAltitudeM)} km" : $"Pe {Km(o.PeriapsisAltitudeM)} km";
            rows.Add(new PhasingRow(o.Choice,
                $"{(o.Higher ? "Higher" : "Lower")} for {o.Orbits} lap{(o.Orbits == 1 ? "" : "s")} ({orbit}) · +{Format.Speed0(o.ExtraDv)} · window {Wait(o.DepartureUt - leg.StartUt)}"));
        }
        var selected = leg.Phasing is { } p ? rows.FirstOrDefault(r => r.Choice == p.Choice) : rows.FirstOrDefault(r => r.Choice == PhasingChoice.None);

        var steps = new List<StepRow>();
        foreach (var m in leg.Maneuvers) steps.Add(StepFor(m, body, leg.Target.Name, now, number++));
        return new LegRow(index, $"Leg {index + 1}: meet {leg.Target.Name}", guide, rows, selected, steps,
            $"Alongside {leg.Target.Name} at {_calendar.FormatDate(leg.ArrivalUt)} ({Wait(leg.ArrivalUt - now)} from the start)",
            Format.Speed0(leg.TotalDv), PhasingChanged);
    }

    private StepRow StepFor(Maneuver m, CelestialBody body, string target, double now, int number)
    {
        var b = m.Burn;
        var after = $"Pe {Km(m.PeriapsisAltitudeM)} km · Ap {Km(m.ApoapsisAltitudeM)} km";
        var (title, feel) = m.Kind switch
        {
            ManeuverKind.PlaneChange => ("Match planes",
                $"Set {target} as target. At the {(m.AtAscendingNode ? "AN" : "DN")} marker, burn {(b.Normal >= 0 ? "normal" : "anti-normal")} " +
                $"{Format.Speed0(m.DeltaV)} until the relative inclination reads 0.0° (now {m.RelativeInclinationDeg:0.00}°)."),
            ManeuverKind.Phasing => (m.Phasing!.Higher ? "Phasing orbit (higher)" : "Phasing orbit (lower)",
                $"Burn {(b.Prograde >= 0 ? "prograde" : "retrograde")} {Format.Speed0(m.DeltaV)} to " +
                (m.Phasing.Higher ? $"raise the far side to {Km(m.ApoapsisAltitudeM)} km" : $"drop the far side to {Km(m.PeriapsisAltitudeM)} km") +
                $". Fly {m.Phasing.Orbits} full lap{(m.Phasing.Orbits == 1 ? "" : "s")} ({Minutes(m.Phasing.Orbits * m.Phasing.PeriodS)}) — " +
                (m.Phasing.Higher ? "the target catches up with you." : "you catch up with the target.")),
            ManeuverKind.Transfer => ("Transfer burn",
                $"When {target} is {m.TargetAheadDeg:0.0}° ahead of you, burn {(b.Prograde >= 0 ? "prograde" : "retrograde")} {Format.Speed0(m.DeltaV)} " +
                (b.Prograde >= 0 ? $"until your apoapsis reaches {Km(m.ApoapsisAltitudeM)} km" : $"until your periapsis drops to {Km(m.PeriapsisAltitudeM)} km") +
                $". Look for the close-approach markers: you meet in {Minutes(m.FlightTimeS)}." +
                (Math.Abs(b.Normal) + Math.Abs(b.Radial) > 2 ? $" (Also {Signed(b.Normal)} normal, {Signed(b.Radial)} radial — easier as a node.)" : "")),
            ManeuverKind.MatchVelocity => ("Match velocity",
                $"Near the close approach, switch the navball to Target, point at target-retrograde and burn {Format.Speed0(m.DeltaV)} " +
                "until the relative speed is near zero. Start a little early: the burn takes time." +
                (m.MissDistanceM > 100 ? $" You'll be about {Distance(m.MissDistanceM)} away — close in with the approach rules below." : "")),
            ManeuverKind.Deorbit => ("Deorbit",
                $"With {target} docked, burn retrograde {Format.Speed0(m.DeltaV)} (at apoapsis, if the orbit isn't circular) to drop the periapsis to " +
                $"{Km(m.PeriapsisAltitudeM)} km. " +
                (body.HasAtmosphere && m.PeriapsisAltitudeM < body.AtmosphereHeightM ? $"{body.Name}'s atmosphere does the rest." : "It will hit the ground.")),
            _ => (m.Kind.ToString(), ""),
        };
        var exact = new List<Fact>
        {
            new("Node at", _calendar.FormatDate(m.Ut)),
            new("UT", Ut(m.Ut)),
            new("Prograde", Signed(b.Prograde)),
            new("Normal", Signed(b.Normal)),
            new("Radial", Signed(b.Radial)),
            new("Orbit after", m.Kind == ManeuverKind.MatchVelocity ? $"{target}'s orbit" : after),
        };
        if (m.Kind == ManeuverKind.MatchVelocity && m.MissDistanceM > 1) exact.Add(new("Distance at meeting", Distance(m.MissDistanceM)));
        return new StepRow($"{number}", title, _calendar.FormatDate(m.Ut), Wait(m.Ut - now), Format.Speed0(m.DeltaV), feel, exact);
    }

    // ---------------- Budget and capacity ----------------

    private void RefreshBudget()
    {
        BudgetLines.Clear();
        if (_request is { } request && _plan is { } plan)
        {
            if (plan.Launch is not null)
                BudgetLines.Add(new BudgetLine($"Launch: {request.Body.Name} surface → {Km(request.Ascent!.ParkingAltitudeM)} km orbit",
                    request.Body.SurfaceToLowOrbitDv, "Community Δv-map estimate; depends on your ascent"));
            foreach (var leg in plan.Legs)
            {
                BudgetLines.Add(new BudgetLine($"Meet {leg.Target.Name}", null, IsHeader: true));
                foreach (var m in leg.Maneuvers) BudgetLines.Add(new BudgetLine(KindTitle(m), m.DeltaV));
            }
            if (plan.Deorbit is { } d)
                BudgetLines.Add(new BudgetLine($"Deorbit {request.Targets[^1].Name}", d.DeltaV, "Docked: the heavier stack burns more fuel for the same Δv"));
        }
        OnPropertyChanged(nameof(BudgetTotalText));
        RefreshCapacity();
    }

    private static string KindTitle(Maneuver m) => m.Kind switch
    {
        ManeuverKind.PlaneChange => "Match planes",
        ManeuverKind.Phasing => "Phasing orbit",
        ManeuverKind.Transfer => "Transfer burn",
        ManeuverKind.MatchVelocity => "Match velocity",
        _ => m.Kind.ToString(),
    };

    private void RefreshDesignChoices()
    {
        var keep = _selectedDesign?.Path;
        DesignChoices.Clear();
        DesignChoices.Add(new DesignChoice(null, "— No design —"));
        foreach (var d in _designs?.Entries ?? [])
            if (d.Report is { Stages.Count: > 0 }) DesignChoices.Add(new DesignChoice(d, d.Title));
        _selectedDesign = DesignChoices.FirstOrDefault(c => c.Path == keep) ?? DesignChoices[0];
        OnPropertyChanged(nameof(SelectedDesign));
        OnPropertyChanged(nameof(CapacityHint));
        RefreshCapacity();
    }

    /// <summary>
    /// Launch: the saved design checked against launch + each leg (+ deorbit), as in the Mission Builder, with 10% margin on
    /// the orbital phases for flying by hand. From orbit: the chosen vessel's remaining Δv against the route.
    /// </summary>
    private void RefreshCapacity()
    {
        CapacityVerdict = null;
        CapacityStatus = null;
        CapacityLines = [];
        if (_request is { } request && _plan is { } plan)
        {
            if (plan.Launch is not null && SelectedDesign?.Design?.Report is { } design)
            {
                var phases = new List<PhaseNeed> { new("Launch", request.Body.SurfaceToLowOrbitDv ?? 0) };
                phases.AddRange(plan.Legs.Select(l => new PhaseNeed($"Meet {l.Target.Name}", l.TotalDv, 0.1 * l.TotalDv)));
                if (plan.Deorbit is { } d) phases.Add(new PhaseNeed("Deorbit", d.DeltaV, 0.1 * d.DeltaV));
                var liftOff = new SurfaceEvent(SurfaceEventKind.LiftOff, 0, 0, request.Body.Name, request.Body.SurfaceGravity,
                    request.Body.Atmosphere?.SeaLevelPressureAtm ?? 0);
                var check = DesignCheck.Check(phases, design, [liftOff]);
                CapacityStatus = check.Status;
                CapacityVerdict = check.Status switch
                {
                    Coverage.Comfortable => $"{design.Name}: enough — {Format.Speed0(check.SpareOverMargins)} to spare beyond 10% margins",
                    Coverage.InsideMargin => $"{design.Name}: enough — {Format.Speed0(check.SpareOverRequired)} to spare, inside the margins",
                    _ => $"{design.Name}: not enough — short by {Format.Speed0(check.Phases.Sum(p => p.Shortfall))}",
                };
                if (check.Surfaces.FirstOrDefault() is { Rating: TwrRating.TooLow or TwrRating.NoStage } twr)
                {
                    CapacityVerdict += $"\nLaunch TWR {twr.Twr:0.00} is too low to lift off.";
                    CapacityStatus = Coverage.Short;
                }
                CapacityLines = check.Phases.Select(p => new Fact(p.Name,
                    $"{Format.Speed0(p.Required)} · " + (p.Status == Coverage.Short ? $"short {Format.Speed0(p.Shortfall)}"
                        : $"stage{(p.Stages.Distinct().Count() == 1 ? "" : "s")} {string.Join(", ", p.Stages.Distinct())}"))).ToList();
            }
            else if (plan.Launch is null && SelectedChaser?.Vessel is { } vessel)
            {
                var report = VesselAnalysis.Analyze(vessel, _sync.Parts.Catalog);
                var need = plan.OrbitalDv;
                var have = report.DeltaVVacuum;
                CapacityStatus = have >= need * 1.1 ? Coverage.Comfortable : have >= need ? Coverage.InsideMargin : Coverage.Short;
                CapacityVerdict = have >= need
                    ? $"{vessel.Name} has {Format.Speed0(have)}: enough, {Format.Speed0(have - need)} to spare"
                    : $"{vessel.Name} has {Format.Speed0(have)}: short by {Format.Speed0(need - have)}";
                if (plan.Deorbit is not null) CapacityVerdict += "\nThe deorbit is flown with the target docked, so it needs more fuel than this suggests.";
                if (report.UnknownParts.Count > 0) CapacityVerdict += $"\n{report.UnknownParts.Count} part(s) not in the part data; Δv may be off.";
            }
        }
        foreach (var name in new[] { nameof(CapacityVerdict), nameof(CapacityStatus), nameof(HasCapacity), nameof(CapacityLines), nameof(CapacityHint) })
            OnPropertyChanged(name);
    }

    // ---------------- Helpers ----------------

    private void SetBusy(int delta)
    {
        _busyCount += delta;
        OnPropertyChanged(nameof(IsBusy));
    }

    private void SetStartFields(double ut)
    {
        var year = (int)Math.Floor(ut / _calendar.SecondsPerYear);
        var rem = ut - year * _calendar.SecondsPerYear;
        var day = (int)Math.Floor(rem / _calendar.SecondsPerDay);
        rem -= day * _calendar.SecondsPerDay;
        _startYear = year + 1;
        _startDay = day + 1;
        _startTimeText = $"{(int)(rem / 3600)}:{(int)(rem % 3600 / 60):00}";
        OnPropertyChanged(nameof(StartYear));
        OnPropertyChanged(nameof(StartDay));
        OnPropertyChanged(nameof(StartTimeText));
    }

    private static void Ensure<T>(List<T?> list, int count)
    {
        while (list.Count < count) list.Add(default);
    }

    private static string OrbitText(OrbitalElements orbit, CelestialBody body)
    {
        var pe = orbit.SemiMajorAxisM * (1 - orbit.Eccentricity) - body.EquatorialRadiusM;
        var ap = orbit.SemiMajorAxisM * (1 + orbit.Eccentricity) - body.EquatorialRadiusM;
        var size = Math.Abs(ap - pe) < 2_000 ? $"{Km(pe)} km" : $"{Km(pe)} × {Km(ap)} km";
        return orbit.InclinationDeg >= 0.5 ? $"{size}, {orbit.InclinationDeg:0}°" : size;
    }

    private string Wait(double seconds) => seconds < 60 ? "now" : "in " + _calendar.FormatDuration(seconds);

    private static string Minutes(double seconds) =>
        seconds < 3600 ? $"{seconds / 60:0} min" : $"{(int)(seconds / 3600)}h {(int)(seconds % 3600 / 60):00}m";

    private static string Km(double metres) => (metres / 1000).ToString(Math.Abs(metres) < 10_000 ? "#,0.#" : "#,0", CultureInfo.CurrentCulture);

    private static string Distance(double metres) => metres < 1000 ? $"{metres:0} m" : $"{metres / 1000:#,0.#} km";

    private static string Signed(double metresPerSecond) =>
        (metresPerSecond >= 0.05 ? "+" : metresPerSecond <= -0.05 ? "−" : "") + Math.Abs(metresPerSecond).ToString("0.0", CultureInfo.CurrentCulture) + " m/s";

    private static string Ut(double ut) => ut.ToString("#,0", CultureInfo.CurrentCulture) + " s";

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Trim().TrimEnd('k', 'K', 'm', 'M', '°', 's').Trim(), NumberStyles.Float | NumberStyles.AllowThousands,
            CultureInfo.CurrentCulture, out value);

    private static bool TryClock(string text, out double hours)
    {
        hours = 0;
        var parts = text.Trim().Split(':');
        if (parts.Length is < 1 or > 3) return false;
        var values = new double[3];
        for (var i = 0; i < parts.Length; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.CurrentCulture, out values[i]) || values[i] < 0) return false;
        hours = values[0] + values[1] / 60 + values[2] / 3600;
        return hours < 6;
    }
}
