using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App.ViewModels;

public enum BurnState { Upcoming, Next, OnCraft, Passed }

/// <summary>One burn on the Flight pane: what and when, its node values, and whether it's on the craft yet.</summary>
public sealed class FlightBurnRow(int number, FlightBurn burn) : ObservableObject
{
    private string _countdown = "";
    private string _burnTime = "";
    private BurnState _state;

    public int Number { get; } = number;
    public FlightBurn Burn { get; } = burn;
    public string Title => Burn.Title;
    public string? Note => Burn.Note;
    public bool HasNote => Burn.Note is not null;
    public string When => GameCalendar.Kerbin.FormatDate(Burn.Ut);
    public string DeltaV => Format.Speed0(Burn.DeltaV);

    public string NodeText =>
        $"prograde {Signed(Burn.Burn.Prograde)}   normal {Signed(Burn.Burn.Normal)}   radial {Signed(Burn.Burn.Radial)}   · UT {Burn.Ut.ToString("#,0", CultureInfo.CurrentCulture)} s";

    public string Countdown { get => _countdown; set => SetProperty(ref _countdown, value); }

    /// <summary>How long the engines run for it ("burn 1m 24s"), when the craft's engines are known; else empty.</summary>
    public string BurnTime { get => _burnTime; set { if (SetProperty(ref _burnTime, value)) OnPropertyChanged(nameof(HasBurnTime)); } }

    public bool HasBurnTime => BurnTime.Length > 0;

    public BurnState State
    {
        get => _state;
        set { if (SetProperty(ref _state, value)) OnPropertyChanged(nameof(StateText)); }
    }

    public string StateText => State switch
    {
        BurnState.Next => "Next",
        BurnState.OnCraft => "On the craft",
        BurnState.Passed => "Passed",
        _ => "",
    };

    private static string Signed(double v) =>
        (v >= 0.05 ? "+" : v <= -0.05 ? "−" : "") + Math.Abs(v).ToString("0.0", CultureInfo.CurrentCulture) + " m/s";
}

/// <summary>One step of a mission flight on the Flight pane: what it does, whether it's done, and its plan from where the craft is.</summary>
public sealed record MissionStepRow(int Number, string Title, string StateText, string DeltaV, string When, string? Note, bool IsCurrent, bool IsDone)
{
    public bool HasNote => !string.IsNullOrEmpty(Note);
    public bool HasState => StateText.Length > 0;
    public bool HasWhen => When.Length > 0;
}

/// <summary>
/// The Flight pane: plans handed over from the planning panes, flown against the live game. Shows each burn's countdown
/// and whether it's on the craft, sends burns as maneuver nodes, and re-plans from the craft's real orbit when it has
/// drifted from the plan.
/// </summary>
public sealed class FlightViewModel : ObservableObject
{
    /// <summary>Further than this from the planned path lights up Re-plan. docs/MATH.md §8.3.</summary>
    public const double DriftThresholdM = 1_000;

    /// <summary>How long after a burn's node time it counts as flown (about half a long burn comes after the node).</summary>
    public const double SettleS = 90;

    /// <summary>The soonest a re-planned burn (a course correction) is placed: time to turn the craft.</summary>
    public const double CorrectionLeadS = 120;

    private readonly FlightRepository _flights;
    private readonly SaveSync _sync;
    private readonly BodyRotation _rotation;
    private readonly MissionRepository? _missions;
    private readonly PlannerEvents _events;
    private readonly GameCalendar _calendar = GameCalendar.Kerbin;
    private Flight? _selected;
    private string? _status;
    private string _driftText = "";
    private bool _isDrifting;
    private bool _busy;
    private (long Id, double Ut)? _autoTried; // the flight and burn last re-planned after
    private (long Id, int Step)? _heldAt; // a mission stepped back by hand: not moved on by itself from there
    private SavedMission? _missionToFly;

    public FlightViewModel(FlightRepository flights, SaveSync sync, PlannerEvents events, BodyRotation rotation, MissionRepository? missions = null)
    {
        _flights = flights;
        _sync = sync;
        _rotation = rotation;
        _missions = missions;
        _events = events;
        events.OpenFlightRequested += id =>
        {
            Reload();
            SelectedFlight = Flights.FirstOrDefault(f => f.Id == id);
            // A landing sent from a mission comes without burns: plan it now if its craft is already in orbit there.
            if (_selected is { Kind: FlightKind.Landing, Craft: not null, Burns.Count: 0 }) _ = ReplanAsync();
            // A mission comes unplanned: plan it from the craft, or from where it starts.
            if (_selected is { Kind: FlightKind.Mission, StepPlans.Count: 0 }) _ = ReplanAsync();
        };
        events.SaveChanged += () =>
        {
            Reload(); // the open flight closes if it belongs to the last save
            RefreshSavedMissions();
        };
        sync.Link.GameUpdated += _ => RefreshLive();
        sync.Link.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(KspLink.IsConnected) or nameof(KspLink.InFlight)) RefreshLive();
        };
        sync.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SaveSync.Save)) RefreshLive();
        };

        UseActiveCraftCommand = new RelayCommand(() => _ = UseActiveCraftAsync(), () => _selected is not null && _sync.ActiveCraft is not null && !_busy);
        ReplanCommand = new RelayCommand(() => _ = ReplanAsync(),
            () => (_selected is { Kind: FlightKind.Mission } || _selected is { Craft: not null } f && CanReplan(f)) && !_busy);
        NextStepCommand = new RelayCommand(() => _ = StepToAsync(+1), () => _selected is { Kind: FlightKind.Mission } f && f.StepIndex < f.Steps.Count && !_busy);
        PreviousStepCommand = new RelayCommand(() => _ = StepToAsync(-1), () => _selected is { Kind: FlightKind.Mission, StepIndex: > 0 } && !_busy);
        FlyMissionCommand = new RelayCommand(() => _events.RequestFlyMission(_missionToFly!.Id), () => _missionToFly is not null);
        RefreshSavedMissions();
        SendBurnCommand = new RelayCommand<FlightBurnRow>(r => _ = SendAsync([r.Burn], replace: false), r => CanSend && r.State != BurnState.Passed);
        SendNextCommand = new RelayCommand(() => _ = SendAsync([NextOwnBurn()!.Burn], replace: false), () => CanSend && NextOwnBurn() is not null);
        SendAllCommand = new RelayCommand(() => _ = SendAsync(OwnFutureBurns(), replace: true), () => CanSend && OwnFutureBurns().Count > 0);
        DeleteCommand = new RelayCommand(Delete, () => _selected is not null);
        Reload();
        SelectedFlight = Flights.FirstOrDefault();
    }

    public ObservableCollection<Flight> Flights { get; } = [];
    public ObservableCollection<FlightBurnRow> Burns { get; } = [];

    public RelayCommand UseActiveCraftCommand { get; }
    public RelayCommand ReplanCommand { get; }
    public RelayCommand<FlightBurnRow> SendBurnCommand { get; }
    public RelayCommand SendNextCommand { get; }
    public RelayCommand SendAllCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand NextStepCommand { get; }
    public RelayCommand PreviousStepCommand { get; }
    public RelayCommand FlyMissionCommand { get; }

    /// <summary>Saved missions, to fly one from Fly mode.</summary>
    public ObservableCollection<SavedMission> SavedMissions { get; } = [];

    public bool HasSavedMissions => SavedMissions.Count > 0;

    public SavedMission? MissionToFly
    {
        get => _missionToFly;
        set { if (SetProperty(ref _missionToFly, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    /// <summary>Reads the saved missions again (the list opens, or another save was chosen), keeping the one picked.</summary>
    public void RefreshSavedMissions()
    {
        if (_missions is null) return;
        var keep = _missionToFly?.Id;
        SavedMissions.Clear();
        foreach (var m in _missions.GetAll()) SavedMissions.Add(m);
        _missionToFly = SavedMissions.FirstOrDefault(m => m.Id == keep) ?? SavedMissions.FirstOrDefault();
        OnPropertyChanged(nameof(MissionToFly));
        OnPropertyChanged(nameof(HasSavedMissions));
    }

    /// <summary>Asks the user a yes/no question; the view supplies the dialog.</summary>
    public Func<string, bool> Confirm { get; set; } = _ => true;

    public Flight? SelectedFlight
    {
        get => _selected;
        set
        {
            if (_storing) return; // the Flights list letting go of the flight being swapped for its re-planned copy
            if (!SetProperty(ref _selected, value)) return;
            Status = null;
            RebuildBurns();
        }
    }

    public bool HasFlight => _selected is not null;
    public bool HasNoFlights => Flights.Count == 0;
    public bool HasNoSelection => _selected is null;

    public string NoSelectionText => Flights.Count == 0
        ? "No flights yet. To fly a whole mission, pick a saved one in the Flights pane (or click Fly this mission in Plan mode). For a quick burn or a trip from where you are, use the From here pane (Land at a spot lands). Or plan on the Rendezvous, Resonant Orbits or Transfer Planner tool, then click Fly this plan (or window), or Fly this landing on a mission's landing spot."
        : "Pick a flight in the Flights pane to see its burns and send them to KSP.";

    // ---------------- The game ----------------

    /// <summary>The live link, in a line: what's being flown, its orbit and target.</summary>
    public string GameText
    {
        get
        {
            var link = _sync.Link;
            if (!link.IsConnected) return "Not connected to KSP. Use KSP › Connect to KSP to follow the game and send burns.";
            if (!link.InFlight) return "In KSP, but not flying a craft. Switch to one (or launch) to send burns.";
            var game = link.Game;
            var active = game?.Active;
            var target = game?.Target?.Name ?? game?.TargetBody;
            var orbit = active?.Orbit is { IsClosed: true } o && o.BodyName is { } body && Body(body) is { } b ? $" · {OrbitText(o.ToElements(), b)} around {body}" : "";
            return $"Flying {active?.Name ?? "nothing"}{orbit}" + (target is null ? "" : $" · target {target}");
        }
    }

    public string GameTimeText => _sync.Now is { } now ? _calendar.FormatDate(now) + (_sync.Link.IsConnected ? "" : " (when KSP last saved)") : "";

    /// <summary>Burns can go to the game: connected and flying a craft.</summary>
    public bool CanSend => _sync.Link.InFlight && !_busy;

    // ---------------- The flight ----------------

    public string KindText => _selected?.Kind switch
    {
        FlightKind.Rendezvous => _selected.Targets.Count == 0 ? "Rendezvous" : $"Rendezvous with {string.Join(", then ", _selected.Targets.Select(t => t.Name))}",
        FlightKind.Resonant => $"Deploy {_selected.SatelliteCount} satellites at {_selected.AltitudeM / 1000:#,0.#} km around {_selected.BodyName}",
        FlightKind.Burns when IsTrip(_selected) => $"To {_selected.DestinationName}, arriving {_selected.AltitudeM / 1000:#,0.#} km up" +
                                                   (_selected.ArrivalInclinationDeg is { } i ? $", inclined {i:0.#}°" : "") +
                                                   ". Re-plan from wherever the craft is on the way.",
        FlightKind.Burns => $"Planned from where the craft was around {_selected.BodyName}. To plan it again, use the From here pane.",
        FlightKind.Landing => $"Land at {_selected.Landing?.Target.Name} on {_selected.BodyName}" +
                              $" ({_selected.Landing?.Target.LatitudeDeg:0.###}°, {_selected.Landing?.Target.LongitudeDeg:0.###}°)" +
                              (_selected.LandSooner ? ", the sooner way" : "") + ". Re-plan from the craft's orbit around " + _selected.BodyName + ".",
        FlightKind.Transfer => $"To {_selected.DestinationName} in the window leaving {_selected.OriginName} {_calendar.FormatDate(_selected.TransferDepartureUt)}, " +
                               $"{_calendar.FormatDuration(_selected.TransferFlightTimeS)} there. Re-plan from wherever the craft is on the way.",
        FlightKind.Mission => _selected.StepIndex >= _selected.Steps.Count
            ? $"Mission flown: all {_selected.Steps.Count} steps."
            : $"Step {_selected.StepIndex + 1} of {_selected.Steps.Count}: {_selected.Steps[_selected.StepIndex].Title}. Each step is planned from the craft's real " +
              "orbit once it gets there, and the steps after it from where it leaves the craft.",
        _ => "",
    };

    // ---------------- A mission ----------------

    public bool HasMission => _selected is { Kind: FlightKind.Mission };

    /// <summary>A mission's steps: the ones flown, the one being flown, and the rest as planned from where it leaves the craft.</summary>
    public ObservableCollection<MissionStepRow> MissionSteps { get; } = [];

    /// <summary>"1,980 m/s still to burn, landing Year 1, Day 12" — what the steps from here add up to.</summary>
    public string MissionTotalText
    {
        get
        {
            if (_selected is not { Kind: FlightKind.Mission } f || f.StepPlans.Count == 0) return "";
            var planned = f.StepPlans.TakeWhile(p => p.IsPlanned).ToList();
            var dv = planned.Sum(p => p.DeltaV);
            var rest = planned.Count < f.StepPlans.Count ? $" for the next {planned.Count} step{(planned.Count == 1 ? "" : "s")}" : "";
            return $"{Format.Speed0(dv)} still to burn{rest}" + (planned.Count > 0 ? $", done {_calendar.FormatDate(planned[^1].EndUt)}" : "");
        }
    }

    private void RebuildMissionSteps()
    {
        MissionSteps.Clear();
        if (_selected is not { Kind: FlightKind.Mission } f) return;
        for (var i = 0; i < f.Steps.Count; i++)
        {
            var step = f.Steps[i];
            var plan = f.StepPlans.FirstOrDefault(p => p.Step == i);
            var done = i < f.StepIndex;
            var current = i == f.StepIndex;
            var dv = done || plan is not { IsPlanned: true } ? "" : step.Kind == MissionStepKind.Launch ? "by hand" : Format.Speed0(plan.DeltaV);
            var when = done || plan is not { IsPlanned: true } ? ""
                : plan.Burns.Count > 0 ? $"{_calendar.FormatDate(plan.Burns[0].Ut)} → {_calendar.FormatDate(plan.EndUt)}"
                : step.Kind == MissionStepKind.Launch && plan.Assumed ? $"in orbit about {_calendar.FormatDate(plan.EndUt)}" : "";
            var note = done ? null : plan?.Problem ?? (current ? null : plan?.Note);
            MissionSteps.Add(new MissionStepRow(i + 1, step.Title, done ? "Done" : current ? "Now" : "", dv, when, note, current, done));
        }
    }

    public string CraftText => _selected?.Craft is { } craft
        ? $"Flown by {craft.Name}" + (craft.SaveVesselId is null ? " (not in the save yet)" : "")
        : "No craft chosen yet: switch to it in KSP, then click \"Use the craft I'm flying\".";

    public string? Note => _selected?.Note;
    public bool HasNote => !string.IsNullOrEmpty(Note);

    public string PlannedText => _selected is { } f ? $"Planned for {_calendar.FormatDate(f.PlannedUt)}" : "";

    /// <summary>A flight that starts around another body: the Δv map's steps to fly first, until it's re-planned from there.</summary>
    public bool HasApproach => _selected?.Approach is not null;

    public string ApproachTitle => _selected is { Approach: { } a } f
        ? $"First, get to {f.BodyName}'s {a.ParkingAltitudeM / 1000:#,0.#} km orbit · {Format.Speed0(a.DeltaV)}" : "";

    public IReadOnlyList<string> ApproachSteps => _selected?.Approach is { } a
        ? a.Steps.Select((s, i) => $"{i + 1}. {s.Title}   {Format.Speed0(s.DeltaV)}").ToList() : [];

    /// <summary>How far the craft is from where the plan has it now.</summary>
    public string DriftText { get => _driftText; private set => SetProperty(ref _driftText, value); }

    /// <summary>Far enough off the plan that Re-plan is worth it.</summary>
    public bool IsDrifting { get => _isDrifting; private set => SetProperty(ref _isDrifting, value); }

    /// <summary>What the last send or re-plan did, or why it couldn't.</summary>
    public string? Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(HasStatus)); } }

    public bool HasStatus => !string.IsNullOrEmpty(Status);

    // ---------------- Updating ----------------

    private void Reload()
    {
        var keep = _selected?.Id;
        Flights.Clear();
        foreach (var f in _flights.GetAll()) Flights.Add(f);
        _selected = Flights.FirstOrDefault(f => f.Id == keep) ?? Flights.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedFlight));
        OnPropertyChanged(nameof(HasNoFlights));
        RebuildBurns();
    }

    /// <summary>
    /// Stores a changed flight and keeps showing it. Swapping it into <see cref="Flights"/> makes the Flights list drop its
    /// selection (it writes null back to <see cref="SelectedFlight"/>), which would close the flight: that write is ignored,
    /// and the list is pointed at the new copy.
    /// </summary>
    private void Store(Flight flight)
    {
        _flights.Update(flight);
        var i = Flights.ToList().FindIndex(f => f.Id == flight.Id);
        _selected = flight;
        _storing = true;
        try
        {
            if (i >= 0) Flights[i] = flight;
        }
        finally
        {
            _storing = false;
        }
        OnPropertyChanged(nameof(SelectedFlight));
        RebuildBurns();
    }

    private bool _storing; // swapping a re-planned flight into the list

    private void RebuildBurns()
    {
        Burns.Clear();
        if (_selected is { } f)
            for (var i = 0; i < f.Burns.Count; i++) Burns.Add(new FlightBurnRow(i + 1, f.Burns[i]));
        RebuildMissionSteps();
        foreach (var name in new[]
                 {
                     nameof(HasFlight), nameof(HasNoSelection), nameof(NoSelectionText), nameof(KindText), nameof(CraftText), nameof(Note), nameof(HasNote), nameof(PlannedText), nameof(HasApproach),
                     nameof(ApproachTitle), nameof(ApproachSteps), nameof(HasMission), nameof(MissionTotalText),
                 })
            OnPropertyChanged(name);
        RefreshLive();
    }

    /// <summary>Countdowns, which burns are on the craft, the drift, and the game line. Runs every time the game is read.</summary>
    private void RefreshLive()
    {
        foreach (var name in new[] { nameof(GameText), nameof(GameTimeText), nameof(CanSend) }) OnPropertyChanged(name);
        var now = _sync.Now;
        var nodes = _sync.Link.Game?.ActiveNodes ?? [];
        var nextSet = false;
        foreach (var row in Burns)
        {
            var ut = row.Burn.Ut;
            row.Countdown = now is not { } t ? "" : ut >= t ? "in " + Duration(ut - t) : Duration(t - ut) + " ago";
            var onCraft = nodes.Any(n => Math.Abs(n.Ut - ut) < 1 && Math.Abs(n.Prograde - row.Burn.Burn.Prograde) < 0.5);
            row.State = now is { } t2 && ut < t2 ? BurnState.Passed : onCraft ? BurnState.OnCraft : !nextSet ? BurnState.Next : BurnState.Upcoming;
            if (row.State is BurnState.Next or BurnState.OnCraft) nextSet = true;
        }
        RefreshDrift(now);
        ReplanAfterBurn(now);
        AdvanceMission(now);
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Once the craft has finished a mission step (in orbit after the launch, around the body a trip goes to, on the
    /// ground…), moves on to the next and plans it, and every step after it, from the craft's real orbit. Only updates
    /// the flight: nothing is sent to KSP.
    /// </summary>
    private void AdvanceMission(double? now)
    {
        if (_busy || !_sync.Link.IsConnected || _selected is not { Kind: FlightKind.Mission, Craft: not null } f || now is not { } t
            || _heldAt == (f.Id, f.StepIndex) || Situation(f, t) is not { } here) return;
        if (MissionFlights.Current(f.Steps, f.StepIndex, here, f.StepPlans, _sync.StockBodies(), SettleS) == f.StepIndex) return;
        _ = ReplanMissionAsync(f, advance: true);
    }

    /// <summary>Where the mission's craft is now, for planning; null without a craft, or when it can't be found.</summary>
    private MissionSituation? Situation(Flight f, double now)
    {
        if (f.Craft is not { } craft || _sync.WhereIs(craft) is not var (body, path, landed)) return null;
        // A craft on the ground the game doesn't say the body of: where the step being flown starts.
        body ??= landed ? f.StepPlans.FirstOrDefault(p => p.Step == f.StepIndex)?.StartBodyName ?? f.MissionStart?.BodyName : null;
        if (body is null || !landed && path is null) return null;
        return new MissionSituation(body, path?.ToElements(), landed, now);
    }

    /// <summary>
    /// Plans a mission from its craft (the step being flown from the real orbit, the rest from where each leaves the
    /// craft), or without one from where the mission starts. With <paramref name="advance"/>, first moves past the
    /// steps the craft has finished.
    /// </summary>
    private async Task ReplanMissionAsync(Flight f, bool advance)
    {
        // Without game time (no save, not connected) the mission is still shown planned, from the start of the game.
        var now = _sync.Now ?? 0;
        _busy = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var bodies = _sync.StockBodies();
            // Landings at a spot need their body's rotation; read it (live, or from the bake) before planning.
            var rotations = new Dictionary<string, double?>();
            foreach (var name in f.Steps.Skip(f.StepIndex).Where(s => s is { Kind: MissionStepKind.Land, Landing: not null }).Select(s => s.BodyName).Distinct())
                if (bodies.GetValueOrDefault(name) is { } body) rotations[name] = await _rotation.InitialRotationDegAsync(body);
            var context = new MissionPlanContext(bodies, f.LeadS, rotations.GetValueOrDefault, _rotation.Bake,
                t => t.SaveVesselId is { } id ? _sync.OrbitOf(new FlightCraft(t.Name, id))?.ToElements() : null);

            var here = Situation(f, now);
            var from = f.StepIndex;
            if (advance && here is not null) f = f with { StepIndex = MissionFlights.Current(f.Steps, f.StepIndex, here, f.StepPlans, bodies, SettleS) };
            var was = f.StepPlans.FirstOrDefault(p => p.Step == f.StepIndex);
            var flight = f;
            var planned = await Task.Run(() => here is null ? MissionFlights.PlanFromStart(flight, now, context) : MissionFlights.Replan(flight, here, context));
            Store(planned);
            Status = MissionStatus(planned, from, was, here is not null, now)
                     + (_sync.Now is null ? " No game time yet, so it's planned from Year 1, Day 1: connect to KSP or link a save to plan from now." : "");
        }
        catch (ArgumentException ex)
        {
            Status = $"Couldn't plan the mission: {ex.Message}";
        }
        finally
        {
            _busy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>What a mission re-plan did, in a line or two: steps finished, what the step being flown costs now.</summary>
    private string MissionStatus(Flight f, int from, MissionStepPlan? was, bool live, double now)
    {
        var parts = new List<string>();
        var done = f.StepIndex - from;
        if (done > 0) parts.Add(done == 1 ? $"Step {from + 1} done." : $"Steps {from + 1}–{f.StepIndex} done.");
        if (f.StepIndex >= f.Steps.Count) return string.Join(" ", parts.Append("That's the whole mission flown."));
        var plan = f.StepPlans.FirstOrDefault(p => p.Step == f.StepIndex);
        var where = live ? $"from {f.Craft!.Name}'s real {(plan?.StartOrbit is null ? "position" : "orbit")}" : "from where the mission starts";
        var step = $"step {f.StepIndex + 1} ({f.Steps[f.StepIndex].Title})";
        if (plan is { IsPlanned: false }) parts.Add($"Couldn't plan {step}: {plan.Problem}");
        else
        {
            var cost = plan is null || plan.Burns.Count == 0 ? "" : $": {Format.Speed0(plan.DeltaV)}" +
                (was is { IsPlanned: true, Burns.Count: > 0 } && Math.Abs(was.DeltaV - plan.DeltaV) >= 1 ? $" (was {Format.Speed0(was.DeltaV)})" : "");
            parts.Add($"Planned {step} {where} at {_calendar.FormatDate(now)}{cost}.");
            var after = f.StepPlans.Count(p => p.Step > f.StepIndex && p.IsPlanned);
            if (after > 0) parts.Add(after == 1 ? "The step after it follows on from there." : $"The {after} steps after it follow on from there.");
        }
        if (!live && f.Craft is null) parts.Add("Switch to the craft in KSP and click \"Use the craft I'm flying\" to plan from its real orbit.");
        return string.Join(" ", parts);
    }

    /// <summary>Moves a mission on a step (or back one) by hand, and plans it from the craft (or from where the mission starts).</summary>
    private async Task StepToAsync(int by)
    {
        if (_selected is not { Kind: FlightKind.Mission } f) return;
        var index = Math.Clamp(f.StepIndex + by, 0, f.Steps.Count);
        // Back a step: stay there, even if the craft looks to have finished it, until the player moves on.
        _heldAt = by < 0 ? (f.Id, index) : null;
        await ReplanMissionAsync(f with { StepIndex = index }, advance: false);
    }

    /// <summary>
    /// Once a rendezvous burn has been flown (its node time is <see cref="SettleS"/> behind), re-plans the rest of the leg
    /// from the craft's real orbit, once per burn. Only updates the flight: nothing is sent to KSP.
    /// </summary>
    private void ReplanAfterBurn(double? now)
    {
        if (_busy || !_sync.Link.IsConnected || _selected is not { Kind: FlightKind.Rendezvous, Craft: not null } f || now is not { } t) return;
        var flown = f.Burns.Where(b => !b.ByReleasedCraft && b.Ut > f.PlannedUt && b.Ut + SettleS < t).Select(b => b.Ut).DefaultIfEmpty(double.NaN).Max();
        if (double.IsNaN(flown) || _autoTried == (f.Id, flown) || !f.Burns.Any(b => !b.ByReleasedCraft && b.Ut > t)) return;
        _autoTried = (f.Id, flown);
        ReplanRest(f, t, afterBurn: true);
    }

    /// <summary>
    /// Re-plans the rest of the current rendezvous leg, keeping its steps and meeting time (docs/MATH.md §8.4). False when
    /// the leg hasn't started, so the whole flight is planned again instead.
    /// </summary>
    private bool ReplanRest(Flight f, double now, bool afterBurn)
    {
        if (Body(f.BodyName) is not { } body || f.Craft is not { } craft || _sync.OrbitOf(craft) is not { } orbit || orbit.BodyName != f.BodyName) return false;
        try
        {
            var start = orbit.ToElements();
            var rest = FlightReplan.RendezvousRest(f, body, start, TargetOrbit, now, Math.Min(f.LeadS, CorrectionLeadS));
            if (rest is null) return false;
            Store(f with { Burns = rest.Burns, Targets = rest.Targets, StartOrbit = start, PlannedUt = now });
            var onCraft = _sync.Link.Game?.ActiveNodes.Count > 0 ? " Send all burns to replace the nodes on the craft." : "";
            Status = (afterBurn ? $"Burn flown, so the rest was re-planned from {craft.Name}'s real orbit. " : "Re-planned the rest of this leg. ") + rest.Summary + onCraft;
        }
        catch (ArgumentException ex)
        {
            Status = ex.Message;
        }
        return true;
    }

    /// <summary>A target's orbit now: live while connected, else as planned.</summary>
    private OrbitalElements TargetOrbit(FlightTarget t) =>
        (t.SaveVesselId is { } id ? _sync.OrbitOf(new FlightCraft(t.Name, id))?.ToElements() : null) ?? t.Orbit;

    private void RefreshDrift(double? now)
    {
        IsDrifting = false;
        if (_selected is not { } f || now is not { } t) { DriftText = ""; return; }
        if (f.Craft is not { } craft) { DriftText = ""; return; }
        if (f.Kind == FlightKind.Mission && MissionDrift(f, craft) is { } text)
        {
            DriftText = text;
            return;
        }
        if (Body(f.BodyName) is not { } body) { DriftText = ""; return; }
        var orbit = _sync.OrbitOf(craft);
        if ((f.Kind == FlightKind.Transfer || IsTrip(f)) && _sync.PathOf(craft)?.BodyName is { } around && around != f.BodyName)
        {
            // On the way: into the parent's sphere after the ejection, or arrived.
            IsDrifting = around != f.DestinationName;
            DriftText = around == f.DestinationName
                ? $"{craft.Name} has reached {around}: capture with the burn above, or Circularize at periapsis in the From here pane."
                : $"{craft.Name} is around {around} now: Re-plan from here to correct the course from its real path.";
            return;
        }
        if (orbit is null || orbit.BodyName != f.BodyName)
        {
            DriftText = f.TripFromBody is not null && orbit?.BodyName is { } on
                ? $"{craft.Name} is around {on}: fly the trip's burns to {f.BodyName}. Re-plan from here corrects them from the real path."
                : f.Approach is not null && orbit?.BodyName is { } there
                ? $"{craft.Name} is around {there}: fly the steps above to {f.BodyName}'s orbit, then Re-plan from here."
                : $"{craft.Name} isn't in orbit around {f.BodyName} right now.";
            return;
        }
        if (f.DriftM(orbit.ToElements(), t, body.Mu) is not { } drift)
        {
            DriftText = "Not in the planned orbit yet.";
            return;
        }
        IsDrifting = drift > DriftThresholdM;
        DriftText = IsDrifting
            ? $"{craft.Name} is {Distance(drift)} off the planned path. " +
              (CanReplan(f) ? "Re-plan to fly the rest from where it really is." : "Plan it again from here in the From here pane.")
            : $"On plan: {Distance(drift)} from the planned path.";
    }

    /// <summary>
    /// What to say about a mission's craft when it isn't simply on (or off) the planned path: the mission is flown, a
    /// launch is still to fly, or the craft is around another body than the step's burns are for. Null otherwise.
    /// </summary>
    private string? MissionDrift(Flight f, FlightCraft craft)
    {
        if (f.StepIndex >= f.Steps.Count) return "The whole mission is flown.";
        var step = f.Steps[f.StepIndex];
        if (_sync.WhereIs(craft) is not var (body, path, landed)) return $"{craft.Name} isn't in the game or the save right now.";
        if (step.Kind == MissionStepKind.Launch)
            return landed ? $"{craft.Name} is on the ground: launch when you're ready. Once it's in orbit, the next step is planned from there."
                : $"Climbing to orbit: once {craft.Name} is in a stable orbit, the next step is planned from it.";
        if (landed) return $"{craft.Name} is on the ground{(body is null ? "" : " on " + body)}.";
        if (body is not null && body != f.BodyName)
        {
            IsDrifting = true;
            return step.Kind == MissionStepKind.Trip
                ? $"{craft.Name} is around {body} on the way to {step.BodyName}: Re-plan from here to correct the course from its real path."
                : $"{craft.Name} is around {body}, but this step's burns are for {f.BodyName}'s orbit: Re-plan from here.";
        }
        return path is null ? $"{craft.Name} isn't on a path the planner can follow right now." : null;
    }

    // ---------------- Commands ----------------

    /// <summary>The craft being flown becomes the flight's craft, and the flight is planned from its orbit.</summary>
    private async Task UseActiveCraftAsync()
    {
        if (_selected is not { } f || _sync.ActiveCraft is not { } craft) return;
        if (f.Craft is { } current && current != craft && !Confirm($"This flight is flown by {current.Name}. Fly it with {craft.Name} instead?")) return;
        Store(f with { Craft = craft });
        await ReplanAsync();
    }

    /// <summary>Plans the rest of the flight again from where the craft really is, starting at least the lead time from now.</summary>
    private async Task ReplanAsync()
    {
        if (_selected is { Kind: FlightKind.Mission } mission)
        {
            await ReplanMissionAsync(mission, advance: _heldAt != (mission.Id, mission.StepIndex));
            return;
        }
        if (_selected is not { } f || f.Craft is not { } craft || !CanReplan(f)) return;
        if (Body(f.BodyName) is not { } body) { Status = $"{f.BodyName} isn't in the planner's bodies."; return; }
        if (_sync.Now is not { } now) { Status = "No game time yet: connect to KSP or link a save."; return; }
        if (IsTrip(f))
        {
            await ReplanTripAsync(f, craft, now);
            return;
        }
        if (f.Kind == FlightKind.Transfer)
        {
            await ReplanTransferAsync(f, craft, now);
            return;
        }
        if (f.Kind == FlightKind.Landing)
        {
            await ReplanLandingAsync(f, body, craft, now);
            return;
        }
        var orbit = _sync.OrbitOf(craft);
        // A rendezvous that starts with a trip from another body: plan it again from wherever the craft is on the way.
        if (f is { Kind: FlightKind.Rendezvous, TripFromBody: not null } && orbit?.BodyName is { } around && around != f.BodyName && Body(around) is { } aroundBody)
        {
            _busy = true;
            CommandManager.InvalidateRequerySuggested();
            try
            {
                if (await ReplanRendezvousAsync(f, body, orbit.ToElements(), now, aroundBody) is not { } trip) return;
                Store(trip);
                Status = $"Re-planned the trip from {craft.Name}'s orbit around {around} at {_calendar.FormatDate(now)}.";
            }
            catch (ArgumentException ex)
            {
                Status = $"Couldn't re-plan: {ex.Message}";
            }
            finally
            {
                _busy = false;
                CommandManager.InvalidateRequerySuggested();
            }
            return;
        }
        if (orbit is null || orbit.BodyName != f.BodyName)
        {
            Status = f.Approach is not null && orbit?.BodyName is { } there
                ? $"{craft.Name} is still around {there}. Fly the steps to {f.BodyName}'s orbit first, then re-plan from there."
                : $"{craft.Name} isn't in orbit around {f.BodyName}, so there's nothing to plan from.";
            return;
        }
        // Partway through a rendezvous leg: keep its steps and fix the rest, rather than starting the leg over.
        if (f.Kind == FlightKind.Rendezvous && ReplanRest(f, now, afterBurn: false)) return;

        _busy = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var start = orbit.ToElements();
            var replanned = f.Kind switch
            {
                FlightKind.Resonant => ReplanResonant(f, body, start, now),
                _ => await ReplanRendezvousAsync(f, body, start, now),
            };
            if (replanned is null) return;
            Store(replanned);
            Status = $"Re-planned from {craft.Name}'s orbit at {_calendar.FormatDate(now)}.";
        }
        catch (ArgumentException ex)
        {
            Status = $"Couldn't re-plan: {ex.Message}";
        }
        finally
        {
            _busy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// Plans a transfer flight again from wherever the craft is: its orbit around the origin (the window's burns), or its
    /// path on the way (a course correction). docs/MATH.md §3.8.
    /// </summary>
    private async Task ReplanTransferAsync(Flight f, FlightCraft craft, double now)
    {
        if (_sync.PathOf(craft) is not { BodyName: { } there } path || Body(there) is not { } around)
        {
            Status = $"{craft.Name} isn't in orbit, so there's nothing to plan from.";
            return;
        }
        _busy = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var bodies = _sync.StockBodies().Values.ToList();
            var replanned = await Task.Run(() => TransferFlights.Plan(f, bodies, around, path.ToElements(), now, f.LeadS));
            Store(replanned);
            Status = $"Planned from {craft.Name}'s path around {around.Name} at {_calendar.FormatDate(now)}.";
        }
        catch (ArgumentException ex)
        {
            Status = $"Couldn't re-plan: {ex.Message}";
        }
        finally
        {
            _busy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// Plans a From here trip (Go to a body) again from wherever the craft is: its orbit before it leaves, or once on the
    /// way, a course correction from its real path. docs/MATH.md §3.8.
    /// </summary>
    private async Task ReplanTripAsync(Flight f, FlightCraft craft, double now)
    {
        if (_sync.PathOf(craft) is not { BodyName: { } there } path || Body(there) is not { } around)
        {
            Status = $"{craft.Name} isn't in orbit, so there's nothing to plan from.";
            return;
        }
        if (there == f.DestinationName)
        {
            Status = $"{craft.Name} has reached {there}: capture with the burn above, or Circularize at periapsis in the From here pane.";
            return;
        }
        if (Body(f.DestinationName!) is not { } to)
        {
            Status = $"{f.DestinationName} isn't in the planner's bodies.";
            return;
        }
        _busy = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var bodies = _sync.StockBodies().Values.ToList();
            var start = path.ToElements();
            var options = await Task.Run(() => TripPlanner.Options(around, to, start, now + Math.Min(f.LeadS, CorrectionLeadS), f.AltitudeM, bodies,
                f.ArrivalInclinationDeg));
            var trip = options[0]; // the cheapest
            Store(f with
            {
                BodyName = around.Name,
                StartOrbit = start,
                Burns = trip.Burns,
                PlannedUt = now,
                Note = trip.Warnings.Count > 0 ? string.Join(" ", trip.Warnings) : null,
            });
            var onCraft = _sync.Link.Game?.ActiveNodes.Count > 0 ? " Send all burns to replace the nodes on the craft." : "";
            Status = (trip.IsCorrection ? $"Corrected the course from {craft.Name}'s real path" : $"Planned from {craft.Name}'s orbit around {around.Name}") +
                     $" at {_calendar.FormatDate(now)}: {Format.Speed0(trip.DeltaV)} in all." + onCraft;
        }
        catch (ArgumentException ex)
        {
            Status = $"Couldn't re-plan: {ex.Message}";
        }
        finally
        {
            _busy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Plans a landing again from the craft's orbit around the landing body. docs/MATH.md §2.9.</summary>
    private async Task ReplanLandingAsync(Flight f, CelestialBody body, FlightCraft craft, double now)
    {
        if (_sync.OrbitOf(craft) is not { } orbit || orbit.BodyName != body.Name)
        {
            Status = $"{craft.Name} isn't in orbit around {body.Name}. Get there first (the From here pane's Go to…), then re-plan.";
            return;
        }
        _busy = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            if (await _rotation.InitialRotationDegAsync(body) is not { } theta0)
            {
                Status = BodyRotation.Missing(body);
                return;
            }
            var bake = _rotation.Bake(body.Name);
            var start = orbit.ToElements();
            var planned = await Task.Run(() => LandingFlights.Plan(f, body, start, now, theta0, bake));
            Store(planned);
            Status = $"Planned from {craft.Name}'s orbit at {_calendar.FormatDate(now)}: {Format.Speed0(planned.Burns.Sum(b => b.DeltaV))} in all.";
        }
        catch (ArgumentException ex)
        {
            Status = $"Couldn't plan the landing: {ex.Message}";
        }
        finally
        {
            _busy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private Flight? ReplanResonant(Flight f, CelestialBody body, OrbitalElements start, double now)
    {
        var released = f.SatellitesReleased + f.Burns.Count(b => b.ByReleasedCraft && b.Ut < now);
        var left = f.SatelliteCount - released;
        if (left <= 0)
        {
            Status = "Every satellite has been released.";
            return null;
        }
        var carrier = ResonantOrbitCalculator.Calculate(new ResonantOrbitRequest(body, f.SatelliteCount, f.AltitudeM, f.PreferDiveOrbit));
        // Once one is out, the rest must follow on the very next passes, however soon, to keep the spacing.
        var plan = ResonantDeployment.Plan(body, carrier, start, left, now + f.LeadS, released + 1, released > 0 ? now : null);
        return f with
        {
            StartOrbit = plan.StartOrbit,
            Burns = plan.Burns,
            PlannedUt = now,
            SatellitesReleased = released,
            Note = plan.Warnings.Count > 0 ? string.Join(" ", plan.Warnings) : null,
        };
    }

    /// <param name="from">The body the craft is around when that isn't <paramref name="body"/>: the trip there comes first (§4.6).</param>
    private async Task<Flight?> ReplanRendezvousAsync(Flight f, CelestialBody body, OrbitalElements start, double now, CelestialBody? from = null)
    {
        var left = f.Targets.Where(t => t.ArrivalUt > now).ToList();
        if (left.Count == 0)
        {
            Status = "Every target has been reached.";
            return null;
        }
        // Targets on their current orbits (live while connected), else as planned.
        var targets = left.Select(t => new RendezvousTarget(t.Name,
            (t.SaveVesselId is { } id ? _sync.OrbitOf(new FlightCraft(t.Name, id))?.ToElements() : null) ?? t.Orbit)).ToList();
        var request = new RendezvousRequest(body, targets, now + f.LeadS, f.StayS, new RendezvousTarget(f.Craft!.Name, start),
            DeorbitPeriapsisAltitudeM: f.DeorbitPeriapsisAltitudeM)
        {
            From = from is null ? null : new TripStart(from, _sync.StockBodies().Values.ToList()),
        };
        var plan = await Task.Run(() => Rendezvous.Plan(request, new RendezvousChoices()));
        return f with
        {
            StartOrbit = start,
            Burns = RendezvousBurns(plan),
            Targets = left.Zip(plan.Legs, (t, leg) => t with { Orbit = leg.Target.Orbit, ArrivalUt = leg.ArrivalUt }).ToList(),
            PlannedUt = now,
            Note = RendezvousViewModel.SkippedCrossingNote(request, plan, now, "the flight's", canLower: false),
            Approach = null, // planned from around the body, so the way there is flown
            TripFromBody = from?.Name, // once around the body, the trip is done
        };
    }

    /// <summary>A rendezvous plan's burns as flight burns, titled with the craft they're for.</summary>
    public static List<FlightBurn> RendezvousBurns(RendezvousPlan plan) => RendezvousFlights.Burns(plan);

    /// <summary>The flight craft's own burns still to come (not the released satellites').</summary>
    private List<FlightBurn> OwnFutureBurns() =>
        Burns.Where(r => !r.Burn.ByReleasedCraft && r.State != BurnState.Passed).Select(r => r.Burn).ToList();

    private FlightBurnRow? NextOwnBurn() =>
        Burns.FirstOrDefault(r => !r.Burn.ByReleasedCraft && r.State is BurnState.Next or BurnState.Upcoming);

    /// <summary>
    /// Puts burns on the craft being flown as maneuver nodes: added to its nodes, or replacing them. Asks first when it
    /// looks like the wrong craft (the flight's craft for its own burns, a released satellite for the satellites').
    /// </summary>
    private async Task SendAsync(IReadOnlyList<FlightBurn> burns, bool replace)
    {
        if (_selected is not { } f || burns.Count == 0) return;
        _busy = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            if (await _sync.Link.ActiveVesselNodesAsync() is not var (vessel, nodes))
            {
                Status = "Switch to the craft in KSP's flight view first.";
                return;
            }

            var active = _sync.ActiveCraft;
            var questions = new List<string>();
            if (f.Craft is null)
                questions.Add($"This flight has no craft yet, so its burns may not fit {vessel}'s orbit. \"Use the craft I'm flying\" plans it from {vessel}.");
            else if (burns.All(b => b.ByReleasedCraft) && _sync.IsSameCraft(active, f.Craft))
                questions.Add($"This burn is for a released satellite, but you're flying the carrier, {vessel}.");
            else if (burns.Any(b => !b.ByReleasedCraft) && !_sync.IsSameCraft(active, f.Craft))
                questions.Add($"You're flying {vessel}, but this flight is flown by {f.Craft.Name}.");
            if (burns.Any(b => !b.ByReleasedCraft) && active is not null && _sync.OrbitOf(active)?.BodyName is { } there
                && there != f.BodyName && there != f.TripFromBody) // a trip's first burns are around the body it starts from
                questions.Add($"{vessel} is around {there}, but these burns are for {f.BodyName}'s orbit." +
                              (f.Approach is null ? "" : $" Fly to {f.BodyName} first, then Re-plan from here."));
            if (replace && nodes > 0) questions.Add($"{vessel}'s {(nodes == 1 ? "maneuver node" : $"{nodes} maneuver nodes")} will be replaced.");
            var what = burns.Count == 1 ? "the node" : $"{burns.Count} nodes";
            if (questions.Count > 0 && !Confirm(string.Join("\n\n", questions) + $"\n\nSend {what} to {vessel}?")) return;

            var sent = await _sync.Link.SendNodesAsync(burns.Select(b => (b.Ut, b.Burn)).ToList(), replace);
            Status = (sent.Count == 1 ? $"Sent 1 node to {sent.Vessel}." : $"Sent {sent.Count} nodes to {sent.Vessel}.")
                     + (sent.Skipped == 0 ? "" : $" {sent.Skipped} burn time{(sent.Skipped == 1 ? " has" : "s have")} already passed; re-plan for those.");
        }
        catch (Exception ex) when (ex is System.InvalidOperationException or KRPC.Client.RPCException)
        {
            Status = ex.Message;
        }
        catch (Exception)
        {
            Status = "Lost the connection to KSP.";
        }
        finally
        {
            _busy = false;
            RefreshLive();
        }
    }

    private void Delete()
    {
        if (_selected is not { } f || !Confirm($"Delete the flight \"{f.Name}\"? Nodes already on your craft stay in KSP.")) return;
        _flights.Delete(f.Id);
        var i = Flights.IndexOf(f);
        Flights.Remove(f);
        SelectedFlight = Flights.Count == 0 ? null : Flights[Math.Min(i, Flights.Count - 1)];
        OnPropertyChanged(nameof(HasNoFlights));
    }

    // ---------------- Helpers ----------------

    private CelestialBody? Body(string name) => _sync.StockBodies().GetValueOrDefault(name);

    /// <summary>A From here trip (Go to a body): one-off burns that know where they're going, so they can be planned again.</summary>
    private static bool IsTrip(Flight f) => f.Kind == FlightKind.Burns && f.DestinationName is not null;

    /// <summary>Every flight but one-off burns (other than a trip) keeps what it needs to be planned again.</summary>
    private static bool CanReplan(Flight f) => f.Kind != FlightKind.Burns || IsTrip(f);

    private string Duration(double seconds) => seconds < 60 ? $"{seconds:0} s" : _calendar.FormatDuration(seconds);

    private static string Distance(double metres) => metres < 1000 ? $"{metres:0} m" : $"{metres / 1000:#,0.#} km";

    internal static string OrbitText(OrbitalElements orbit, CelestialBody body)
    {
        var pe = orbit.SemiMajorAxisM * (1 - orbit.Eccentricity) - body.EquatorialRadiusM;
        if (orbit.Eccentricity >= 1) return $"a {pe / 1000:#,0.#} km pass, escaping";
        var ap = orbit.SemiMajorAxisM * (1 + orbit.Eccentricity) - body.EquatorialRadiusM;
        return Math.Abs(ap - pe) < 2_000 ? $"{pe / 1000:#,0.#} km" : $"{pe / 1000:#,0.#} × {ap / 1000:#,0.#} km";
    }
}
