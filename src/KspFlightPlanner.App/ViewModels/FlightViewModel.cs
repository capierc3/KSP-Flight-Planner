using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App.ViewModels;

public enum BurnState { Upcoming, Next, OnCraft, Passed }

/// <summary>One burn on the Flight tab: what and when, its node values, and whether it's on the craft yet.</summary>
public sealed class FlightBurnRow(int number, FlightBurn burn) : ObservableObject
{
    private string _countdown = "";
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

/// <summary>
/// The Flight tab: plans handed over from the planning tabs, flown against the live game. Shows each burn's countdown
/// and whether it's on the craft, sends burns as maneuver nodes, and re-plans from the craft's real orbit when it has
/// drifted from the plan.
/// </summary>
public sealed class FlightViewModel : ObservableObject
{
    /// <summary>Further than this from the planned path lights up Re-plan. docs/MATH.md §8.3.</summary>
    public const double DriftThresholdM = 1_000;

    private readonly FlightRepository _flights;
    private readonly SaveSync _sync;
    private readonly GameCalendar _calendar = GameCalendar.Kerbin;
    private Flight? _selected;
    private string? _status;
    private string _driftText = "";
    private bool _isDrifting;
    private bool _busy;

    public FlightViewModel(FlightRepository flights, SaveSync sync, PlannerEvents events)
    {
        _flights = flights;
        _sync = sync;
        events.OpenFlightRequested += id =>
        {
            Reload();
            SelectedFlight = Flights.FirstOrDefault(f => f.Id == id);
        };
        events.SaveChanged += Reload; // the open flight closes if it belongs to the last save
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
        ReplanCommand = new RelayCommand(() => _ = ReplanAsync(), () => _selected?.Craft is not null && !_busy);
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

    /// <summary>Asks the user a yes/no question; the view supplies the dialog.</summary>
    public Func<string, bool> Confirm { get; set; } = _ => true;

    public Flight? SelectedFlight
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            Status = null;
            RebuildBurns();
        }
    }

    public bool HasFlight => _selected is not null;
    public bool HasNoFlights => Flights.Count == 0;

    // ---------------- The game ----------------

    /// <summary>The live link, in a line: what's being flown, its orbit and target.</summary>
    public string GameText
    {
        get
        {
            var link = _sync.Link;
            if (!link.IsConnected) return "Not connected to KSP. Use Tools › Connect to KSP to follow the game and send burns.";
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
        _ => "",
    };

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
        _selected = Flights.FirstOrDefault(f => f.Id == keep);
        OnPropertyChanged(nameof(SelectedFlight));
        OnPropertyChanged(nameof(HasNoFlights));
        RebuildBurns();
    }

    /// <summary>Stores a changed flight and shows it.</summary>
    private void Store(Flight flight)
    {
        _flights.Update(flight);
        var i = Flights.IndexOf(_selected!);
        _selected = flight;
        if (i >= 0) Flights[i] = flight;
        OnPropertyChanged(nameof(SelectedFlight));
        RebuildBurns();
    }

    private void RebuildBurns()
    {
        Burns.Clear();
        if (_selected is { } f)
            for (var i = 0; i < f.Burns.Count; i++) Burns.Add(new FlightBurnRow(i + 1, f.Burns[i]));
        foreach (var name in new[]
                 {
                     nameof(HasFlight), nameof(KindText), nameof(CraftText), nameof(Note), nameof(HasNote), nameof(PlannedText), nameof(HasApproach),
                     nameof(ApproachTitle), nameof(ApproachSteps),
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
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshDrift(double? now)
    {
        IsDrifting = false;
        if (_selected is not { } f || now is not { } t) { DriftText = ""; return; }
        if (f.Craft is not { } craft) { DriftText = ""; return; }
        if (Body(f.BodyName) is not { } body) { DriftText = ""; return; }
        var orbit = _sync.OrbitOf(craft);
        if (orbit is null || orbit.BodyName != f.BodyName)
        {
            DriftText = f.Approach is not null && orbit?.BodyName is { } there
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
            ? $"{craft.Name} is {Distance(drift)} off the planned path. Re-plan to fly the rest from where it really is."
            : $"On plan: {Distance(drift)} from the planned path.";
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
        if (_selected is not { } f || f.Craft is not { } craft) return;
        if (Body(f.BodyName) is not { } body) { Status = $"{f.BodyName} isn't in the planner's bodies."; return; }
        if (_sync.Now is not { } now) { Status = "No game time yet: connect to KSP or link a save."; return; }
        var orbit = _sync.OrbitOf(craft);
        if (orbit is null || orbit.BodyName != f.BodyName)
        {
            Status = f.Approach is not null && orbit?.BodyName is { } there
                ? $"{craft.Name} is still around {there}. Fly the steps to {f.BodyName}'s orbit first, then re-plan from there."
                : $"{craft.Name} isn't in orbit around {f.BodyName}, so there's nothing to plan from.";
            return;
        }

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
        var plan = ResonantDeployment.Plan(body, carrier, start, left, now + f.LeadS, released + 1);
        return f with
        {
            StartOrbit = plan.StartOrbit,
            Burns = plan.Burns,
            PlannedUt = now,
            SatellitesReleased = released,
            Note = plan.Warnings.Count > 0 ? string.Join(" ", plan.Warnings) : null,
        };
    }

    private async Task<Flight?> ReplanRendezvousAsync(Flight f, CelestialBody body, OrbitalElements start, double now)
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
            DeorbitPeriapsisAltitudeM: f.DeorbitPeriapsisAltitudeM);
        var plan = await Task.Run(() => Rendezvous.Plan(request, new RendezvousChoices()));
        return f with
        {
            StartOrbit = start,
            Burns = RendezvousBurns(plan),
            Targets = left.Zip(plan.Legs, (t, leg) => t with { Orbit = leg.Target.Orbit, ArrivalUt = leg.ArrivalUt }).ToList(),
            PlannedUt = now,
            Note = null,
            Approach = null, // planned from around the body, so the way there is flown
        };
    }

    /// <summary>A rendezvous plan's burns as flight burns, titled with the craft they're for.</summary>
    public static List<FlightBurn> RendezvousBurns(RendezvousPlan plan)
    {
        var burns = plan.Legs.SelectMany(l => l.Maneuvers.Select(m => new FlightBurn($"{Title(m)}: {l.Target.Name}", m.Ut, m.Burn))).ToList();
        if (plan.Deorbit is { } d) burns.Add(new FlightBurn($"Deorbit {plan.Legs[^1].Target.Name}", d.Ut, d.Burn) { Note = "Docked with the target." });
        return burns;
    }

    private static string Title(Maneuver m) => m.Kind switch
    {
        ManeuverKind.PlaneChange => "Match planes",
        ManeuverKind.Phasing => "Phasing orbit",
        ManeuverKind.Transfer => "Transfer burn",
        ManeuverKind.MatchVelocity => "Match velocity",
        ManeuverKind.Deorbit => "Deorbit",
        _ => m.Kind.ToString(),
    };

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
            else if (burns.All(b => b.ByReleasedCraft) && active == f.Craft)
                questions.Add($"This burn is for a released satellite, but you're flying the carrier, {vessel}.");
            else if (burns.Any(b => !b.ByReleasedCraft) && active != f.Craft)
                questions.Add($"You're flying {vessel}, but this flight is flown by {f.Craft.Name}.");
            if (burns.Any(b => !b.ByReleasedCraft) && active is not null && _sync.OrbitOf(active)?.BodyName is { } there && there != f.BodyName)
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

    private string Duration(double seconds) => seconds < 60 ? $"{seconds:0} s" : _calendar.FormatDuration(seconds);

    private static string Distance(double metres) => metres < 1000 ? $"{metres:0} m" : $"{metres / 1000:#,0.#} km";

    private static string OrbitText(OrbitalElements orbit, CelestialBody body)
    {
        var pe = orbit.SemiMajorAxisM * (1 - orbit.Eccentricity) - body.EquatorialRadiusM;
        var ap = orbit.SemiMajorAxisM * (1 + orbit.Eccentricity) - body.EquatorialRadiusM;
        return Math.Abs(ap - pe) < 2_000 ? $"{pe / 1000:#,0.#} km" : $"{pe / 1000:#,0.#} × {ap / 1000:#,0.#} km";
    }
}
