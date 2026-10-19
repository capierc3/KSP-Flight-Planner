using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App.ViewModels;

public enum QuickAction { CircularizeAtApoapsis, CircularizeAtPeriapsis, SetApoapsis, SetPeriapsis, GoTo, MatchPlanes, Rendezvous, Deploy, Deorbit, Land }

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

/// <summary>
/// One way of making a trip (<see cref="Option"/>) or a landing (<see cref="Landing"/>), as a row to pick: what it's best
/// at, and when, how long and its Δv.
/// </summary>
public sealed record TripOptionRow(string Label, string Text, TripOption? Option = null, LandingOption? Landing = null);

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
    private readonly BodyRotation _rotation;
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
    private double _plannedHeight; // a trip's arrival height over _plannedTo
    private LandingLeg? _plannedLanding;
    private bool _plannedSooner;
    private DeployStep? _plannedDeploy; // a deployment's satellites and carrier, at _plannedHeight

    // Deploy satellites: how many, and whether to share the carrier's apoapsis (a dive orbit) instead of its periapsis.
    private string _satellitesText = "3";
    private bool _preferDive;

    // Land at a spot: the spots on the body the craft is around, and the one picked or typed.
    private LandingTarget? _spot;
    private string _latitudeText = "";
    private string _longitudeText = "";
    private string _twrText = Landing.DefaultTwr.ToString(CultureInfo.CurrentCulture);
    private (string? Body, KspSave? Save, string? Target) _spotsFor = ("\0", null, null);

    public FromHereViewModel(SaveSync sync, FlightRepository flights, PlannerEvents events, BodyRotation rotation)
    {
        _sync = sync;
        _flights = flights;
        _events = events;
        _rotation = rotation;
        _action = Actions[0];
        RefreshDestinations();
        sync.Link.GameUpdated += _ => RefreshLive();
        sync.Link.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(KspLink.IsConnected) or nameof(KspLink.InFlight)) RefreshLive();
        };

        PlanCommand = new RelayCommand(Plan, () => !_busy && Here() is not null);
        SendCommand = new RelayCommand(() => _ = SendAsync(), () => !_busy && _sync.Link.InFlight && Burns.Any(b => b.State != BurnState.Passed && !b.Burn.ByReleasedCraft));
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
        new(QuickAction.Deploy, "Deploy satellites"),
        new(QuickAction.Deorbit, "Deorbit"),
        new(QuickAction.Land, "Land at a spot"),
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
            foreach (var name in new[] { nameof(AltitudeText), nameof(NeedsAltitude), nameof(AltitudeLabel), nameof(ActionHint), nameof(NeedsDestination), nameof(NeedsSpot), nameof(NeedsSatellites), nameof(OptionsTitle) })
                OnPropertyChanged(name);
            Clear();
        }
    }

    public bool NeedsAltitude => _action.Action is QuickAction.SetApoapsis or QuickAction.SetPeriapsis or QuickAction.Deorbit or QuickAction.Deploy
                                 || _action.Action == QuickAction.GoTo && !GoingToCraft;

    /// <summary>Go to is set to KSP's target, and that's a craft: a rendezvous, which needs no height.</summary>
    private bool GoingToCraft => _destination is { IsTarget: true } && _sync.Link.Game?.Target is not null;

    public string AltitudeLabel => _action.Action switch
    {
        QuickAction.SetApoapsis => "New apoapsis",
        QuickAction.SetPeriapsis => "New periapsis",
        QuickAction.GoTo => "Orbit there at",
        QuickAction.Deploy => "Satellites at",
        _ => "Periapsis",
    };

    public bool NeedsDestination => _action.Action == QuickAction.GoTo;

    public bool NeedsSpot => _action.Action == QuickAction.Land;

    public bool NeedsSatellites => _action.Action == QuickAction.Deploy;

    /// <summary>How many satellites the craft carries, to spread evenly round the orbit.</summary>
    public string SatellitesText
    {
        get => _satellitesText;
        set { if (SetProperty(ref _satellitesText, value)) Clear(); }
    }

    /// <summary>Release from a carrier orbit that dips below the satellites' (a dive orbit), as in the Resonant Orbits tool.</summary>
    public bool PreferDive
    {
        get => _preferDive;
        set { if (SetProperty(ref _preferDive, value)) Clear(); }
    }

    public string OptionsTitle => _action.Action == QuickAction.Land ? "Ways to land" : "Ways to go";

    /// <summary>Spots on the body the craft is around: wiki landmarks, then the save's flags, landed craft and KerbNet waypoints.</summary>
    public IReadOnlyList<LandingTarget> Spots { get; private set; } = [];

    public LandingTarget? SelectedSpot
    {
        get => _spot;
        set
        {
            if (!SetProperty(ref _spot, value) || value is null) return;
            _latitudeText = value.LatitudeDeg.ToString("0.####", CultureInfo.CurrentCulture);
            _longitudeText = value.LongitudeDeg.ToString("0.####", CultureInfo.CurrentCulture);
            OnPropertyChanged(nameof(LatitudeText));
            OnPropertyChanged(nameof(LongitudeText));
            Clear();
        }
    }

    /// <summary>The spot's latitude (north +); typing a position lands there instead of at the spot picked.</summary>
    public string LatitudeText
    {
        get => _latitudeText;
        set { if (SetProperty(ref _latitudeText, value)) Clear(); }
    }

    public string LongitudeText
    {
        get => _longitudeText;
        set { if (SetProperty(ref _longitudeText, value)) Clear(); }
    }

    /// <summary>The lander's thrust-to-weight ratio at the surface, for the braking burn.</summary>
    public string TwrText
    {
        get => _twrText;
        set { if (SetProperty(ref _twrText, value)) Clear(); }
    }

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
            if (value.Option is { } trip) ShowTrip(trip);
            else if (value.Landing is { } landing) ShowLanding(landing);
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
        QuickAction.Deploy => "Spreads the satellites evenly round a circular orbit at this height, like the Resonant Orbits tool. If your orbit doesn't reach " +
                              "that height, it climbs (or drops) there first, then enters the carrier orbit and releases one satellite each lap. " +
                              "Each satellite circularizes itself: release it, switch to it and send it its node. Keep it as a flight to follow along.",
        QuickAction.Land => "Pick a spot (or type its latitude and longitude). Tilts your orbit first if it doesn't reach that far north or south, then " +
                            "waits for a pass close to the spot: a deorbit burn half an orbit before, a small burn on the way down to swing the track " +
                            "over it, and a braking burn from 1 km above the ground. On a body with air the landing spot is a rough estimate.",
        _ => "Burns retrograde at the next apoapsis to drop the periapsis into the atmosphere (or onto the ground).",
    };

    /// <summary>What's being flown, where, and what's targeted.</summary>
    public string CraftText
    {
        get
        {
            var link = _sync.Link;
            if (!link.IsConnected) return "Not connected to KSP. Use KSP › Connect to KSP, then fly a craft.";
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
                case QuickAction.Land:
                    if (LandingFromInputs() is not { } leg) return;
                    _ = PlanLandingAsync(body, orbit, now, leg);
                    return;
                case QuickAction.Deorbit:
                    if (!TryKm(out var periapsis)) return;
                    var deorbit = Rendezvous.Deorbit(body, orbit, after, periapsis);
                    burns = [new FlightBurn($"Deorbit (periapsis {Km(periapsis)})", deorbit.Ut, deorbit.Burn)];
                    break;
                case QuickAction.Deploy:
                    if (!TryKm(out var deployAt)) return;
                    if (!int.TryParse(_satellitesText.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var count) || count is < 1 or > 64)
                    {
                        Status = "Enter how many satellites to deploy (1 to 64).";
                        return;
                    }
                    ResonantOrbitResult carrier;
                    try
                    {
                        carrier = ResonantOrbitCalculator.Calculate(new ResonantOrbitRequest(body, count, deployAt, _preferDive));
                    }
                    catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException)
                    {
                        Status = ex.Message.Split(" (Parameter")[0];
                        return;
                    }
                    burns = [.. ResonantDeployment.Plan(body, carrier, orbit, count, after).Burns];
                    (_plannedDeploy, _plannedHeight) = (new DeployStep(count, _preferDive), deployAt);
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
            // A released satellite's burn isn't the craft's: it neither moves the craft nor uses its fuel.
            var own = burns.Where(b => !b.ByReleasedCraft).ToList();
            var end = own.Aggregate(orbit, (o, b) => Rendezvous.OrbitAfter(b.Ut, b.Burn, o, body.Mu));
            var released = burns.Where(b => b.ByReleasedCraft).ToList();
            ResultText = $"{Format.Speed0(own.Sum(b => b.DeltaV))} in all" +
                         (released.Count > 0 ? $", plus {Format.Speed0(released[0].DeltaV)} for each satellite" : "") +
                         $" · leaves you in {FlightViewModel.OrbitText(end, body)} around {body.Name}";
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
            (_plannedBody, _plannedFrom, _plannedUt, _plannedTo, _plannedHeight) = (from, orbit, now, to, height);
            foreach (var option in options) TripOptions.Add(new TripOptionRow(option.Label, OptionText(option, now), option));
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

    /// <summary>The spot and lander from the inputs; null, with a status, when they don't read.</summary>
    private LandingLeg? LandingFromInputs()
    {
        var culture = CultureInfo.CurrentCulture;
        if (!double.TryParse(_latitudeText.Trim().TrimEnd('°'), NumberStyles.Float, culture, out var lat) || Math.Abs(lat) > 90
            || !double.TryParse(_longitudeText.Trim().TrimEnd('°'), NumberStyles.Float, culture, out var lon) || Math.Abs(lon) > 180)
        {
            Status = "Pick a spot, or enter its latitude (−90 to 90, north +) and longitude (−180 to 180, east +).";
            return null;
        }
        if (!double.TryParse(_twrText, NumberStyles.Float, culture, out var twr) || twr <= 1)
        {
            Status = "Enter the lander's thrust-to-weight ratio at the surface (more than 1).";
            return null;
        }
        var target = _spot is { } s && Math.Abs(s.LatitudeDeg - lat) < 1e-4 && Math.Abs(s.LongitudeDeg - lon) < 1e-4
            ? s
            : new LandingTarget("Entered spot", "Entered", lat, lon);
        return new LandingLeg(target, twr);
    }

    /// <summary>Plans the ways to land at the spot off the UI thread (it looks at every pass for a turn of the body) and shows the first.</summary>
    private async Task PlanLandingAsync(CelestialBody body, OrbitalElements orbit, double now, LandingLeg leg)
    {
        _busy = true;
        Status = $"Working out the way down to {leg.Target.Name}…";
        CommandManager.InvalidateRequerySuggested();
        try
        {
            if (await _rotation.InitialRotationDegAsync(body) is not { } theta0)
            {
                Status = BodyRotation.Missing(body);
                return;
            }
            var bake = _rotation.Bake(body.Name);
            var options = await Task.Run(() => LandingFlights.Options(body, leg, orbit, now, LeadS, theta0, bake));
            (_plannedBody, _plannedFrom, _plannedUt, _plannedLanding) = (body, orbit, now, leg);
            foreach (var option in options)
                TripOptions.Add(new TripOptionRow(option.Label, $"lands in {Duration(option.LandingUt - now)} · {Format.Speed0(option.DeltaV)}", Landing: option));
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

    /// <summary>Shows one way of landing: its burns, when and how it comes down, and anything to know.</summary>
    private void ShowLanding(LandingOption option)
    {
        Burns.Clear();
        for (var i = 0; i < option.Burns.Count; i++) Burns.Add(new FlightBurnRow(i + 1, option.Burns[i]));
        _plannedSooner = option.Label != TripOptions[0].Label;
        var site = option.Site;
        ResultText = $"{Format.Speed0(option.DeltaV)} in all · lands {_calendar.FormatDate(option.LandingUt)} at {site.Target.Name}, " +
                     $"coming in {(option.Northbound ? "northbound" : "southbound")}" +
                     (site.GroundHeightM is { } g ? $" · ground {g:#,0} m{(site.GroundFromBake ? " (from the bake)" : "")}" : "");
        var notes = option.Warnings.ToList();
        if (FuelNote() is { } fuel) notes.Add(fuel);
        Status = notes.Count > 0 ? string.Join(" ", notes) : null;
        OnPropertyChanged(nameof(HasBurns));
        RefreshLive();
    }

    /// <summary>
    /// Keeps the spot list in step with the body the craft is around, the save (flags, landed craft, waypoints) and
    /// KSP's target. A landed craft or flag targeted in KSP is picked; else the spot already picked, the save's first
    /// flag, or the first landmark. A plan on show is left alone.
    /// </summary>
    private void RefreshSpots()
    {
        var body = Here()?.Body;
        var target = _sync.Link.Game?.Target?.Name;
        var key = (body?.Name, _sync.Save, target);
        if (key == _spotsFor) return;
        var bodyChanged = key.Item1 != _spotsFor.Body;
        _spotsFor = key;
        Spots = body is null ? [] : Landing.Targets(body, _sync.Save);
        OnPropertyChanged(nameof(Spots));
        var keep = !bodyChanged && _spot is { } current ? Spots.FirstOrDefault(s => s.Name == current.Name && s.Source == current.Source) : null;
        var pick = Spots.FirstOrDefault(s => s.Name == target && s.Source is "Flag" or "Landed craft")
                   ?? keep ?? Spots.FirstOrDefault(s => s.Source == "Flag") ?? Spots.FirstOrDefault();
        if (pick is null || pick == _spot) return;
        if (Burns.Count > 0)
        {
            _spot = pick;
            OnPropertyChanged(nameof(SelectedSpot));
            return;
        }
        SelectedSpot = pick;
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
        var own = Burns.Where(b => !b.Burn.ByReleasedCraft).ToList(); // a satellite burns its own fuel
        var times = VesselAnalysis.BurnTimes(report, own.Select(b => b.Burn.DeltaV));
        for (var i = 0; i < own.Count; i++)
            own[i].BurnTime = times[i] is { } t ? "burn " + Units.BurnTime(t) : "not enough fuel";
        var need = own.Sum(b => b.Burn.DeltaV);
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
            // A relay's usual home: the synchronous orbit, if it's inside the sphere of influence; else where the craft reaches.
            QuickAction.Deploy => body.SynchronousAltitudeM is { } sync && sync < body.SoiAltitudeM
                ? sync
                : orbit.SemiMajorAxisM * (1 + orbit.Eccentricity) - body.EquatorialRadiusM,
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
        _plannedLanding = null;
        _plannedDeploy = null;
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
        RefreshSpots();
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
        // A satellite's own burn goes to it once it's released (from the Flight pane), not to the carrier.
        var burns = Burns.Where(b => b.State != BurnState.Passed && !b.Burn.ByReleasedCraft).Select(b => (b.Burn.Ut, b.Burn.Burn)).ToList();
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
        var flight = _plannedLanding is { } landing
            ? new Flight { Name = $"Land at {landing.Target.Name}", Kind = FlightKind.Landing, Landing = landing, LandSooner = _plannedSooner }
            : _plannedRendezvous is { } plan
            ? new Flight
            {
                Name = $"Meet {plan.Legs[0].Target.Name}",
                Kind = FlightKind.Rendezvous,
                Targets = [new FlightTarget(plan.Legs[0].Target.Name, _plannedTarget?.SaveVesselId, plan.Legs[0].Target.Orbit, plan.Legs[0].ArrivalUt)],
            }
            : _plannedDeploy is { } deploy
            ? new Flight
            {
                Name = $"Deploy {deploy.SatelliteCount} satellites around {body.Name}",
                Kind = FlightKind.Resonant,
                SatelliteCount = deploy.SatelliteCount,
                AltitudeM = _plannedHeight,
                PreferDiveOrbit = deploy.PreferDiveOrbit,
            }
            : _plannedTo is { } to
            ? new Flight { Name = $"To {to.Name}", Kind = FlightKind.Burns, DestinationName = to.Name, AltitudeM = _plannedHeight }
            : new Flight { Name = burns[0].Title, Kind = FlightKind.Burns };
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
