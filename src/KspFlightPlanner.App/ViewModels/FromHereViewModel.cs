using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App.ViewModels;

public enum QuickAction { CircularizeAtApoapsis, CircularizeAtPeriapsis, SetApoapsis, SetPeriapsis, GoTo, MatchPlanes, Rendezvous, Deorbit }

public sealed record QuickActionOption(QuickAction Action, string Name)
{
    public override string ToString() => Name;
}

/// <summary>An entry in the Go to list: a body, or (<see cref="Body"/> null) whatever is targeted in KSP, following the game.</summary>
public sealed class DestinationOption(CelestialBody? body) : ObservableObject
{
    private string _name = body?.Name ?? "KSP's target (none set)";

    public CelestialBody? Body { get; } = body;
    public bool IsTarget => Body is null;
    public string Name { get => _name; set => SetProperty(ref _name, value); }

    public override string ToString() => Name;
}

/// <summary>One way of making a trip, as a row to pick: what it's best at, and when it leaves, how long it takes, its Δv.</summary>
public sealed record TripOptionRow(TripOption Option, string Text)
{
    public string Label => Option.Label;
    public bool HasLabel => Option.Label.Length > 0;
}

/// <summary>
/// Fly mode's From here pane: "I'm here, now I want to do X". Plans a one-off burn (or a rendezvous) from the live orbit
/// of the craft being flown, then sends it to KSP or keeps it as a flight. No mission or plan needed.
/// </summary>
public sealed class FromHereViewModel : ObservableObject
{
    /// <summary>The soonest a burn is planned: time to point the craft. A burn on a circular orbit goes here.</summary>
    public const double LeadS = 120;

    private readonly SaveSync _sync;
    private readonly FlightRepository _flights;
    private readonly PlannerEvents _events;
    private readonly GameCalendar _calendar = GameCalendar.Kerbin;
    private QuickActionOption _action;
    private string _altitudeText = "";
    private string? _status;
    private string _resultText = "";
    private bool _busy;
    private DestinationOption? _destination;
    private readonly DestinationOption _target = new(null);
    private string? _targetKey; // what KSP's target was when last looked at
    private TripOptionRow? _trip;
    private string? _destinationsFrom = "\0"; // the body the destination list is for ("" outside flight); "\0" until it's built

    // What the shown burns were planned from, for keeping them as a flight.
    private CelestialBody? _plannedBody;
    private OrbitalElements? _plannedFrom;
    private double _plannedUt;
    private RendezvousPlan? _plannedRendezvous;
    private FlightCraft? _plannedTarget;
    private CelestialBody? _plannedTo;

    public FromHereViewModel(SaveSync sync, FlightRepository flights, PlannerEvents events)
    {
        _sync = sync;
        _flights = flights;
        _events = events;
        _action = Actions[0];
        RefreshDestinations();
        sync.Link.GameUpdated += _ => RefreshLive();
        sync.Link.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(KspLink.IsConnected) or nameof(KspLink.InFlight)) RefreshLive();
        };

        PlanCommand = new RelayCommand(Plan, () => !_busy && Here() is not null);
        SendCommand = new RelayCommand(() => _ = SendAsync(), () => !_busy && _sync.Link.InFlight && Burns.Any(b => b.State != BurnState.Passed));
        FlyCommand = new RelayCommand(Fly, () => !_busy && Burns.Count > 0);
    }

    public IReadOnlyList<QuickActionOption> Actions { get; } =
    [
        new(QuickAction.CircularizeAtApoapsis, "Circularize at apoapsis"),
        new(QuickAction.CircularizeAtPeriapsis, "Circularize at periapsis"),
        new(QuickAction.SetApoapsis, "Change the apoapsis"),
        new(QuickAction.SetPeriapsis, "Change the periapsis"),
        new(QuickAction.GoTo, "Go to…"),
        new(QuickAction.MatchPlanes, "Match planes with the target"),
        new(QuickAction.Rendezvous, "Rendezvous with the target"),
        new(QuickAction.Deorbit, "Deorbit"),
    ];

    public ObservableCollection<FlightBurnRow> Burns { get; } = [];

    public RelayCommand PlanCommand { get; }
    public RelayCommand SendCommand { get; }
    public RelayCommand FlyCommand { get; }

    public QuickActionOption SelectedAction
    {
        get => _action;
        set
        {
            if (value is null || !SetProperty(ref _action, value)) return;
            _altitudeText = DefaultAltitude();
            foreach (var name in new[] { nameof(AltitudeText), nameof(NeedsAltitude), nameof(AltitudeLabel), nameof(ActionHint), nameof(NeedsDestination) }) OnPropertyChanged(name);
            Clear();
        }
    }

    public bool NeedsAltitude => _action.Action is QuickAction.SetApoapsis or QuickAction.SetPeriapsis or QuickAction.Deorbit
                                 || _action.Action == QuickAction.GoTo && !GoingToCraft;

    /// <summary>Go to is set to KSP's target, and that's a craft: a rendezvous, which needs no height.</summary>
    private bool GoingToCraft => _destination is { IsTarget: true } && _sync.Link.Game?.Target is not null;

    public string AltitudeLabel => _action.Action switch
    {
        QuickAction.SetApoapsis => "New apoapsis",
        QuickAction.SetPeriapsis => "New periapsis",
        QuickAction.GoTo => "Orbit there at",
        _ => "Periapsis",
    };

    public bool NeedsDestination => _action.Action == QuickAction.GoTo;

    /// <summary>
    /// Where the craft can go: KSP's target (a body or a craft) first, then from the body it's around, its moons, nearest
    /// first, and the planet if it's a moon.
    /// </summary>
    public IReadOnlyList<DestinationOption> Destinations { get; private set; } = [];

    public DestinationOption? SelectedDestination
    {
        get => _destination;
        set
        {
            if (!SetProperty(ref _destination, value)) return;
            _altitudeText = DefaultAltitude();
            OnPropertyChanged(nameof(AltitudeText));
            OnPropertyChanged(nameof(NeedsAltitude));
            Clear();
        }
    }

    /// <summary>The ways of making the trip planned (Go to), when there's more than one to choose from.</summary>
    public ObservableCollection<TripOptionRow> TripOptions { get; } = [];

    public bool HasTripOptions => TripOptions.Count > 1;

    /// <summary>The way of making the trip whose burns are shown.</summary>
    public TripOptionRow? SelectedTrip
    {
        get => _trip;
        set
        {
            if (value is null || !SetProperty(ref _trip, value)) return;
            ShowTrip(value.Option);
        }
    }

    public string AltitudeText
    {
        get => _altitudeText;
        set { if (SetProperty(ref _altitudeText, value)) Clear(); }
    }

    public string ActionHint => _action.Action switch
    {
        QuickAction.CircularizeAtApoapsis => "One burn at the next apoapsis, however soon. Just past it, falling back into the atmosphere, it's half a minute from now. On an orbit that's already round, two minutes.",
        QuickAction.CircularizeAtPeriapsis => "One burn at the next periapsis, however soon. On an orbit that's already round, it's two minutes from now. " +
                                              "Just arrived at a moon (inside its sphere of influence), this is the capture burn.",
        QuickAction.GoTo => "Anywhere in the system: the burns to leave, any course correction on the way, and the capture into a round orbit at this " +
                            "height there, going the way it spins. A height in the atmosphere brings you down (or aerobrakes) instead. Trips through a " +
                            "launch window (another planet, or moon to moon) offer a few windows to choose from; from a moon to another planet, it stops " +
                            "in the planet's orbit on the way. Already on the way? It plans a course correction. KSP's target follows the game: a body " +
                            "goes there, a craft plans a rendezvous with it.",
        QuickAction.SetApoapsis => "Burns at the periapsis, which stays where it is.",
        QuickAction.SetPeriapsis => "Burns at the apoapsis, which stays where it is.",
        QuickAction.MatchPlanes => "Target a craft, or a moon of the body you're orbiting, in KSP first. Burns at the cheaper of the next two plane crossings.",
        QuickAction.Rendezvous => "Target a craft in KSP first. Plans the plane match, phasing and intercept, like the Rendezvous tool.",
        _ => "Burns retrograde at the next apoapsis to drop the periapsis into the atmosphere (or onto the ground).",
    };

    /// <summary>What's being flown, where, and what's targeted.</summary>
    public string CraftText
    {
        get
        {
            var link = _sync.Link;
            if (!link.IsConnected) return "Not connected to KSP. Use Tools › Connect to KSP, then fly a craft.";
            if (!link.InFlight) return "In KSP, but not flying a craft. Switch to one to plan from where it is.";
            if (link.Game?.Active is not { } active) return "Waiting for the game…";
            if (Here() is not (var body, var orbit)) return $"{active.Name} isn't in a closed orbit, so there's nothing to plan from yet.";
            var target = link.Game.Target?.Name ?? link.Game.TargetBody;
            return $"{active.Name} · {FlightViewModel.OrbitText(orbit, body)} around {body.Name}" + (target is null ? " · no target" : $" · target {target}");
        }
    }

    public string? Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrEmpty(Status);

    /// <summary>Total Δv and the orbit the burns leave you in.</summary>
    public string ResultText { get => _resultText; private set => SetProperty(ref _resultText, value); }

    public bool HasBurns => Burns.Count > 0;

    // ---------------- Planning ----------------

    /// <summary>The craft being flown: the body it orbits and its orbit now. Null outside flight or off a closed orbit.</summary>
    private (CelestialBody Body, OrbitalElements Orbit)? Here()
    {
        if (!_sync.Link.InFlight || _sync.Link.Game?.Active?.Orbit is not { IsPath: true } orbit) return null;
        return orbit.BodyName is { } name && _sync.StockBodies().GetValueOrDefault(name) is { } body ? (body, orbit.ToElements()) : null;
    }

    private void Plan()
    {
        Clear();
        if (Here() is not (var body, var orbit) || _sync.Now is not { } now) return;
        var after = now + LeadS;
        if (orbit.Eccentricity >= 1 && _action.Action is not (QuickAction.CircularizeAtApoapsis or QuickAction.CircularizeAtPeriapsis or QuickAction.SetApoapsis
                or QuickAction.GoTo))
        {
            Status = $"You're on a path that escapes {body.Name}: capture first (circularize at periapsis, or change the apoapsis).";
            return;
        }
        try
        {
            List<FlightBurn> burns;
            switch (_action.Action)
            {
                case QuickAction.CircularizeAtApoapsis or QuickAction.CircularizeAtPeriapsis:
                    burns = [QuickBurns.Circularize(body, orbit, now, LeadS, _action.Action == QuickAction.CircularizeAtApoapsis)];
                    break;
                case QuickAction.SetApoapsis or QuickAction.SetPeriapsis:
                    if (!TryKm(out var altitude)) return;
                    burns = [QuickBurns.SetApsis(body, orbit, now, LeadS, altitude, _action.Action == QuickAction.SetApoapsis)];
                    break;
                case QuickAction.GoTo when GoingToCraft:
                    if (PlanRendezvous(body, orbit, after) is not { } meet) return;
                    burns = meet;
                    break;
                case QuickAction.GoTo:
                    if (Destination() is not { } destination)
                    {
                        Status = _destination is { IsTarget: true } ? "Set a target in KSP first: a moon, a planet or a craft." : "Pick where to go.";
                        return;
                    }
                    if (destination.Id == body.Id)
                    {
                        Status = $"You're already around {destination.Name}. To change your orbit here, circularize or change the apoapsis or periapsis.";
                        return;
                    }
                    if (!TryKm(out var height)) return;
                    _ = PlanTripAsync(body, destination, orbit, now, after, height);
                    return;
                case QuickAction.Deorbit:
                    if (!TryKm(out var periapsis)) return;
                    var deorbit = Rendezvous.Deorbit(body, orbit, after, periapsis);
                    burns = [new FlightBurn($"Deorbit (periapsis {Km(periapsis)})", deorbit.Ut, deorbit.Burn)];
                    break;
                case QuickAction.MatchPlanes:
                    if (TargetOrbit(body) is not (var name, var targetOrbit, _)) return;
                    var (plane, _) = Rendezvous.MatchPlane(body, orbit, targetOrbit, after);
                    burns = [new FlightBurn($"Match planes: {name}", plane.Ut, plane.Burn)];
                    break;
                default:
                    if (PlanRendezvous(body, orbit, after) is not { } rendezvous) return;
                    burns = rendezvous;
                    break;
            }

            (_plannedBody, _plannedFrom, _plannedUt) = (body, orbit, now);
            for (var i = 0; i < burns.Count; i++) Burns.Add(new FlightBurnRow(i + 1, burns[i]));
            var end = burns.Aggregate(orbit, (o, b) => Rendezvous.OrbitAfter(b.Ut, b.Burn, o, body.Mu));
            ResultText = $"{Format.Speed0(burns.Sum(b => b.DeltaV))} in all · leaves you in {FlightViewModel.OrbitText(end, body)} around {body.Name}";
            OnPropertyChanged(nameof(HasBurns));
            Status = FuelNote();
            // An apsis burn is never pushed to the next orbit, so it can be close.
            if (burns[0].Ut - now < LeadS)
                Status = (burns[0].Note is { } note ? note + " " : "") +
                         $"The burn is only {Duration(burns[0].Ut - now)} away: send it and point the craft now, starting about half the burn time early.";
            RefreshLive();
        }
        catch (ArgumentException ex)
        {
            Status = ex.Message;
        }
    }

    /// <summary>
    /// Plans the ways to <paramref name="to"/> off the UI thread (a launch window search takes a moment), lists them and
    /// shows the first (the cheapest).
    /// </summary>
    private async Task PlanTripAsync(CelestialBody from, CelestialBody to, OrbitalElements orbit, double now, double after, double height)
    {
        _busy = true;
        Status = $"Working out the way to {to.Name}…";
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var bodies = _sync.StockBodies().Values.ToList();
            var options = await Task.Run(() => TripPlanner.Options(from, to, orbit, after, height, bodies));
            (_plannedBody, _plannedFrom, _plannedUt, _plannedTo) = (from, orbit, now, to);
            foreach (var option in options) TripOptions.Add(new TripOptionRow(option, OptionText(option, now)));
            OnPropertyChanged(nameof(HasTripOptions));
            SelectedTrip = TripOptions[0];
        }
        catch (ArgumentException ex)
        {
            Status = ex.Message;
        }
        finally
        {
            _busy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>"leaves in 46d 2h · 67d 3h there · 1,691 m/s".</summary>
    private string OptionText(TripOption option, double now)
    {
        var leaves = double.IsNaN(option.DepartureUt) ? "on course" : option.DepartureUt - now < 3600 ? "leaves now" : "leaves in " + Duration(option.DepartureUt - now);
        var there = Duration(option.ArrivalUt - (double.IsNaN(option.DepartureUt) ? now : option.DepartureUt));
        return $"{leaves} · {there} there · {Format.Speed0(option.DeltaV)}";
    }

    /// <summary>Shows one way of making the planned trip: its burns, where it arrives, and anything to know.</summary>
    private void ShowTrip(TripOption option)
    {
        Burns.Clear();
        var burns = option.Burns;
        for (var i = 0; i < burns.Count; i++) Burns.Add(new FlightBurnRow(i + 1, burns[i]));
        var last = option.Legs[^1];
        var to = last.Hop.To;
        var stops = option.Legs.Take(option.Legs.Count - 1).Select(l => $"{l.Hop.To.Name} ({Km(l.AltitudeM)})").ToList();
        ResultText = $"{Format.Speed0(option.DeltaV)} in all · " + (option.IsCorrection ? "corrected: " : "") +
                     $"arrives {_calendar.FormatDate(option.ArrivalUt)}, {Km(option.Arrival.PeriapsisAltitudeM)} over {to.Name} " +
                     $"({option.Arrival.Path.InclinationDeg:0}° to its equator)" + (last.Plan.Capture is null ? " without a capture burn" : " and captures there") +
                     (stops.Count > 0 ? $" · stops in orbit around {string.Join(", then ", stops)} on the way" : "");
        var notes = option.Warnings.ToList();
        if (burns.Count == 0) notes.Insert(0, "On course, with nothing to burn.");
        if (FuelNote() is { } fuel) notes.Add(fuel);
        Status = notes.Count > 0 ? string.Join(" ", notes) : null;
        OnPropertyChanged(nameof(HasBurns));
        RefreshLive();
    }

    /// <summary>The craft being flown as the save last recorded it (its parts, fuel and engines), if it's in the save.</summary>
    private VesselReport? ActiveReport()
    {
        if (_sync.ActiveCraft?.SaveVesselId is not { } id || _sync.Save?.Vessels.FirstOrDefault(v => v.Id == id) is not { } vessel) return null;
        try { return VesselAnalysis.Analyze(vessel, _sync.Parts.Catalog); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Sets each burn's length for the craft being flown (docs/MATH.md §7.1), and says if its fuel doesn't stretch to them
    /// all; null when it does, or the engines aren't known.
    /// </summary>
    private string? FuelNote()
    {
        if (ActiveReport() is not { ThrustKn: > 0 } report) return null;
        var times = VesselAnalysis.BurnTimes(report, Burns.Select(b => b.Burn.DeltaV));
        for (var i = 0; i < Burns.Count; i++)
            Burns[i].BurnTime = times[i] is { } t ? "burn " + Units.BurnTime(t) : "not enough fuel";
        var need = Burns.Sum(b => b.Burn.DeltaV);
        var name = _sync.ActiveCraft!.Name;
        return need > report.DeltaVVacuum
            ? $"{name} has {Format.Speed0(report.DeltaVVacuum)} left (as the save last recorded it): {Format.Speed0(need - report.DeltaVVacuum)} short."
            : null;
    }

    /// <summary>A rendezvous with the craft targeted in KSP (as the Rendezvous tool plans it); null, with a status, if there isn't one here.</summary>
    private List<FlightBurn>? PlanRendezvous(CelestialBody body, OrbitalElements orbit, double after)
    {
        if (TargetOrbit(body, craftOnly: true) is not (var targetName, var targetElements, var craft)) return null;
        var plan = Rendezvous.Plan(new RendezvousRequest(body, [new RendezvousTarget(targetName, targetElements)], after, 0,
            new RendezvousTarget(_sync.Link.Game!.Active!.Name, orbit)));
        (_plannedRendezvous, _plannedTarget) = (plan, craft);
        return FlightViewModel.RendezvousBurns(plan);
    }

    /// <summary>The body Go to is set to: the one picked, or the body targeted in KSP. Null for a targeted craft, or no target.</summary>
    private CelestialBody? Destination() =>
        _destination is { IsTarget: true }
            ? _sync.Link.Game?.Target is null && _sync.Link.Game?.TargetBody is { } name ? _sync.StockBodies().GetValueOrDefault(name) : null
            : _destination?.Body;

    /// <summary>The KSP target's name and orbit around <paramref name="body"/> (a craft, or, for a plane match, a moon of it).</summary>
    private (string Name, OrbitalElements Orbit, FlightCraft? Craft)? TargetOrbit(CelestialBody body, bool craftOnly = false)
    {
        var game = _sync.Link.Game;
        if (game?.Target is { } vessel)
        {
            if (vessel.Orbit is not { IsClosed: true } o || o.BodyName != body.Name)
            {
                Status = $"{vessel.Name} isn't in orbit around {body.Name}.";
                return null;
            }
            return (vessel.Name, o.ToElements(), _sync.TargetCraft);
        }
        if (!craftOnly && game?.TargetBody is { } name && _sync.StockBodies().GetValueOrDefault(name) is { Orbit: { } moonOrbit } moon)
        {
            if (moon.ParentId == body.Id) return (moon.Name, moonOrbit, null);
            Status = $"{moon.Name} doesn't orbit {body.Name}.";
            return null;
        }
        Status = craftOnly ? "Target a craft in KSP first." : "Target a craft, or a moon of " + body.Name + ", in KSP first.";
        return null;
    }

    private bool TryKm(out double metres)
    {
        metres = 0;
        if (!double.TryParse(_altitudeText.Trim().TrimEnd('k', 'K', 'm', 'M').Trim(), NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.CurrentCulture, out var km))
        {
            Status = $"Enter the {AltitudeLabel.ToLowerInvariant()} in km.";
            return false;
        }
        metres = km * 1000;
        return true;
    }

    /// <summary>The altitude box's starting value: the apsis as it is now, or a periapsis low in the atmosphere.</summary>
    private string DefaultAltitude()
    {
        // A destination's parking height doesn't depend on where the craft is: shown outside flight too.
        if (_action.Action == QuickAction.GoTo)
            return Destination()?.DefaultParkingAltitudeM is { } parking ? Math.Round(parking / 1000).ToString("#,0", CultureInfo.CurrentCulture) : "";
        if (Here() is not (var body, var orbit)) return "";
        var periapsis = Kepler.PeriapsisRadius(orbit) - body.EquatorialRadiusM;
        var metres = _action.Action switch
        {
            // Capturing from a flyby: an apoapsis at the parking height, or above the pass.
            QuickAction.SetApoapsis when orbit.Eccentricity >= 1 => Math.Max(periapsis, body.DefaultParkingAltitudeM),
            QuickAction.SetApoapsis => orbit.SemiMajorAxisM * (1 + orbit.Eccentricity) - body.EquatorialRadiusM,
            QuickAction.SetPeriapsis => orbit.SemiMajorAxisM * (1 - orbit.Eccentricity) - body.EquatorialRadiusM,
            QuickAction.Deorbit => body.AtmosphereHeightM * 0.43, // as the Rendezvous tool's deorbit (docs/MATH.md §2.5)
            _ => double.NaN,
        };
        return double.IsNaN(metres) ? "" : (Math.Round(metres / 1000)).ToString("#,0", CultureInfo.CurrentCulture);
    }

    private void Clear()
    {
        Burns.Clear();
        Status = null;
        ResultText = "";
        _plannedRendezvous = null;
        _plannedTarget = null;
        _plannedTo = null;
        TripOptions.Clear();
        _trip = null;
        OnPropertyChanged(nameof(SelectedTrip));
        OnPropertyChanged(nameof(HasTripOptions));
        OnPropertyChanged(nameof(HasBurns));
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshLive()
    {
        OnPropertyChanged(nameof(CraftText));
        RefreshDestinations();
        if (_altitudeText.Length == 0 && NeedsAltitude && DefaultAltitude() is { Length: > 0 } fill)
        {
            _altitudeText = fill;
            OnPropertyChanged(nameof(AltitudeText));
        }
        var now = _sync.Now;
        foreach (var row in Burns)
        {
            var ut = row.Burn.Ut;
            row.Countdown = now is not { } t ? "" : ut >= t ? "in " + Duration(ut - t) : Duration(t - ut) + " ago";
            row.State = now is { } t2 && ut < t2 ? BurnState.Passed : BurnState.Upcoming;
        }
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Keeps the Go to list up to date. KSP's target entry is renamed to what's targeted whenever that changes. When the
    /// craft has moved to another body, the rest is rebuilt: its moons, and the planet when it's around a moon (not the star:
    /// that's an interplanetary trip). Then KSP's target is picked if one is set, else, around a moon, the planet. Leaves the
    /// burns shown alone (arriving shouldn't wipe the plan that got you there).
    /// </summary>
    private void RefreshDestinations()
    {
        var game = _sync.Link.Game;
        var targetKey = game?.Target?.Name is { } craftName ? "craft:" + craftName : game?.TargetBody;
        var targetChanged = targetKey != _targetKey;
        if (targetChanged)
        {
            _targetKey = targetKey;
            _target.Name = game?.Target is { } t ? $"KSP's target: {t.Name} (rendezvous)"
                : game?.TargetBody is { } b ? $"KSP's target: {b}"
                : "KSP's target (none set)";
            OnPropertyChanged(nameof(NeedsAltitude));
        }

        var here = Here()?.Body;
        var moved = (here?.Name ?? "") != _destinationsFrom;
        if (moved)
        {
            _destinationsFrom = here?.Name ?? "";
            var list = new List<DestinationOption> { _target };
            list.AddRange(Reachable(here).Select(b => new DestinationOption(b)));
            Destinations = list;
            // KSP's target if one is set; else around a moon its planet, else the nearest moon (of the home planet, outside flight).
            var from = here ?? _sync.StockBodies().Values.FirstOrDefault(b => b.IsDefault);
            _destination = targetKey is not null ? _target
                : list.FirstOrDefault(o => o.Body is { } b && from is not null && b.Id == from.ParentId && here is not null)
                  ?? list.FirstOrDefault(o => o.Body is { } b && from is not null && b.ParentId == from.Id)
                  ?? list.ElementAtOrDefault(1) ?? _target;
            OnPropertyChanged(nameof(Destinations));
            OnPropertyChanged(nameof(SelectedDestination));
            OnPropertyChanged(nameof(NeedsAltitude));
        }

        // A new place to go: its parking height, unless burns are showing (their height is the one they were planned for).
        if ((moved || targetChanged && _destination is { IsTarget: true }) && _action.Action == QuickAction.GoTo && Burns.Count == 0)
        {
            _altitudeText = DefaultAltitude();
            OnPropertyChanged(nameof(AltitudeText));
        }
    }

    /// <summary>
    /// Every body a trip can go to from <paramref name="here"/>, nearest first: its moons, its planet (around a moon), then
    /// the rest of the system, each planet followed by its moons. Not the star, and not where the craft already is.
    /// Outside flight (<paramref name="here"/> null), every planet and moon in order, to pick one ahead.
    /// </summary>
    private IReadOnlyList<CelestialBody> Reachable(CelestialBody? here)
    {
        var bodies = _sync.StockBodies().Values.Where(b => b.Orbit is not null && b.ParentId is not null && b.Id != here?.Id).ToList();
        var byId = _sync.StockBodies().Values.ToDictionary(b => b.Id);
        var star = byId.Values.FirstOrDefault(b => b.ParentId is null);
        // A body's planet: itself if it orbits the star, else its parent.
        CelestialBody Planet(CelestialBody b) => b.ParentId == star?.Id || b.ParentId is not { } p || !byId.TryGetValue(p, out var parent) ? b : parent;

        var near = here is null ? [] : bodies.Where(b => b.ParentId == here.Id).OrderBy(b => b.Orbit!.SemiMajorAxisM)
            .Concat(bodies.Where(b => b.Id == here.ParentId)).ToList();
        var rest = bodies.Except(near)
            .OrderBy(b => Planet(b).Orbit!.SemiMajorAxisM).ThenBy(b => Planet(b) == b ? 0 : 1).ThenBy(b => b.Orbit!.SemiMajorAxisM);
        return [.. near, .. rest];
    }

    // ---------------- Sending and keeping ----------------

    private async Task SendAsync()
    {
        var burns = Burns.Where(b => b.State != BurnState.Passed).Select(b => (b.Burn.Ut, b.Burn.Burn)).ToList();
        _busy = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var sent = await _sync.Link.SendNodesAsync(burns, replaceExisting: false);
            Status = (sent.Count == 1 ? $"Sent 1 node to {sent.Vessel}." : $"Sent {sent.Count} nodes to {sent.Vessel}.")
                     + (sent.Skipped == 0 ? "" : $" {sent.Skipped} burn time{(sent.Skipped == 1 ? " has" : "s have")} already passed: plan again.");
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

    /// <summary>Keeps the burns as a flight and opens it in the Flight pane (countdowns, drift, send all).</summary>
    private void Fly()
    {
        if (_plannedBody is not { } body || _plannedFrom is not { } from) return;
        var burns = Burns.Select(b => b.Burn).ToList();
        var craft = _sync.ActiveCraft;
        var flight = _plannedRendezvous is { } plan
            ? new Flight
            {
                Name = $"Meet {plan.Legs[0].Target.Name}",
                Kind = FlightKind.Rendezvous,
                Targets = [new FlightTarget(plan.Legs[0].Target.Name, _plannedTarget?.SaveVesselId, plan.Legs[0].Target.Orbit, plan.Legs[0].ArrivalUt)],
            }
            : new Flight { Name = _plannedTo is { } to ? $"To {to.Name}" : burns[0].Title, Kind = FlightKind.Burns };
        flight = flight with
        {
            BodyName = body.Name,
            Craft = craft,
            StartOrbit = from,
            Burns = burns,
            PlannedUt = _plannedUt,
            LeadS = LeadS,
        };
        _events.RequestOpenFlight(_flights.Add(flight));
        Status = $"Kept as the flight \"{flight.Name}\".";
    }

    private string Duration(double seconds) => seconds < 60 ? $"{seconds:0} s" : _calendar.FormatDuration(seconds);

    private static string Km(double metres) => $"{metres / 1000:#,0.#} km";
}
