using System.Collections.ObjectModel;
using System.Globalization;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App.ViewModels;

public sealed class ResonantOrbitViewModel : ObservableObject
{
    private readonly BodyRepository _bodies;
    private readonly ConstellationRepository _plans;
    private readonly SaveSync? _sync;
    private readonly FlightRepository? _flights;
    private readonly PlannerEvents? _events;

    private StarSystem? _selectedSystem;
    private CelestialBody? _selectedBody;
    private int _satelliteCount = 3;
    private string _altitudeText = "";
    private bool _preferDiveOrbit;
    private bool _useOcclusion;
    private bool _showLineOfSight = true;
    private ResonantOrbitResult? _result;
    private string? _errorText;
    private string _planName = "";
    private SavedConstellation? _selectedPlan;

    public ResonantOrbitViewModel(BodyRepository bodies, ConstellationRepository plans, SaveSync? sync = null, FlightRepository? flights = null,
        PlannerEvents? events = null)
    {
        _bodies = bodies;
        _plans = plans;
        _sync = sync;
        _flights = flights;
        _events = events;
        FlyCommand = new RelayCommand(Fly, () => Result is not null && _flights is not null && SelectedSystem?.Name == StockSystem);

        UseSynchronousAltitudeCommand = new RelayCommand(
            () => SetAltitude(SelectedBody?.SynchronousAltitudeM), () => SelectedBody?.SynchronousAltitudeM is not null);
        UseMinimumLineOfSightCommand = new RelayCommand(
            () => SetAltitude(MinimumLineOfSightAltitude), () => MinimumLineOfSightAltitude is > 0);
        SavePlanCommand = new RelayCommand(SavePlan, () => Result is not null && !string.IsNullOrWhiteSpace(PlanName));
        LoadPlanCommand = new RelayCommand(LoadSelectedPlan, () => SelectedPlan is not null);
        DeletePlanCommand = new RelayCommand(DeleteSelectedPlan, () => SelectedPlan is not null);

        foreach (var system in _bodies.GetSystems()) Systems.Add(system);
        SelectedSystem = Systems.FirstOrDefault();
        RefreshPlans();
    }

    public ObservableCollection<StarSystem> Systems { get; } = [];
    public ObservableCollection<CelestialBody> Bodies { get; } = [];
    public ObservableCollection<SavedConstellation> SavedPlans { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];

    public RelayCommand UseSynchronousAltitudeCommand { get; }
    public RelayCommand UseMinimumLineOfSightCommand { get; }
    public RelayCommand SavePlanCommand { get; }
    public RelayCommand LoadPlanCommand { get; }
    public RelayCommand DeletePlanCommand { get; }

    /// <summary>Hands the deployment to the Flight tab, which times the burns from the carrier's real orbit.</summary>
    public RelayCommand FlyCommand { get; }

    public StarSystem? SelectedSystem
    {
        get => _selectedSystem;
        set
        {
            if (!SetProperty(ref _selectedSystem, value)) return;
            Bodies.Clear();
            if (value is null) return;
            foreach (var body in _bodies.GetBodies(value.Id)) Bodies.Add(body);
            SelectedBody = Bodies.FirstOrDefault(b => b.IsDefault) ?? Bodies.FirstOrDefault();
        }
    }

    public CelestialBody? SelectedBody
    {
        get => _selectedBody;
        set { if (SetProperty(ref _selectedBody, value)) Recalculate(); }
    }

    public int SatelliteCount
    {
        get => _satelliteCount;
        set { if (SetProperty(ref _satelliteCount, Math.Clamp(value, 1, 64))) Recalculate(); }
    }

    public string AltitudeText
    {
        get => _altitudeText;
        set { if (SetProperty(ref _altitudeText, value)) Recalculate(); }
    }

    public bool PreferDiveOrbit
    {
        get => _preferDiveOrbit;
        set { if (SetProperty(ref _preferDiveOrbit, value)) Recalculate(); }
    }

    public bool UseOcclusion
    {
        get => _useOcclusion;
        set { if (SetProperty(ref _useOcclusion, value)) Recalculate(); }
    }

    public bool ShowLineOfSight
    {
        get => _showLineOfSight;
        set => SetProperty(ref _showLineOfSight, value);
    }

    public ResonantOrbitResult? Result
    {
        get => _result;
        private set => SetProperty(ref _result, value);
    }

    public string? ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    public string PlanName
    {
        get => _planName;
        set => SetProperty(ref _planName, value);
    }

    public SavedConstellation? SelectedPlan
    {
        get => _selectedPlan;
        set => SetProperty(ref _selectedPlan, value);
    }

    public double? MinimumLineOfSightAltitude => SelectedBody is { } b
        ? OrbitMath.MinimumLineOfSightAltitude(b.EquatorialRadiusM, SatelliteCount, new OcclusionSettings(UseOcclusion).ModifierFor(b))
        : null;

    // ---- Body info ----
    public string BodyRadiusText => Format.Distance(SelectedBody?.EquatorialRadiusM);
    public string BodyMuText => SelectedBody is { } b ? b.Mu.ToString("0.#####E+0", CultureInfo.CurrentCulture) + " m³/s²" : "n/a";
    public string BodyAtmosphereText => SelectedBody is { HasAtmosphere: true } b ? Format.Distance(b.AtmosphereHeightM) : "None";
    public string BodyRotationText => SelectedBody is { } b
        ? Format.Duration(Math.Abs(b.RotationPeriodS)) + (b.RotationPeriodS < 0 ? " (retrograde)" : "")
        : "n/a";
    public string SynchronousAltitudeText => Format.Distance(SelectedBody?.SynchronousAltitudeM);
    public string MinimumLineOfSightText => MinimumLineOfSightAltitude is > 0 and var los && los < (SelectedBody?.SoiAltitudeM ?? 0)
        ? Format.Distance(los) : "n/a";
    public string SoiAltitudeText => Format.Distance(SelectedBody?.SoiAltitudeM);

    // ---- Results ----
    public string SatellitePeriodText => Result is { } r ? Format.Duration(r.SatellitePeriodS) : "—";
    public string OrbitTypeText => Result is { } r ? (r.IsDiveOrbit ? "Dive orbit" : "Resonant orbit") : "—";
    public string CarrierPeriodText => Result is { } r ? Format.Duration(r.CarrierPeriodS) : "—";
    public string CarrierApoapsisText => Result is { } r ? Format.Distance(r.CarrierApoapsisAltitudeM) : "—";
    public string CarrierPeriapsisText => Result is { } r ? Format.Distance(r.CarrierPeriapsisAltitudeM) : "—";
    public string InjectionDeltaVText => Result is { } r ? Format.Speed(r.InjectionDeltaV) : "—";

    private const string StockSystem = "KSP Stock";

    /// <summary>
    /// Creates the flight. When the craft being flown in KSP orbits this body it becomes the carrier and the burns are
    /// planned from its orbit straight away; otherwise the Flight tab asks for the carrier.
    /// </summary>
    private void Fly()
    {
        if (Result is not { } result || SelectedBody is not { } body || !Format.TryParseDistance(AltitudeText, out var altitude)) return;
        var flight = new Flight
        {
            Name = $"Deploy {SatelliteCount} satellites around {body.Name}",
            Kind = FlightKind.Resonant,
            BodyName = body.Name,
            SatelliteCount = SatelliteCount,
            AltitudeM = altitude,
            PreferDiveOrbit = PreferDiveOrbit,
            PlannedUt = _sync?.Now ?? 0,
            Note = "Switch to the carrier in KSP, then click \"Use the craft I'm flying\" to plan its burns from its orbit.",
        };
        if (_sync is { ActiveCraft: { } craft, Now: { } now } && _sync.OrbitOf(craft) is { } orbit && orbit.BodyName == body.Name)
        {
            var plan = ResonantDeployment.Plan(body, result, orbit.ToElements(), SatelliteCount, now + flight.LeadS);
            flight = flight with
            {
                Craft = craft,
                StartOrbit = plan.StartOrbit,
                Burns = plan.Burns,
                PlannedUt = now,
                Note = plan.Warnings.Count > 0 ? string.Join(" ", plan.Warnings) : null,
            };
        }
        var id = _flights!.Add(flight);
        _events?.RequestOpenFlight(id);
    }

    private void SetAltitude(double? metres)
    {
        if (metres is { } m) AltitudeText = Math.Round(m, 1).ToString("0.#", CultureInfo.CurrentCulture);
    }

    private void Recalculate()
    {
        Warnings.Clear();
        ErrorText = null;
        Result = null;

        if (SelectedBody is { } body && Format.TryParseDistance(AltitudeText, out var altitude))
        {
            try
            {
                Result = ResonantOrbitCalculator.Calculate(new ResonantOrbitRequest(
                    body, SatelliteCount, altitude, PreferDiveOrbit, new OcclusionSettings(UseOcclusion)));
                foreach (var w in Result.Warnings) Warnings.Add(w);
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException)
            {
                ErrorText = ex.Message.Split(" (Parameter")[0];
            }
        }
        else if (!string.IsNullOrWhiteSpace(AltitudeText))
        {
            ErrorText = "Enter the orbital altitude in metres.";
        }

        foreach (var name in DerivedProperties) OnPropertyChanged(name);
    }

    private static readonly string[] DerivedProperties =
    [
        nameof(MinimumLineOfSightAltitude), nameof(BodyRadiusText), nameof(BodyMuText), nameof(BodyAtmosphereText),
        nameof(BodyRotationText), nameof(SynchronousAltitudeText), nameof(MinimumLineOfSightText), nameof(SoiAltitudeText),
        nameof(SatellitePeriodText), nameof(OrbitTypeText), nameof(CarrierPeriodText), nameof(CarrierApoapsisText),
        nameof(CarrierPeriapsisText), nameof(InjectionDeltaVText),
    ];

    private void RefreshPlans()
    {
        SavedPlans.Clear();
        foreach (var plan in _plans.GetAll()) SavedPlans.Add(plan);
    }

    private void SavePlan()
    {
        if (SelectedBody is null || !Format.TryParseDistance(AltitudeText, out var altitude)) return;
        _plans.Add(new SavedConstellation
        {
            Name = PlanName.Trim(),
            BodyId = SelectedBody.Id,
            SatelliteCount = SatelliteCount,
            AltitudeM = altitude,
            PreferDiveOrbit = PreferDiveOrbit,
            UseOcclusion = UseOcclusion,
        });
        PlanName = "";
        RefreshPlans();
    }

    private void LoadSelectedPlan()
    {
        if (SelectedPlan is not { } plan || _bodies.GetBody(plan.BodyId) is not { } body) return;
        SelectedSystem = Systems.FirstOrDefault(s => s.Id == body.SystemId);
        SelectedBody = Bodies.FirstOrDefault(b => b.Id == body.Id);
        _satelliteCount = plan.SatelliteCount;
        _preferDiveOrbit = plan.PreferDiveOrbit;
        _useOcclusion = plan.UseOcclusion;
        OnPropertyChanged(nameof(SatelliteCount));
        OnPropertyChanged(nameof(PreferDiveOrbit));
        OnPropertyChanged(nameof(UseOcclusion));
        SetAltitude(plan.AltitudeM);
        Recalculate();
    }

    private void DeleteSelectedPlan()
    {
        if (SelectedPlan is not { } plan) return;
        _plans.Delete(plan.Id);
        RefreshPlans();
    }
}
