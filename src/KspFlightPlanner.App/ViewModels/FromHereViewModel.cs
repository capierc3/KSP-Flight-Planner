using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App.ViewModels;

public enum QuickAction { CircularizeAtApoapsis, CircularizeAtPeriapsis, SetApoapsis, SetPeriapsis, MatchPlanes, Rendezvous, Deorbit }

public sealed record QuickActionOption(QuickAction Action, string Name)
{
    public override string ToString() => Name;
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

    // What the shown burns were planned from, for keeping them as a flight.
    private CelestialBody? _plannedBody;
    private OrbitalElements? _plannedFrom;
    private double _plannedUt;
    private RendezvousPlan? _plannedRendezvous;
    private FlightCraft? _plannedTarget;

    public FromHereViewModel(SaveSync sync, FlightRepository flights, PlannerEvents events)
    {
        _sync = sync;
        _flights = flights;
        _events = events;
        _action = Actions[0];
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
            foreach (var name in new[] { nameof(AltitudeText), nameof(NeedsAltitude), nameof(AltitudeLabel), nameof(ActionHint) }) OnPropertyChanged(name);
            Clear();
        }
    }

    public bool NeedsAltitude => _action.Action is QuickAction.SetApoapsis or QuickAction.SetPeriapsis or QuickAction.Deorbit;

    public string AltitudeLabel => _action.Action switch
    {
        QuickAction.SetApoapsis => "New apoapsis",
        QuickAction.SetPeriapsis => "New periapsis",
        _ => "Periapsis",
    };

    public string AltitudeText
    {
        get => _altitudeText;
        set { if (SetProperty(ref _altitudeText, value)) Clear(); }
    }

    public string ActionHint => _action.Action switch
    {
        QuickAction.CircularizeAtApoapsis => "One burn at the next apoapsis, however soon. Just past it, falling back into the atmosphere, it's half a minute from now. On an orbit that's already round, two minutes.",
        QuickAction.CircularizeAtPeriapsis => "One burn at the next periapsis, however soon. On an orbit that's already round, it's two minutes from now.",
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
        if (!_sync.Link.InFlight || _sync.Link.Game?.Active?.Orbit is not { IsClosed: true } orbit) return null;
        return orbit.BodyName is { } name && _sync.StockBodies().GetValueOrDefault(name) is { } body ? (body, orbit.ToElements()) : null;
    }

    private void Plan()
    {
        Clear();
        if (Here() is not (var body, var orbit) || _sync.Now is not { } now) return;
        var after = now + LeadS;
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
                    if (TargetOrbit(body, craftOnly: true) is not (var targetName, var targetElements, var craft)) return;
                    var plan = Rendezvous.Plan(new RendezvousRequest(body, [new RendezvousTarget(targetName, targetElements)], after, 0,
                        new RendezvousTarget(_sync.Link.Game!.Active!.Name, orbit)));
                    burns = FlightViewModel.RendezvousBurns(plan);
                    (_plannedRendezvous, _plannedTarget) = (plan, craft);
                    break;
            }

            (_plannedBody, _plannedFrom, _plannedUt) = (body, orbit, now);
            for (var i = 0; i < burns.Count; i++) Burns.Add(new FlightBurnRow(i + 1, burns[i]));
            var end = burns.Aggregate(orbit, (o, b) => Rendezvous.OrbitAfter(b.Ut, b.Burn, o, body.Mu));
            ResultText = $"{Format.Speed0(burns.Sum(b => b.DeltaV))} in all · leaves you in {FlightViewModel.OrbitText(end, body)} around {body.Name}";
            OnPropertyChanged(nameof(HasBurns));
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
        if (Here() is not (var body, var orbit)) return "";
        var metres = _action.Action switch
        {
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
        OnPropertyChanged(nameof(HasBurns));
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshLive()
    {
        OnPropertyChanged(nameof(CraftText));
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
