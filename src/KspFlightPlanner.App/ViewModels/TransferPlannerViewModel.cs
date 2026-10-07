using System.Collections.ObjectModel;
using System.Globalization;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App.ViewModels;

public sealed record Fact(string Label, string Value);

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

    private bool _isBusy;
    private string? _errorText;
    private int _version;
    private TransferRequest? _request;
    private TransferPlan? _plan;
    private TransferWindow? _selectedWindow;
    private bool _suspendRecalculate;

    public TransferPlannerViewModel(BodyRepository bodies)
    {
        _bodies = bodies;
        ResetToBestCommand = new RelayCommand(() => SelectedWindow = _plan?.BestWindow, () => IsCustomSelection);

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

    public RelayCommand ResetToBestCommand { get; }

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
        set { if (SetProperty(ref _includeLanding, value)) RefreshWindowOutputs(); }
    }

    // ---------------- State ----------------

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string? ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    public TransferPlan? Plan
    {
        get => _plan;
        private set => SetProperty(ref _plan, value);
    }

    public TransferWindow? SelectedWindow
    {
        get => _selectedWindow;
        private set
        {
            if (SetProperty(ref _selectedWindow, value)) RefreshWindowOutputs();
        }
    }

    public bool IsCustomSelection => _plan is not null && _selectedWindow is not null && !ReferenceEquals(_plan.BestWindow, _selectedWindow);

    /// <summary>Called when the user clicks a point on the porkchop plot.</summary>
    public void SelectPoint(double departureUt, double flightTimeS)
    {
        if (_request is null) return;
        if (TransferPlanner.Evaluate(_request, departureUt, flightTimeS) is { } window) SelectedWindow = window;
    }

    // ---------------- Outputs ----------------

    public string WindowTitle => IsCustomSelection ? "Selected transfer" : "Best launch window";
    public string DepartureText => SelectedWindow is { } w ? _calendar.FormatDate(w.DepartureUt) : "—";
    public string WaitText => SelectedWindow is { } w && _request is { } r ? _calendar.FormatDuration(w.DepartureUt - r.EarliestDepartureUt) : "—";
    public string ArrivalText => SelectedWindow is { } w ? _calendar.FormatDate(w.ArrivalUt) : "—";
    public string FlightTimeText => SelectedWindow is { } w ? _calendar.FormatDuration(w.FlightTimeS) : "—";
    public string PhaseAngleText => SelectedWindow is { } w ? Deg(w.PhaseAngleDeg) : "—";
    public string EjectionAngleText => SelectedWindow is { } w ? w.Ejection.ToString() : "—";
    public string EjectionInclinationText => SelectedWindow is { } w ? Deg(w.Ejection.InclinationDeg) : "—";
    public string EjectionDvText => SelectedWindow is { } w ? Format.Speed(w.EjectionDv) : "—";
    public string CaptureDvText => SelectedWindow is { } w ? Format.Speed(w.CaptureDv) : "—";
    public string TransferDvText => SelectedWindow is { } w ? Format.Speed(w.TotalDv) : "—";
    public string BudgetTotalText => BudgetLines.Count > 0 ? Format.Speed(MissionBudget.Total(BudgetLines)) : "—";

    public string HohmannPhaseText => Plan is { } p ? Deg(p.Hohmann.PhaseAngleDeg) : "—";
    public string HohmannTimeText => Plan is { } p ? _calendar.FormatDuration(p.Hohmann.TransferTimeS) : "—";
    public string HohmannEjectionText => Plan is { } p ? Format.Speed(p.Hohmann.EjectionDv) : "—";
    public string HohmannEjectionAngleText => Plan is { } p ? p.Hohmann.Ejection.ToString() : "—";
    public string HohmannCaptureText => Plan is { } p ? Format.Speed(p.Hohmann.CaptureDv) : "—";
    public string SynodicText => Plan is { } p ? _calendar.FormatDuration(p.Hohmann.SynodicPeriodS) : "—";

    public string PorkchopStartLabel => Plan is { } p ? _calendar.FormatDay(p.Porkchop.DepartureStartUt) : "";
    public string PorkchopEndLabel => Plan is { } p ? _calendar.FormatDay(p.Porkchop.DepartureEndUt) : "";
    public string PorkchopMinFlightLabel => Plan is { } p ? $"{p.Porkchop.FlightTimeMinS / _calendar.SecondsPerDay:0} d" : "";
    public string PorkchopMaxFlightLabel => Plan is { } p ? $"{p.Porkchop.FlightTimeMaxS / _calendar.SecondsPerDay:0} d" : "";
    public string PorkchopScaleLabel => Plan is { } p ? $"{Format.Speed(p.Porkchop.TotalDv.Where(double.IsFinite).Min())} → 3×" : "";

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
            Plan = null;
            SelectedWindow = null;
            RefreshPlanOutputs();
            return;
        }

        IsBusy = true;
        try
        {
            var plan = await Task.Run(() => TransferPlanner.Plan(request));
            if (version != _version) return;
            _request = request;
            Plan = plan;
            SelectedWindow = plan.BestWindow;
            RefreshPlanOutputs();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            if (version == _version) ErrorText = ex.Message;
        }
        finally
        {
            if (version == _version) IsBusy = false;
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

    private void RefreshWindowOutputs()
    {
        BudgetLines.Clear();
        if (_request is not null && SelectedWindow is { } w)
            foreach (var line in MissionBudget.Build(_request, w, IncludeLanding)) BudgetLines.Add(line);

        foreach (var name in WindowProperties) OnPropertyChanged(name);
    }

    private void RefreshPlanOutputs()
    {
        foreach (var name in PlanProperties) OnPropertyChanged(name);
        RefreshWindowOutputs();
    }

    private static readonly string[] WindowProperties =
    [
        nameof(IsCustomSelection), nameof(WindowTitle), nameof(DepartureText), nameof(WaitText), nameof(ArrivalText),
        nameof(FlightTimeText), nameof(PhaseAngleText), nameof(EjectionAngleText), nameof(EjectionInclinationText),
        nameof(EjectionDvText), nameof(CaptureDvText), nameof(TransferDvText), nameof(BudgetTotalText),
    ];

    private static readonly string[] PlanProperties =
    [
        nameof(HohmannPhaseText), nameof(HohmannTimeText), nameof(HohmannEjectionText), nameof(HohmannEjectionAngleText),
        nameof(HohmannCaptureText),
        nameof(SynodicText), nameof(PorkchopStartLabel), nameof(PorkchopEndLabel), nameof(PorkchopMinFlightLabel),
        nameof(PorkchopMaxFlightLabel), nameof(PorkchopScaleLabel),
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
