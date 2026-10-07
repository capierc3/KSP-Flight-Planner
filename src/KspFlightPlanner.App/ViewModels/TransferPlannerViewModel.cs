using System.Collections.ObjectModel;
using System.Globalization;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App.ViewModels;

public sealed record Fact(string Label, string Value);

/// <summary>Formatted details of one transfer leg (outbound or return).</summary>
public sealed record LegSummary(
    string Title,
    string Departure,
    string WaitLabel,
    string Wait,
    string Arrival,
    string FlightTime,
    string PhaseAngle,
    string EjectionPoint,
    string EjectionInclination,
    string EjectionDv,
    string CaptureLabel,
    string CaptureDv,
    string TransferDv);

public sealed class TransferPlannerViewModel : ObservableObject
{
    private readonly BodyRepository _bodies;
    private readonly GameCalendar _calendar = GameCalendar.Kerbin;
    private Dictionary<long, CelestialBody> _byId = [];

    private StarSystem? _selectedSystem;
    private CelestialBody? _selectedOrigin;
    private CelestialBody? _selectedDestination;
    private string _parkingAltitudeText = "";
    private string _arrivalAltitudeText = "";
    private int _earliestYear = 1;
    private int _earliestDay = 1;
    private ArrivalMode _arrivalMode = ArrivalMode.Circularize;
    private bool _includeLanding = true;

    private bool _planReturn = true;
    private int _minimumStayDays;
    private ArrivalMode _returnArrivalMode = ArrivalMode.Aerobrake;
    private bool _showReturnPlot;

    private int _busyCount;
    private string? _errorText;
    private int _version;
    private int _returnVersion;
    private bool _suspendRecalculate;

    private TransferRequest? _request;
    private TransferPlan? _plan;
    private TransferWindow? _selectedWindow;

    private TransferRequest? _returnRequest;
    private TransferPlan? _returnPlan;
    private TransferWindow? _returnWindow;

    public TransferPlannerViewModel(BodyRepository bodies)
    {
        _bodies = bodies;
        ResetToBestCommand = new RelayCommand(() => SelectedWindow = _plan?.BestWindow, () => IsCustomSelection);
        ResetReturnToBestCommand = new RelayCommand(() => ReturnWindow = _returnPlan?.BestWindow, () => IsCustomReturnSelection);

        // Only systems with orbital data can plan transfers.
        foreach (var system in bodies.GetSystems())
            if (bodies.GetBodies(system.Id).Count(b => b.Orbit is not null) >= 2) Systems.Add(system);
        SelectedSystem = Systems.FirstOrDefault();
    }

    public ObservableCollection<StarSystem> Systems { get; } = [];
    public ObservableCollection<CelestialBody> Origins { get; } = [];
    public ObservableCollection<CelestialBody> Destinations { get; } = [];
    public ObservableCollection<BudgetLine> BudgetLines { get; } = [];
    public ObservableCollection<Fact> OriginFacts { get; } = [];
    public ObservableCollection<Fact> DestinationFacts { get; } = [];
    public IReadOnlyList<ArrivalMode> ArrivalModes { get; } = Enum.GetValues<ArrivalMode>();
    public IReadOnlyList<ArrivalMode> ReturnArrivalModes { get; } = [ArrivalMode.Aerobrake, ArrivalMode.Circularize];

    public RelayCommand ResetToBestCommand { get; }
    public RelayCommand ResetReturnToBestCommand { get; }

    // ---------------- Inputs ----------------

    public StarSystem? SelectedSystem
    {
        get => _selectedSystem;
        set
        {
            if (!SetProperty(ref _selectedSystem, value) || value is null) return;
            var all = _bodies.GetBodies(value.Id);
            _byId = all.ToDictionary(b => b.Id);
            Origins.Clear();
            foreach (var b in all.Where(b => b.Orbit is not null && Siblings(b).Any())) Origins.Add(b);
            SelectedOrigin = Origins.FirstOrDefault(b => b.IsDefault) ?? Origins.FirstOrDefault();
        }
    }

    public CelestialBody? SelectedOrigin
    {
        get => _selectedOrigin;
        set
        {
            if (!SetProperty(ref _selectedOrigin, value)) return;
            _suspendRecalculate = true;
            Destinations.Clear();
            if (value is not null)
            {
                foreach (var b in Siblings(value)) Destinations.Add(b);
                ParkingAltitudeText = Km(value.DefaultParkingAltitudeM);
                ReturnArrivalMode = value.HasAtmosphere ? ArrivalMode.Aerobrake : ArrivalMode.Circularize;
            }
            SelectedDestination = Destinations.FirstOrDefault(b => b.Name == "Duna") ?? Destinations.FirstOrDefault();
            FillFacts(OriginFacts, value);
            _suspendRecalculate = false;
            Recalculate();
        }
    }

    public CelestialBody? SelectedDestination
    {
        get => _selectedDestination;
        set
        {
            if (!SetProperty(ref _selectedDestination, value)) return;
            if (value is not null)
            {
                ArrivalAltitudeText = Km(value.DefaultParkingAltitudeM);
                if (_arrivalMode == ArrivalMode.Aerobrake && !value.HasAtmosphere) SelectedArrivalMode = ArrivalMode.Circularize;
            }
            FillFacts(DestinationFacts, value);
            OnPropertyChanged(nameof(StayLabel));
            Recalculate();
        }
    }

    /// <summary>Parking orbit altitude at the origin, in km.</summary>
    public string ParkingAltitudeText
    {
        get => _parkingAltitudeText;
        set { if (SetProperty(ref _parkingAltitudeText, value)) Recalculate(); }
    }

    /// <summary>Target orbit altitude at the destination, in km.</summary>
    public string ArrivalAltitudeText
    {
        get => _arrivalAltitudeText;
        set { if (SetProperty(ref _arrivalAltitudeText, value)) Recalculate(); }
    }

    public int EarliestYear
    {
        get => _earliestYear;
        set { if (SetProperty(ref _earliestYear, Math.Max(1, value))) Recalculate(); }
    }

    public int EarliestDay
    {
        get => _earliestDay;
        set { if (SetProperty(ref _earliestDay, Math.Clamp(value, 1, _calendar.DaysPerYear))) Recalculate(); }
    }

    public ArrivalMode SelectedArrivalMode
    {
        get => _arrivalMode;
        set { if (SetProperty(ref _arrivalMode, value)) Recalculate(); }
    }

    public bool IncludeLanding
    {
        get => _includeLanding;
        set { if (SetProperty(ref _includeLanding, value)) RefreshBudget(); }
    }

    public bool PlanReturn
    {
        get => _planReturn;
        set
        {
            if (!SetProperty(ref _planReturn, value)) return;
            if (!value) ShowReturnPlot = false;
            RecalculateReturn();
        }
    }

    /// <summary>Minimum time to spend at the destination before heading home, in days.</summary>
    public int MinimumStayDays
    {
        get => _minimumStayDays;
        set { if (SetProperty(ref _minimumStayDays, Math.Max(0, value))) RecalculateReturn(); }
    }

    public string StayLabel => $"Minimum stay at {SelectedDestination?.Name ?? "destination"} (days)";

    public ArrivalMode ReturnArrivalMode
    {
        get => _returnArrivalMode;
        set { if (SetProperty(ref _returnArrivalMode, value)) RecalculateReturn(); }
    }

    public bool ShowReturnPlot
    {
        get => _showReturnPlot;
        set
        {
            if (!SetProperty(ref _showReturnPlot, value)) return;
            OnPropertyChanged(nameof(ShowOutboundPlot));
            RefreshPlotOutputs();
        }
    }

    public bool ShowOutboundPlot
    {
        get => !_showReturnPlot;
        set => ShowReturnPlot = !value;
    }

    // ---------------- State ----------------

    public bool IsBusy => _busyCount > 0;

    public string? ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    public TransferWindow? SelectedWindow
    {
        get => _selectedWindow;
        private set
        {
            if (!SetProperty(ref _selectedWindow, value)) return;
            Outbound = value is null ? null : Summarize(_request!, value, IsCustomSelection ? "Selected outbound transfer" : "Best launch window",
                "Wait from earliest", value.DepartureUt - _request!.EarliestDepartureUt);
            OnPropertyChanged(nameof(IsCustomSelection));
            RefreshPlotOutputs();
            RefreshBudget();
            RecalculateReturn();
        }
    }

    public TransferWindow? ReturnWindow
    {
        get => _returnWindow;
        private set
        {
            if (!SetProperty(ref _returnWindow, value)) return;
            Return = value is null || _selectedWindow is null ? null : Summarize(_returnRequest!, value,
                IsCustomReturnSelection ? "Selected return transfer" : "Best return window",
                $"Time at {_returnRequest!.Origin.Name}", value.DepartureUt - _selectedWindow.ArrivalUt);
            OnPropertyChanged(nameof(IsCustomReturnSelection));
            RefreshPlotOutputs();
            RefreshBudget();
        }
    }

    public LegSummary? Outbound { get; private set { field = value; OnPropertyChanged(); } }
    public LegSummary? Return { get; private set { field = value; OnPropertyChanged(); } }

    public bool IsCustomSelection => _plan is not null && _selectedWindow is not null && _plan.BestWindow != _selectedWindow;
    public bool IsCustomReturnSelection => _returnPlan is not null && _returnWindow is not null && _returnPlan.BestWindow != _returnWindow;

    /// <summary>Called when the user clicks a point on the porkchop plot currently shown.</summary>
    public void SelectPoint(double departureUt, double flightTimeS)
    {
        if (ShowReturnPlot)
        {
            if (_returnRequest is not null && TransferPlanner.Evaluate(_returnRequest, departureUt, flightTimeS) is { } w)
                ReturnWindow = w;
        }
        else if (_request is not null && TransferPlanner.Evaluate(_request, departureUt, flightTimeS) is { } w)
        {
            SelectedWindow = w;
        }
    }

    // ---------------- Outputs ----------------

    public string BudgetTotalLabel => Return is not null ? "Total (round trip)" : "Total (one way)";
    public string BudgetTotalText => BudgetLines.Count > 0 ? Format.Speed(MissionBudget.Total(BudgetLines)) : "—";

    public string HohmannPhaseText => _plan is { } p ? Deg(p.Hohmann.PhaseAngleDeg) : "—";
    public string HohmannTimeText => _plan is { } p ? _calendar.FormatDuration(p.Hohmann.TransferTimeS) : "—";
    public string HohmannEjectionText => _plan is { } p ? Format.Speed(p.Hohmann.EjectionDv) : "—";
    public string HohmannEjectionAngleText => _plan is { } p ? p.Hohmann.Ejection.ToString() : "—";
    public string HohmannCaptureText => _plan is { } p ? Format.Speed(p.Hohmann.CaptureDv) : "—";
    public string SynodicText => _plan is { } p ? _calendar.FormatDuration(p.Hohmann.SynodicPeriodS) : "—";

    public PorkchopPlot? CurrentPlot => ShowReturnPlot ? _returnPlan?.Porkchop : _plan?.Porkchop;
    public TransferWindow? CurrentPlotSelection => ShowReturnPlot ? _returnWindow : _selectedWindow;
    public string PlotTitle => ShowReturnPlot && _returnRequest is { } r ? $"Porkchop plot — return ({r.Origin.Name} → {r.Destination.Name})"
        : _request is { } o ? $"Porkchop plot — outbound ({o.Origin.Name} → {o.Destination.Name})" : "Porkchop plot";
    public string PorkchopStartLabel => CurrentPlot is { } p ? _calendar.FormatDay(p.DepartureStartUt) : "";
    public string PorkchopEndLabel => CurrentPlot is { } p ? _calendar.FormatDay(p.DepartureEndUt) : "";
    public string PorkchopMinFlightLabel => CurrentPlot is { } p ? $"{p.FlightTimeMinS / _calendar.SecondsPerDay:0} d" : "";
    public string PorkchopMaxFlightLabel => CurrentPlot is { } p ? $"{p.FlightTimeMaxS / _calendar.SecondsPerDay:0} d" : "";
    public string PorkchopScaleLabel => CurrentPlot is { } p ? $"{Format.Speed(p.TotalDv.Where(double.IsFinite).Min())} → 3×" : "";

    // ---------------- Logic ----------------

    private IEnumerable<CelestialBody> Siblings(CelestialBody body) =>
        _byId.Values.Where(b => b.Id != body.Id && b.Orbit is not null && b.ParentId == body.ParentId && body.ParentId is not null);

    private async void Recalculate()
    {
        if (_suspendRecalculate) return;
        var version = ++_version;
        ErrorText = null;

        var request = BuildRequest(out var error);
        if (request is null)
        {
            ErrorText = error;
            _request = null;
            _plan = null;
            SelectedWindow = null;
            RefreshHohmannOutputs();
            return;
        }

        SetBusy(+1);
        try
        {
            var plan = await Task.Run(() => TransferPlanner.Plan(request));
            if (version != _version) return;
            _request = request;
            _plan = plan;
            RefreshHohmannOutputs();
            _selectedWindow = null; // force refresh even if the window is unchanged
            SelectedWindow = plan.BestWindow;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            if (version == _version) ErrorText = ex.Message;
        }
        finally
        {
            SetBusy(-1);
        }
    }

    private async void RecalculateReturn()
    {
        var version = ++_returnVersion;
        if (!PlanReturn || _request is null || _selectedWindow is null)
        {
            _returnRequest = null;
            _returnPlan = null;
            ReturnWindow = null;
            RefreshPlotOutputs();
            return;
        }

        // Debounce: dragging on the outbound porkchop changes the arrival date many times a second.
        await Task.Delay(150);
        if (version != _returnVersion) return;

        var request = _request.Reverse(_selectedWindow.ArrivalUt + MinimumStayDays * _calendar.SecondsPerDay, ReturnArrivalMode);
        SetBusy(+1);
        try
        {
            var plan = await Task.Run(() => TransferPlanner.Plan(request));
            if (version != _returnVersion) return;
            _returnRequest = request;
            _returnPlan = plan;
            _returnWindow = null; // force refresh even if the window is unchanged
            ReturnWindow = plan.BestWindow;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            if (version == _returnVersion) ErrorText = $"Return trip: {ex.Message}";
        }
        finally
        {
            SetBusy(-1);
        }
    }

    private TransferRequest? BuildRequest(out string? error)
    {
        error = null;
        if (SelectedOrigin is not { } origin || SelectedDestination is not { } dest) return null;
        if (origin.ParentId is not { } parentId || !_byId.TryGetValue(parentId, out var parent)) return null;
        if (!TryKm(ParkingAltitudeText, out var parking)) { error = "Enter the parking orbit altitude in km."; return null; }
        if (!TryKm(ArrivalAltitudeText, out var arrival)) { error = "Enter the arrival orbit altitude in km."; return null; }
        if (parking >= origin.SoiAltitudeM) { error = $"Parking orbit is outside {origin.Name}'s sphere of influence."; return null; }
        if (arrival >= dest.SoiAltitudeM) { error = $"Arrival orbit is outside {dest.Name}'s sphere of influence."; return null; }

        return new TransferRequest(origin, dest, parent, parking, arrival,
            _calendar.ToUniversalTime(EarliestYear, EarliestDay), SelectedArrivalMode);
    }

    private LegSummary Summarize(TransferRequest request, TransferWindow w, string title, string waitLabel, double waitS) => new(
        Title: title,
        Departure: _calendar.FormatDate(w.DepartureUt),
        WaitLabel: waitLabel,
        Wait: _calendar.FormatDuration(waitS),
        Arrival: _calendar.FormatDate(w.ArrivalUt),
        FlightTime: _calendar.FormatDuration(w.FlightTimeS),
        PhaseAngle: Deg(w.PhaseAngleDeg),
        EjectionPoint: w.Ejection.ToString(),
        EjectionInclination: Deg(w.Ejection.InclinationDeg),
        EjectionDv: Format.Speed(w.EjectionDv),
        CaptureLabel: request.Arrival switch
        {
            ArrivalMode.Aerobrake => "Capture Δv (aerobrake)",
            ArrivalMode.Flyby => "Capture Δv (flyby)",
            _ => "Capture Δv",
        },
        CaptureDv: Format.Speed(w.CaptureDv),
        TransferDv: Format.Speed(w.TotalDv));

    private void RefreshBudget()
    {
        BudgetLines.Clear();
        if (_request is not null && _selectedWindow is { } w)
        {
            var returnLeg = _returnRequest is not null && _returnWindow is { } rw ? (_returnRequest, rw) : ((TransferRequest, TransferWindow)?)null;
            foreach (var line in MissionBudget.Build(_request, w, IncludeLanding, returnLeg)) BudgetLines.Add(line);
        }
        OnPropertyChanged(nameof(BudgetTotalText));
        OnPropertyChanged(nameof(BudgetTotalLabel));
    }

    private void RefreshHohmannOutputs()
    {
        foreach (var name in HohmannProperties) OnPropertyChanged(name);
    }

    private void RefreshPlotOutputs()
    {
        foreach (var name in PlotProperties) OnPropertyChanged(name);
    }

    private void SetBusy(int delta)
    {
        _busyCount += delta;
        OnPropertyChanged(nameof(IsBusy));
    }

    private static readonly string[] HohmannProperties =
    [
        nameof(HohmannPhaseText), nameof(HohmannTimeText), nameof(HohmannEjectionText), nameof(HohmannEjectionAngleText),
        nameof(HohmannCaptureText), nameof(SynodicText),
    ];

    private static readonly string[] PlotProperties =
    [
        nameof(CurrentPlot), nameof(CurrentPlotSelection), nameof(PlotTitle), nameof(PorkchopStartLabel),
        nameof(PorkchopEndLabel), nameof(PorkchopMinFlightLabel), nameof(PorkchopMaxFlightLabel), nameof(PorkchopScaleLabel),
    ];

    private void FillFacts(ObservableCollection<Fact> facts, CelestialBody? body)
    {
        facts.Clear();
        if (body is null) return;
        var parent = body.ParentId is { } pid && _byId.TryGetValue(pid, out var p) ? p : null;

        facts.Add(new("Type", body.BodyClass is null ? "—" : parent is null ? body.BodyClass : $"{body.BodyClass} of {parent.Name}"));
        facts.Add(new("Radius", Format.Distance(body.EquatorialRadiusM)));
        facts.Add(new("Surface gravity", $"{body.SurfaceGravity:0.00} m/s² ({body.SurfaceGravity / 9.80665:0.000} g)"));
        facts.Add(new("Sidereal day", _calendar.FormatDuration(Math.Abs(body.RotationPeriodS))));
        facts.Add(new("Sphere of influence", Format.Distance(body.SoiRadiusM)));
        if (body.Orbit is { } o && parent is not null)
        {
            facts.Add(new("Orbit (semi-major axis)", Format.Distance(o.SemiMajorAxisM)));
            facts.Add(new("Eccentricity / inclination", $"{o.Eccentricity:0.###} / {o.InclinationDeg:0.##}°"));
            facts.Add(new("Orbital period", _calendar.FormatDuration(Kepler.OrbitalPeriod(o, parent.Mu))));
        }
        if (body.Atmosphere is { } atm)
        {
            facts.Add(new("Atmosphere height", Format.Distance(atm.HeightM)));
            if (atm.SeaLevelPressureAtm is { } pAtm)
                facts.Add(new("Sea-level pressure", $"{pAtm:0.###} atm ({pAtm * Atmosphere.KilopascalsPerAtm:0.##} kPa)"));
            if (atm.ScaleHeightM is { } h) facts.Add(new("Scale height", Format.Distance(h)));
            if (atm.MinTempC is { } tMin && atm.MaxTempC is { } tMax) facts.Add(new("Temperature", $"{tMin:0} to {tMax:0} °C"));
            if (atm.HasOxygen is { } ox) facts.Add(new("Oxygen", ox ? "Yes (jet engines work)" : "No"));
        }
        else
        {
            facts.Add(new("Atmosphere", "None"));
        }
        facts.Add(new("Surface → low orbit Δv", body.SurfaceToLowOrbitDv is { } dv ? $"≈ {Format.Speed(dv)}" : "—"));
        facts.Add(new("Suggested low orbit", Format.Distance(body.DefaultParkingAltitudeM)));
    }

    private static string Km(double metres) => (metres / 1000).ToString("0.###", CultureInfo.CurrentCulture);

    private static bool TryKm(string text, out double metres)
    {
        var ok = double.TryParse(text.Trim().TrimEnd('k', 'K', 'm', 'M').Trim(),
            NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var km);
        metres = km * 1000;
        return ok && km > 0;
    }

    private static string Deg(double deg) => $"{deg:0.0}°";
}
