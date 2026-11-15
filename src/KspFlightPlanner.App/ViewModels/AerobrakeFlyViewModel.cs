using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.App.ViewModels;

/// <summary>One pass flown, as a row: what the model said, what happened, and the drag measured.</summary>
public sealed record FlownPassRow(string Number, string Predicted, string Actual, string Drag, string SpeedLost, string PeakG);

/// <summary>
/// From here's Aerobrake action (docs/MATH.md §9.5): picks the periapsis for the next pass from the craft's live orbit,
/// follows each pass from the game's readings (height, speed, g, where it'll leave the air), and measures the drag against
/// the model so the next pass is planned with the drag as it really is.
/// </summary>
public sealed class AerobrakeFlyViewModel : ObservableObject
{
    private static readonly CultureInfo C = CultureInfo.CurrentCulture;
    private static readonly GameCalendar Calendar = GameCalendar.Kerbin;
    private readonly SaveSync _sync;
    private readonly Func<(CelestialBody Body, OrbitalElements Orbit)?> _here;
    private AimChoice _aim;
    private AttitudeChoice _attitude;
    private string _ownText = "";
    private string _targetApText = "";
    private string _gentleApText = "";
    private string _targetPeText = "";
    private string _maxPassesText = "10";
    private string? _airBody; // the body the targets were filled in for
    private CraftAero? _shape;
    private string? _shapeFor; // the vessel the shape was read for
    private bool _loading;
    private string _dragNote = "";
    private AerobrakeTracker? _tracker;
    private string? _trackedVessel;
    private FlightSample? _last;
    private string _live = "";
    private string _forecast = "";
    private string _measured = "";
    private string _next = "";

    public AerobrakeFlyViewModel(SaveSync sync, FlightRecorder recorder, Func<(CelestialBody, OrbitalElements)?> here)
    {
        _sync = sync;
        _here = here;
        Aims =
        [
            new(AerobrakeAim.FewestPasses, "Fewest passes", "As low as is safe: the most braking each pass."),
            new(AerobrakeAim.Gentlest, "Gentlest", "As high as still gets the apoapsis down in time: the least drag each pass."),
            new(AerobrakeAim.TargetOrbit, "Target orbit", "Brake down towards an orbit, then burn at apoapsis to finish it."),
            new(AerobrakeAim.Own, "My own periapsis", "The periapsis you type, checked for safety."),
        ];
        _aim = Aims[2];
        Attitudes =
        [
            new(AeroAttitude.EnginesFirst, "Engines first (hold retrograde)"),
            new(AeroAttitude.NoseFirst, "Nose first (hold prograde)"),
            new(AeroAttitude.SideOn, "Side-on (most drag)"),
        ];
        _attitude = Attitudes[0];
        recorder.Sampled += OnSample;
        sync.Link.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(KspLink.IsConnected) or nameof(KspLink.InFlight))) return;
            if (!sync.Link.InFlight) _last = null; // the drag measured so far is kept for when the craft is flown again
            UpdateLive();
        };
    }

    public IReadOnlyList<AimChoice> Aims { get; }
    public IReadOnlyList<AttitudeChoice> Attitudes { get; }
    public ObservableCollection<FlownPassRow> Flown { get; } = [];
    public bool HasFlown => Flown.Count > 0;

    /// <summary>Raised when an input changes, so the burns shown (planned from the old ones) are cleared.</summary>
    public event Action? Changed;

    public AimChoice Aim
    {
        get => _aim;
        set
        {
            if (value is null || !SetProperty(ref _aim, value)) return;
            foreach (var n in new[] { nameof(IsOwnAim), nameof(ShowsTargetApoapsis), nameof(ShowsTargetPeriapsis), nameof(TargetApoapsisLabel), nameof(TargetApoapsisText), nameof(ShowsMaxPasses) })
                OnPropertyChanged(n);
            Changed?.Invoke();
        }
    }

    public AttitudeChoice Attitude
    {
        get => _attitude;
        set
        {
            if (value is null || !SetProperty(ref _attitude, value)) return;
            // A new attitude is a new drag: what was measured holding the old one doesn't carry over.
            _tracker = null;
            UpdateDragNote();
            Changed?.Invoke();
        }
    }

    public bool IsOwnAim => _aim.Aim == AerobrakeAim.Own;
    public bool ShowsTargetApoapsis => _aim.Aim is AerobrakeAim.Gentlest or AerobrakeAim.TargetOrbit;
    public bool ShowsTargetPeriapsis => _aim.Aim == AerobrakeAim.TargetOrbit;
    public bool ShowsMaxPasses => ShowsTargetApoapsis;
    public string TargetApoapsisLabel => _aim.Aim == AerobrakeAim.Gentlest ? "Bring Ap down to" : "Target apoapsis";

    public string OwnPeriapsisText { get => _ownText; set { if (SetProperty(ref _ownText, value)) Changed?.Invoke(); } }

    public string TargetApoapsisText
    {
        get => _aim.Aim == AerobrakeAim.Gentlest ? _gentleApText : _targetApText;
        set
        {
            if (value == TargetApoapsisText) return;
            if (_aim.Aim == AerobrakeAim.Gentlest) _gentleApText = value; else _targetApText = value;
            OnPropertyChanged();
            Changed?.Invoke();
        }
    }

    public string TargetPeriapsisText { get => _targetPeText; set { if (SetProperty(ref _targetPeText, value)) Changed?.Invoke(); } }
    public string MaxPassesText { get => _maxPassesText; set { if (SetProperty(ref _maxPassesText, value)) Changed?.Invoke(); } }

    /// <summary>Where the drag comes from.</summary>
    public string DragNote { get => _dragNote; private set => SetProperty(ref _dragNote, value); }

    /// <summary>Where the craft is in the braking now: coasting to the next pass, in the air, or between passes.</summary>
    public string LiveText { get => _live; private set => SetProperty(ref _live, value); }

    /// <summary>What the pass ahead (or under way) will do.</summary>
    public string ForecastText { get => _forecast; private set { if (SetProperty(ref _forecast, value)) OnPropertyChanged(nameof(HasForecast)); } }
    public bool HasForecast => _forecast.Length > 0;

    /// <summary>The drag measured against the model.</summary>
    public string MeasuredText { get => _measured; private set { if (SetProperty(ref _measured, value)) OnPropertyChanged(nameof(HasMeasured)); } }
    public bool HasMeasured => _measured.Length > 0;

    /// <summary>After a pass: what to do next, worked out from the orbit it left on.</summary>
    public string NextText { get => _next; private set { if (SetProperty(ref _next, value)) OnPropertyChanged(nameof(HasNext)); } }
    public bool HasNext => _next.Length > 0;

    /// <summary>The drag to plan with: the parts' for the attitude held, times what's been measured. Null until the parts are read.</summary>
    private DragTable? Drag => _shape?.Table(_attitude.Attitude) is { } table ? _tracker?.DragFactor is { } f ? table.Scaled(f) : table : null;

    /// <summary>The craft's mass now (kg): the game's reading, else the save's.</summary>
    private double? MassKg => _last?.MassKg is > 0 and var m ? m
        : _sync.ActiveStack is { } v ? VesselAnalysis.Analyze(v, _sync.Parts.Catalog).WetMassT * 1000 : null;

    /// <summary>
    /// Called when the action is picked (or Go to is set to aerobrake at <paramref name="airBody"/>): reads the flown
    /// craft's parts and fills in the targets for the body to brake at, the one the craft is around unless given.
    /// </summary>
    public void Activate(CelestialBody? airBody = null)
    {
        _ = LoadShapeAsync();
        FillTargets(airBody ?? _here()?.Body);
        UpdateLive();
    }

    private void FillTargets(CelestialBody? body)
    {
        if (body is null || body.Name == _airBody || !body.HasAtmosphere) return;
        _airBody = body.Name;
        var km = (body.DefaultParkingAltitudeM / 1000).ToString("0", C);
        (_targetApText, _targetPeText) = (km, km);
        _gentleApText = (Math.Round(body.AtmosphereHeightM * 7 / 10_000) * 10).ToString("0", C);
        _ownText = Math.Round(body.AtmosphereHeightM * 0.65 / 1000).ToString(C);
        foreach (var n in new[] { nameof(TargetApoapsisText), nameof(TargetPeriapsisText), nameof(OwnPeriapsisText) }) OnPropertyChanged(n);
    }

    private async Task LoadShapeAsync()
    {
        if (_sync.ActiveStack is not { } vessel) { UpdateDragNote(); return; }
        if (_shapeFor == vessel.Id && _shape is not null || _loading) return;
        _loading = true;
        try
        {
            var aero = await AeroDataCache.GetAsync(_sync.Settings.KspInstallPath);
            _shape = aero is null ? null : await Task.Run(() => CraftAero.FromVessel(vessel, aero));
            _shapeFor = vessel.Id;
            _tracker = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _shape = null;
        }
        finally
        {
            _loading = false;
        }
        UpdateDragNote();
        UpdateLive();
    }

    private void UpdateDragNote()
    {
        if (_shape is not { } s)
        {
            DragNote = _sync.ActiveStack is null ? "Fly a craft to read its parts."
                : _sync.Settings.KspInstallPath is null ? "Set your KSP folder to read the craft's drag."
                : "No drag data for this craft's parts (KSP writes PartDatabase.cfg each time it loads).";
            return;
        }
        DragNote = $"Drag from its {s.PartCount} parts as in the last save: {s.DragArea(10, _attitude.Attitude):N1} m² at Mach 10" +
                   (_tracker?.DragFactor is { } f ? $", times {f:0.00} as measured." : ". Measured on the first pass.");
    }

    /// <summary>
    /// The next step from the orbit now, for the aim (off the UI thread: the searches take a moment). Null with a reason
    /// when something's missing.
    /// </summary>
    public Task<(AerobrakeStep? Step, string? Problem)> NextStepAsync(CelestialBody body, OrbitalElements orbit) =>
        StepFromAsync(body, AerobrakeOrbit.FromPath(body, orbit));

    /// <summary>
    /// The next step for the aim from <paramref name="now"/>, an orbit around <paramref name="body"/>: the one the craft
    /// is on, or (Go to, aerobraking there) the path it'll arrive on.
    /// </summary>
    public async Task<(AerobrakeStep? Step, string? Problem)> StepFromAsync(CelestialBody body, AerobrakeOrbit now)
    {
        if (!body.HasAtmosphere || AirModel.For(body) is null) return (null, $"{body.Name} has no air to brake in.");
        if (_shape is null) await LoadShapeAsync();
        if (Drag is not { } drag) return (null, DragNote);
        if (MassKg is not { } mass || mass <= 0) return (null, "No mass for the craft yet.");
        double Km(string text) => double.TryParse(text.Trim(), NumberStyles.Float, C, out var v) ? v * 1000 : double.NaN;
        var aim = _aim.Aim;
        var (own, tAp, tPe) = (Km(_ownText), Km(TargetApoapsisText), Km(_targetPeText));
        var air = body.AtmosphereHeightM;
        if (aim == AerobrakeAim.Own && !(own > 0 && own < air)) return (null, $"Enter a periapsis in the air (below {air / 1000:N0} km).");
        if (ShowsTargetApoapsis && !(tAp > air)) return (null, $"Enter an apoapsis to get down to, above the air ({air / 1000:N0} km).");
        if (aim == AerobrakeAim.TargetOrbit && !(tPe > air && tPe <= tAp)) return (null, "Enter a target periapsis above the air and no higher than the target apoapsis.");
        var passes = int.TryParse(_maxPassesText.Trim(), NumberStyles.Integer, C, out var p) && p >= 1 ? Math.Min(30, p) : 10;
        var step = await Task.Run(() => AerobrakeLive.Next(body, now, mass, drag, aim, own, tAp, tPe, passes));
        return (step, null);
    }

    // ---------------- Live ----------------

    private void OnSample(string vessel, FlightSample s)
    {
        _last = s;
        var body = _sync.StockBodies().GetValueOrDefault(s.Body);
        if (_tracker is null || vessel != _trackedVessel || _tracker.Body.Name != s.Body)
        {
            _tracker = null;
            Flown.Clear();
            OnPropertyChanged(nameof(HasFlown));
            NextText = "";
            if (body is { HasAtmosphere: true } && _shape?.Table(_attitude.Attitude) is { } table && _shapeFor == _sync.ActiveStack?.Id)
            {
                var inclination = double.IsFinite(s.InclinationDeg) ? s.InclinationDeg : 0;
                _tracker = new AerobrakeTracker(body, inclination, table);
                _trackedVessel = vessel;
            }
            else if (_sync.ActiveStack?.Id is { } id && id != _shapeFor) _ = LoadShapeAsync();
        }
        if (_tracker?.Add(s) is { } ended) _ = PassEndedAsync(ended);
        UpdateLive();
    }

    private async Task PassEndedAsync(FlownPass pass)
    {
        Flown.Add(new FlownPassRow(pass.Number.ToString(C),
            pass.PredictedApoapsisM is { } p ? Km(p) : "comes down",
            pass.CameDown ? "came down" : pass.ApoapsisM is { } a ? Km(a) : "escaping",
            pass.DragFactor is { } f ? $"{f * 100:N0}%" : "–",
            Format.Speed0(pass.SpeedLost), $"{pass.PeakG:N1} g"));
        OnPropertyChanged(nameof(HasFlown));
        UpdateDragNote();
        if (pass.CameDown || _here() is not (var body, var orbit)) { NextText = ""; return; }
        NextText = "Working out the next pass with the drag as measured…";
        var (step, problem) = await NextStepAsync(body, orbit);
        NextText = step is null ? problem ?? "" : NextStepText(step, orbit, body);
    }

    /// <summary>"Next: periapsis 47.3 km (it's at 45.1 km now) …: Plan it for the burn."</summary>
    public static string NextStepText(AerobrakeStep step, OrbitalElements orbit, CelestialBody body)
    {
        var pe = Kepler.PeriapsisRadius(orbit) - body.EquatorialRadiusM;
        if (step.Finish)
            return $"Done braking. {step.Why} Plan it for the burn{(step.ApoapsisM is null ? "" : "s")}.";
        if (Math.Abs(pe - step.PeriapsisM) < AerobrakeLive.CloseEnoughM)
            return $"Next pass: periapsis {Km(step.PeriapsisM)}, where it is now. Nothing to burn. {step.Why}";
        return $"Next pass: periapsis {Km(step.PeriapsisM)} (it's {Km(pe)} now). {step.Why} Plan it for the burn.";
    }

    private void UpdateLive()
    {
        if (!_sync.Link.InFlight) { (LiveText, ForecastText, MeasuredText) = ("Fly a craft to follow its passes.", "", ""); return; }
        if (_last is not { } s || _here() is not (var body, var orbit)) { (LiveText, ForecastText) = ("Waiting for the game…", ""); return; }
        if (!body.HasAtmosphere) { (LiveText, ForecastText, MeasuredText) = ($"{body.Name} has no air to brake in.", "", ""); return; }

        var top = body.AtmosphereHeightM;
        var pe = Kepler.PeriapsisRadius(orbit) - body.EquatorialRadiusM;
        var drag = Drag;
        if (_tracker is { InAir: true } t)
        {
            LiveText = $"In the air: {Km(s.AltitudeM)} · {Format.Speed0(s.OrbitalSpeed)} · {(s.VerticalSpeed < 0 ? "falling" : "climbing")} " +
                       $"{Math.Abs(s.VerticalSpeed):N0} m/s · {s.GForce:N1} g (peak {t.PeakG:N1} g) · {Format.Speed0(t.SpeedLost)} off so far";
            if (drag is not null && MassKg is not null)
            {
                var f = t.Forecast(s, drag);
                var going = f.Outcome switch
                {
                    PassOutcome.ComesDown => "Comes down: it won't climb back out. Burn prograde now to get out.",
                    PassOutcome.Escaping => "Leaves still escaping.",
                    _ => $"Leaves the air in {Calendar.FormatDuration(f.TimeInAirS)} on apoapsis {Km(f.ApoapsisAfterM!.Value)}, periapsis {Km(f.PeriapsisAfterM!.Value)}.",
                };
                ForecastText = going + (t.PredictedApoapsisM is { } p ? $" (Worked out going in: apoapsis {Km(p)}.)" : "");
            }
        }
        else if (pe < top)
        {
            var nextPe = Kepler.NextPeriapsisUt(orbit, body.Mu, s.Ut);
            LiveText = $"Coasting · periapsis {Km(pe)} in the air" + (nextPe is { } at ? $", in {Calendar.FormatDuration(at - s.Ut)}" : "") +
                       $" · apoapsis {(orbit.Eccentricity < 1 ? Km(s.ApoapsisAltitudeM) : "none (arriving from far away)")}";
            if (drag is not null && MassKg is { } mass)
            {
                var start = new AerobrakeOrbit(pe, -body.Mu / (2 * orbit.SemiMajorAxisM), orbit.InclinationDeg);
                var first = Aerobraking.Plan(body, start, mass, drag, 1).Passes.FirstOrDefault();
                ForecastText = first is null ? "" : "Next pass: " + first.Outcome switch
                {
                    PassOutcome.ComesDown => "comes down. Raise the periapsis before it.",
                    PassOutcome.Escaping => $"takes off {Format.Speed0(first.SpeedLost)} but it still escapes. Go lower to be captured.",
                    _ => $"takes off {Format.Speed0(first.SpeedLost)} at up to {first.PeakG:N1} g, leaving apoapsis {Km(first.ApoapsisAfterM!.Value)}.",
                };
            }
        }
        else
        {
            LiveText = $"Periapsis {Km(pe)} is above the air ({Km(top)}): plan it to dip in.";
            ForecastText = "";
        }

        MeasuredText = _tracker switch
        {
            { InAir: true, PassFactor: { } f } => $"Drag this pass: {f * 100:N0}% of the model.",
            { InAir: true } => "Measuring the drag…",
            { DragFactor: { } f } => $"Drag measured at {f * 100:N0}% of the model: planning with that.",
            _ => "",
        };
    }

    private static string Km(double metres) => (metres / 1000).ToString(Math.Abs(metres) < 100_000 ? "N1" : "N0", C) + " km";
}
