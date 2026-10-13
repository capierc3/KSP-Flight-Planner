using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using KspFlightPlanner.Core;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App.ViewModels;

/// <summary>An image attached to a custom location, decoded for display.</summary>
public sealed record LocationImageItem(long Id, string FileName, BitmapSource Thumbnail, byte[] Data);

/// <summary>
/// Editor for one custom location. Only independent values are editable (name, body, periapsis, apoapsis,
/// inclination, description, images); everything derived from them is recalculated and shown read-only,
/// so the stored data can never contradict itself.
/// </summary>
public sealed class CustomLocationEditorViewModel : ObservableObject
{
    private const long MaxImageBytes = 15 * 1024 * 1024;

    private readonly Func<CustomLocation, CustomLocationAnalysis> _analyze;
    private readonly Func<CustomLocation, double?> _phaseFromHome;
    private readonly GameCalendar _calendar = GameCalendar.Kerbin;
    private readonly CustomLocationRepository _repository;
    private readonly LocationsViewModel _owner;
    private readonly Action<long> _saved;
    private readonly Action _deleted;
    private CustomLocation _original;
    private CustomLocation? _loaded; // _original as the inputs show it (rounded for display)

    private string _name = "";
    private CelestialBodyOption? _body;
    private bool _isElliptical;
    private string _periapsisKm = "";
    private string _apoapsisKm = "";
    private string _inclination = "0";
    private string _description = "";
    private bool _isShared;
    private string? _error;

    private bool _isPlaced;
    private bool _usePhaseAngle;
    private string _lan = "0";
    private string _argPe = "0";
    private string _meanAnomaly = "0";
    private string _phaseAngle = "0";
    private string _epochYear = "1";
    private string _epochDay = "1";
    private string _epochHour = "0";

    public CustomLocationEditorViewModel(CustomLocation location, IReadOnlyList<CelestialBodyOption> bodies,
        Func<CustomLocation, CustomLocationAnalysis> analyze, Func<CustomLocation, double?> phaseFromHome,
        CustomLocationRepository repository, LocationsViewModel owner, Action<long> saved, Action deleted)
    {
        Bodies = bodies;
        _analyze = analyze;
        _phaseFromHome = phaseFromHome;
        _repository = repository;
        _owner = owner;
        _saved = saved;
        _deleted = deleted;
        _original = location;

        SaveCommand = new RelayCommand(Save, () => IsDirty && Error is null);
        RevertCommand = new RelayCommand(() => Load(_original), () => IsDirty);
        DeleteCommand = new RelayCommand(Delete, () => !IsNew);
        UnlinkCommand = new RelayCommand(Unlink, () => IsLinked);
        AddImagesCommand = new RelayCommand(AddImages, () => !IsNew);
        RemoveImageCommand = new RelayCommand<LocationImageItem>(RemoveImage);

        Load(location);
        if (!IsNew) LoadImages();
    }

    public IReadOnlyList<CelestialBodyOption> Bodies { get; }
    public ObservableCollection<Fact> Derived { get; } = [];
    public ObservableCollection<LocationImageItem> Images { get; } = [];

    public RelayCommand SaveCommand { get; }
    public RelayCommand RevertCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand UnlinkCommand { get; }
    public RelayCommand AddImagesCommand { get; }
    public RelayCommand<LocationImageItem> RemoveImageCommand { get; }

    public bool IsNew => _original.Id == 0;
    public string Title => IsNew ? "New location" : _original.Name;
    /// <summary>Follows a vessel in a KSP save: its orbit and position are replaced whenever the game saves.</summary>
    public bool IsLinked => _original.IsLinked;
    public string LinkText => IsLinked
        ? $"Linked to a vessel in the KSP save \"{_original.KspSave}\" — the orbit and position update whenever KSP saves, so edits to them are overwritten. Name, description and images stay yours."
        : "";

    public string ImagesHint => IsNew ? "Save the location first, then add images." : "PNG, JPG, BMP or GIF up to 15 MB. Stored in the planner database.";

    // ---------------- Editable values ----------------

    public string Name { get => _name; set { if (SetProperty(ref _name, value)) Changed(); } }

    public CelestialBodyOption? Body { get => _body; set { if (SetProperty(ref _body, value)) Changed(); } }

    /// <summary>Off: circular (apoapsis follows periapsis). On: separate periapsis and apoapsis.</summary>
    public bool IsElliptical
    {
        get => _isElliptical;
        set
        {
            if (!SetProperty(ref _isElliptical, value)) return;
            if (!value) _apoapsisKm = _periapsisKm;
            OnPropertyChanged(nameof(ApoapsisKm));
            OnPropertyChanged(nameof(PeriapsisLabel));
            Changed();
        }
    }

    public string PeriapsisLabel => IsElliptical ? "Periapsis altitude (km)" : "Altitude (km)";

    public string PeriapsisKm
    {
        get => _periapsisKm;
        set
        {
            if (!SetProperty(ref _periapsisKm, value)) return;
            if (!IsElliptical) { _apoapsisKm = value; OnPropertyChanged(nameof(ApoapsisKm)); }
            Changed();
        }
    }

    public string ApoapsisKm { get => _apoapsisKm; set { if (SetProperty(ref _apoapsisKm, value)) Changed(); } }

    public string Inclination { get => _inclination; set { if (SetProperty(ref _inclination, value)) Changed(); } }

    public string Description { get => _description; set { if (SetProperty(ref _description, value)) OnPropertyChanged(nameof(IsDirty)); } }

    /// <summary>Shown in every KSP save, not just the one it was made in.</summary>
    public bool IsShared { get => _isShared; set { if (SetProperty(ref _isShared, value)) OnPropertyChanged(nameof(IsDirty)); } }

    /// <summary>A save's own vessels always stay in that save.</summary>
    public bool ShowShare => !IsLinked;

    /// <summary>Only with a save chosen: otherwise everything is shared.</summary>
    public bool CanShare => _repository.ActiveSave is not null;

    public string ShareText => _repository.ActiveSave is { } save
        ? $"Shared across all saves (otherwise only in \"{save}\")"
        : "Shared across all saves (choose a KSP save in Settings to keep locations to one save)";

    // ---------------- Position (placed locations) ----------------

    /// <summary>False: planned (doesn't exist yet, no fixed position). True: placed (exists in your save).</summary>
    public bool IsPlaced
    {
        get => _isPlaced;
        set { if (SetProperty(ref _isPlaced, value)) { OnPropertyChanged(nameof(IsPlanned)); Changed(); } }
    }

    public bool IsPlanned { get => !IsPlaced; set => IsPlaced = !value; }

    /// <summary>Enter the position as a phase angle from Kerbin instead of orbital elements.</summary>
    public bool UsePhaseAngle
    {
        get => _usePhaseAngle;
        set { if (SetProperty(ref _usePhaseAngle, value)) { OnPropertyChanged(nameof(UseElements)); Changed(); } }
    }

    public bool UseElements { get => !UsePhaseAngle; set => UsePhaseAngle = !value; }

    public string Lan { get => _lan; set { if (SetProperty(ref _lan, value)) Changed(); } }
    public string ArgumentOfPeriapsis { get => _argPe; set { if (SetProperty(ref _argPe, value)) Changed(); } }
    public string MeanAnomaly { get => _meanAnomaly; set { if (SetProperty(ref _meanAnomaly, value)) Changed(); } }
    public string PhaseAngle { get => _phaseAngle; set { if (SetProperty(ref _phaseAngle, value)) Changed(); } }
    public string EpochYear { get => _epochYear; set { if (SetProperty(ref _epochYear, value)) Changed(); } }
    public string EpochDay { get => _epochDay; set { if (SetProperty(ref _epochDay, value)) Changed(); } }
    public string EpochHour { get => _epochHour; set { if (SetProperty(ref _epochHour, value)) Changed(); } }

    // ---------------- State ----------------

    /// <summary>Why the current values can't be saved, or null when they're valid.</summary>
    public string? Error { get => _error; private set => SetProperty(ref _error, value); }

    public bool IsDirty => IsNew || Current() is not { } c || c != _loaded || IsShared != IsSharedNow(_original);

    private bool IsSharedNow(CustomLocation location) =>
        location.KspSave is null && (location.Id != 0 || _repository.ActiveSave is null);

    // ---------------- Logic ----------------

    private void Load(CustomLocation location)
    {
        _name = location.Name;
        _body = Bodies.FirstOrDefault(b => b.Name == location.BodyName) ?? Bodies.FirstOrDefault();
        _isElliptical = !location.IsCircular;
        _periapsisKm = Km(location.PeriapsisAltitudeM);
        _apoapsisKm = Km(location.ApoapsisAltitudeM);
        _inclination = location.InclinationDeg.ToString("0.###", CultureInfo.CurrentCulture);
        _description = location.Description ?? "";
        _isShared = IsSharedNow(location);
        _isPlaced = location.IsPlaced;
        _usePhaseAngle = location.Mode == PositionMode.PhaseAngle;
        _lan = Num(location.LanDeg);
        _argPe = Num(location.ArgumentOfPeriapsisDeg);
        _meanAnomaly = Num(location.MeanAnomalyDeg);
        _phaseAngle = Num(location.PhaseAngleDeg);
        var year = (int)Math.Floor(location.EpochUt / _calendar.SecondsPerYear);
        var day = (int)Math.Floor((location.EpochUt - year * _calendar.SecondsPerYear) / _calendar.SecondsPerDay);
        var hour = (location.EpochUt - year * _calendar.SecondsPerYear - day * _calendar.SecondsPerDay) / 3600;
        _epochYear = (year + 1).ToString(CultureInfo.CurrentCulture);
        _epochDay = (day + 1).ToString(CultureInfo.CurrentCulture);
        _epochHour = Num(hour);
        foreach (var p in new[] { nameof(Name), nameof(Body), nameof(IsElliptical), nameof(PeriapsisKm), nameof(ApoapsisKm),
                     nameof(Inclination), nameof(Description), nameof(IsShared), nameof(PeriapsisLabel), nameof(Title), nameof(IsPlaced), nameof(IsPlanned),
                     nameof(UsePhaseAngle), nameof(UseElements), nameof(Lan), nameof(ArgumentOfPeriapsis), nameof(MeanAnomaly),
                     nameof(PhaseAngle), nameof(EpochYear), nameof(EpochDay), nameof(EpochHour) })
            OnPropertyChanged(p);
        _loaded = Current();
        Changed();
    }

    /// <summary>The location described by the inputs, or null with <see cref="Error"/> set if they don't parse.</summary>
    private CustomLocation? Current()
    {
        if (Body is not { } b) return null;
        if (!TryNumber(PeriapsisKm, out var pe) || !TryNumber(IsElliptical ? ApoapsisKm : PeriapsisKm, out var ap) || !TryNumber(Inclination, out var inc))
            return null;
        if (!TryNumber(Lan, out var lan) || !TryNumber(ArgumentOfPeriapsis, out var argPe) || !TryNumber(MeanAnomaly, out var mean)
            || !TryNumber(PhaseAngle, out var phase) || !int.TryParse(EpochYear, out var year) || !int.TryParse(EpochDay, out var day)
            || !TryNumber(EpochHour, out var hour) || year < 1 || day < 1 || day > _calendar.DaysPerYear || hour < 0 || hour >= _calendar.HoursPerDay)
            return null;
        return new CustomLocation(_original.Id, Name.Trim(), b.Name, pe * 1000, ap * 1000, inc, Normalize(Description),
            IsPlaced, UsePhaseAngle ? PositionMode.PhaseAngle : PositionMode.Elements, lan, argPe, mean, phase,
            _calendar.ToUniversalTime(year, day, hour))
        {
            KspSave = _original.KspSave,
            KspVesselId = _original.KspVesselId,
        };
    }

    private void Changed()
    {
        Derived.Clear();
        var location = Current();
        if (location is null)
        {
            Error = $"Enter numbers for the altitudes, inclination and position (day 1–{_calendar.DaysPerYear}, hour 0–{_calendar.HoursPerDay}).";
        }
        else
        {
            try
            {
                Fill(_analyze(location));
                Error = null;
            }
            catch (ArgumentException ex)
            {
                Error = ex.Message;
            }
        }
        OnPropertyChanged(nameof(IsDirty));
    }

    private void Fill(CustomLocationAnalysis a)
    {
        var o = a.Orbit;
        var bodyRadius = Body!.Body.EquatorialRadiusM;
        void Add(string label, string value) => Derived.Add(new Fact(label, value));

        Add("Shape", o.IsCircular ? "Circular" : $"Elliptical, e = {o.Eccentricity:0.#####}");
        Add("Semi-major axis", $"{Units.Distance(o.SemiMajorAxis)} from centre ({Units.Km(o.SemiMajorAxis - bodyRadius)} mean altitude)");
        Add("Orbital period", Units.Duration(o.PeriodS));
        Add(o.IsCircular ? "Orbital speed" : "Speed at periapsis / apoapsis",
            o.IsCircular ? Units.Speed(o.PeriapsisSpeed) : $"{Units.Speed(o.PeriapsisSpeed)} / {Units.Speed(o.ApoapsisSpeed)}");
        // Where the trip to this location branches off the Δv map (not what it orbits — that's the body above).
        Add("Departs from", a.IsInterplanetary
            ? $"{a.AttachedTo.BodyName} escape (SOI edge) → transfer orbit around {Body.Name}"
            : $"{a.AttachedTo.DisplayName} → {(o.IsCircular ? "Hohmann transfer" : "two-burn transfer")}");

        if (a.IsInterplanetary)
        {
            Add("Leave Kerbin with v∞", Units.Speed(a.DepartureVInf ?? 0));
            Add("Ejection from low Kerbin orbit", $"{Units.Dv(a.EjectionFromLowOrbit ?? 0)} (map already charges the first {Units.Dv(a.AttachedTo.MapDvFromParent)})");
            Add("Extra ejection Δv", Units.Dv(a.DepartureDv));
            Add("Arrival burn", Units.Dv(a.ArrivalDv));
        }
        else
        {
            Add($"Burn 1 at {a.AttachedTo.Label.ToLowerInvariant()}", Units.Dv(a.DepartureDv));
            Add("Burn 2 at the target", Units.Dv(a.ArrivalDv));
        }
        Add("Δv on the map edge", $"{Units.Dv(a.Node.DvFromParent)}  (transfer ≈ {a.Node.FlightTime})");
        if (a.PlaneChangeDv > 0) Add("Plane change (at apoapsis)", $"{Units.Dv(a.PlaneChangeDv)} — added when 'Include worst-case plane changes' is on");
        if (a.Node.AerobrakeUp) Add("Coming home by aerobraking", $"{Units.Dv(a.Node.AerobrakeResidualDv)} burn still needed");
        var location = Current()!;
        Add("Position", location.IsPlaced ? "Placed — the Transfer Planner finds real launch windows"
                                          : "Planned — no fixed position yet, so any departure time works");
        if (location.IsPlaced && _phaseFromHome(location) is { } phase)
            Add($"Ahead of Kerbin on {_calendar.FormatDay(location.EpochUt)}", $"{phase:0.0}°  (check against the game)");
    }

    private void Save()
    {
        if (Current() is not { } location || Error is not null) return;
        long id;
        if (IsNew) id = _repository.Add(location);
        else
        {
            _repository.Update(location);
            id = location.Id;
        }
        if (IsShared != IsSharedNow(_original)) _repository.SetShared(id, IsShared);
        _original = _loaded = location with { Id = id, KspSave = _repository.Get(id)?.KspSave };
        OnPropertyChanged(nameof(IsDirty));
        _saved(id);
    }

    private void Delete()
    {
        if (!_owner.Confirm($"Delete \"{_original.Name}\" and its images?\n\nStops using it in the open mission move to its parent orbit, " +
                            "and saved missions drop their stops there when opened."))
            return;
        _repository.Delete(_original.Id);
        _deleted();
    }

    private void Unlink()
    {
        if (!_owner.Confirm($"Stop \"{_original.Name}\" following its vessel in the KSP save?\n\nIt keeps its current orbit; " +
                            "you can import the vessel again later."))
            return;
        _repository.Unlink(_original.Id);
        _saved(_original.Id);
    }

    private void AddImages()
    {
        if (_owner.PickImages() is not { } files) return;
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > MaxImageBytes) continue;
            var data = File.ReadAllBytes(file);
            if (Decode(data) is null) continue; // not an image WPF can read
            _repository.AddImage(_original.Id, info.Name, data);
        }
        LoadImages();
    }

    private void RemoveImage(LocationImageItem image)
    {
        if (!_owner.Confirm($"Remove {image.FileName}?")) return;
        _repository.DeleteImage(image.Id);
        LoadImages();
    }

    private void LoadImages()
    {
        Images.Clear();
        foreach (var image in _repository.GetImages(_original.Id))
            if (Decode(image.Data, 220) is { } thumb) Images.Add(new LocationImageItem(image.Id, image.FileName, thumb, image.Data));
    }

    /// <summary>Decodes image bytes (optionally downscaled for thumbnails); null if they aren't a readable image.</summary>
    public static BitmapSource? Decode(byte[] data, int? decodeHeight = null)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(data);
            if (decodeHeight is { } h) bitmap.DecodePixelHeight = h;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value);

    private static string Num(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);

    private static string Km(double metres) => (metres / 1000).ToString("0.###", CultureInfo.CurrentCulture);

    private static string? Normalize(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
