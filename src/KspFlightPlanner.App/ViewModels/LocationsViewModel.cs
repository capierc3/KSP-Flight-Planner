using System.Collections.ObjectModel;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App.ViewModels;

public sealed record FactGroup(string Title, IReadOnlyList<Fact> Facts);

/// <summary>A row in the locations tree: a body (read-only data) or a custom location (editable).</summary>
public sealed class LocationTreeItem : ObservableObject
{
    private bool _isSelected;
    private bool _isExpanded;

    public LocationTreeItem(string title, string subtitle, string color, CelestialBody? body = null, CustomLocation? location = null)
    {
        Title = title;
        Subtitle = subtitle;
        Color = color;
        Body = body;
        Location = location;
    }

    public string Title { get; }
    public string Subtitle { get; }
    public string Color { get; }
    public CelestialBody? Body { get; }
    public CustomLocation? Location { get; }
    public bool IsCustom => Location is not null;
    public string Glyph => IsCustom ? "◆" : "●";
    public ObservableCollection<LocationTreeItem> Children { get; } = [];

    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
}

/// <summary>
/// The Locations tab: every body with all its data (read-only) and the user's custom locations (editable).
/// </summary>
public sealed class LocationsViewModel : ObservableObject
{
    private readonly BodyRepository _bodies;
    private readonly DvMapRepository _maps;
    private readonly CustomLocationRepository _locations;
    private readonly PlannerEvents _events;
    private object? _detail;
    private LocationTreeItem? _selected;

    private IReadOnlyList<CelestialBody> _stock = [];
    private DeltaVMap? _map;

    public LocationsViewModel(BodyRepository bodies, DvMapRepository maps, CustomLocationRepository locations, PlannerEvents events)
    {
        _bodies = bodies;
        _maps = maps;
        _locations = locations;
        _events = events;

        NewLocationCommand = new RelayCommand(NewLocation, () => _map is not null);
        _events.LocationsChanged += sender => { if (sender != this) Reload(keepSelection: true); };
        Reload(keepSelection: false);
    }

    public ObservableCollection<LocationTreeItem> Tree { get; } = [];
    public RelayCommand NewLocationCommand { get; }

    /// <summary>Asks the user a yes/no question; the view supplies a dialog.</summary>
    public Func<string, bool> Confirm { get; set; } = _ => true;

    /// <summary>Lets the user pick image files; the view supplies the dialog. Returns null when cancelled.</summary>
    public Func<string[]?> PickImages { get; set; } = () => null;

    /// <summary>The right-hand panel: a <see cref="BodyDetailViewModel"/> or a <see cref="CustomLocationEditorViewModel"/>.</summary>
    public object? Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    /// <summary>Called by the view when the tree selection changes.</summary>
    public void Select(LocationTreeItem? item)
    {
        if (item is null || item == _selected) return;
        if (Detail is CustomLocationEditorViewModel { IsDirty: true } editor
            && !Confirm($"Discard unsaved changes to \"{editor.Title}\"?"))
            return;

        _selected = item;
        Detail = item.Location is { } location ? CreateEditor(location) : item.Body is { } body ? CreateBodyDetail(body) : null;
    }

    /// <summary>Opens a custom location (e.g. from the Mission Builder's ✎ button).</summary>
    public void SelectLocation(long locationId)
    {
        var item = All(Tree).FirstOrDefault(i => i.Location?.Id == locationId);
        if (item is null) return;
        ExpandTo(item);
        item.IsSelected = true;
        Select(item);
    }

    // ---------------- Building the tree ----------------

    private void Reload(bool keepSelection)
    {
        var selectedBody = _selected?.Body?.Id;
        var selectedLocation = _selected?.Location?.Id;

        var system = _bodies.GetSystems().FirstOrDefault(s => s.Name == "KSP Stock");
        _stock = system is null ? [] : _bodies.GetBodies(system.Id);
        _map = _maps.GetMap();
        var custom = _locations.GetAll();

        Tree.Clear();
        var byParent = _stock.ToLookup(b => b.ParentId);
        LocationTreeItem Build(CelestialBody body)
        {
            var subtitle = body.Orbit is { } o ? $"{body.BodyClass} · {Short(o.SemiMajorAxisM)}" : body.BodyClass ?? "";
            var item = new LocationTreeItem(body.Name, subtitle, body.Color, body: body);
            foreach (var child in byParent[body.Id].OrderBy(c => c.Orbit?.SemiMajorAxisM ?? 0)) item.Children.Add(Build(child));
            foreach (var l in custom.Where(l => l.BodyName == body.Name).OrderBy(l => l.PeriapsisAltitudeM))
                item.Children.Add(new LocationTreeItem(l.Name, CustomLocations.AltitudeText(l), body.Color, location: l));
            return item;
        }
        foreach (var root in byParent[null]) Tree.Add(Build(root));
        foreach (var root in Tree) root.IsExpanded = true;

        _selected = null;
        if (keepSelection && selectedLocation is { } lid && All(Tree).FirstOrDefault(i => i.Location?.Id == lid) is { } li)
            SelectQuietly(li);
        else if (keepSelection && selectedBody is { } bid && All(Tree).FirstOrDefault(i => i.Body?.Id == bid && !i.IsCustom) is { } bi)
            SelectQuietly(bi);
        else if (All(Tree).FirstOrDefault(i => i.Body?.Name == "Kerbin" && !i.IsCustom) is { } kerbin)
            SelectQuietly(kerbin);
    }

    private void SelectQuietly(LocationTreeItem item)
    {
        ExpandTo(item);
        item.IsSelected = true;
        _selected = item;
        Detail = item.Location is { } location ? CreateEditor(location) : item.Body is { } body ? CreateBodyDetail(body) : null;
    }

    private void ExpandTo(LocationTreeItem target)
    {
        bool Walk(LocationTreeItem node)
        {
            if (node == target) return true;
            foreach (var child in node.Children)
                if (Walk(child)) { node.IsExpanded = true; return true; }
            return false;
        }
        foreach (var root in Tree) Walk(root);
    }

    private static IEnumerable<LocationTreeItem> All(IEnumerable<LocationTreeItem> items) =>
        items.SelectMany(i => new[] { i }.Concat(All(i.Children)));

    // ---------------- Detail panels ----------------

    private BodyDetailViewModel CreateBodyDetail(CelestialBody body) =>
        new(body, _stock, _map, _locations.GetAll().Where(l => l.BodyName == body.Name).ToList(), id => SelectLocation(id));

    private CustomLocationEditorViewModel CreateEditor(CustomLocation location) =>
        new(location, BodyOptions(), Analyze, _locations, this, OnSaved, OnDeleted);

    private void NewLocation()
    {
        var body = _selected?.Body ?? _stock.First(b => b.Name == "Kerbin");
        var lowOrbit = _map?.Nodes.FirstOrDefault(n => n.BodyName == body.Name && n.Kind == "LowOrbit");
        var altitude = Math.Round(((lowOrbit?.AltitudeM ?? body.DefaultParkingAltitudeM) + 50_000) / 1000) * 1000;
        if (Detail is CustomLocationEditorViewModel { IsDirty: true } editor && !Confirm($"Discard unsaved changes to \"{editor.Title}\"?"))
            return;
        if (_selected is not null) _selected.IsSelected = false;
        _selected = null;
        Detail = CreateEditor(CustomLocation.Circular(0, "New location", body.Name, altitude));
    }

    private IReadOnlyList<CelestialBodyOption> BodyOptions() => _maps.GetCustomLocationBodies();

    private CustomLocationAnalysis Analyze(CustomLocation location)
    {
        if (_map is null) throw new ArgumentException("The Δv map is not available.");
        return CustomLocations.Analyze(location, _map, _stock.ToDictionary(b => b.Name));
    }

    private void OnSaved(long id)
    {
        _events.RaiseLocationsChanged(this);
        Reload(keepSelection: false);
        SelectLocation(id);
    }

    private void OnDeleted()
    {
        _events.RaiseLocationsChanged(this);
        Reload(keepSelection: false);
    }

    private static string Short(double metres) =>
        metres >= 1e9 ? $"{metres / 1e9:0.##} Gm" : metres >= 1e6 ? $"{metres / 1e6:0.#} Mm" : $"{metres / 1e3:N0} km";
}

/// <summary>Read-only view of everything known about a body.</summary>
public sealed class BodyDetailViewModel
{
    private const double G0 = 9.80665;

    public BodyDetailViewModel(CelestialBody body, IReadOnlyList<CelestialBody> all, DeltaVMap? map,
        IReadOnlyList<CustomLocation> customHere, Action<long> openLocation)
    {
        Body = body;
        var byId = all.ToDictionary(b => b.Id);
        var parent = body.ParentId is { } pid ? byId[pid] : null;
        var kerbin = all.FirstOrDefault(b => b.Name == "Kerbin");

        Title = body.Name;
        Subtitle = body.BodyClass is null ? "" : parent is null ? body.BodyClass : $"{body.BodyClass} of {parent.Name}";
        Color = body.Color;

        // Physical
        var physical = new List<Fact>
        {
            new("Equatorial radius", Units.Distance(body.EquatorialRadiusM)),
            new("Mass", Units.Scientific(body.MassKg, "kg")),
            new("Gravitational parameter μ", Units.Scientific(body.Mu, "m³/s²")),
            new("Surface gravity", $"{body.SurfaceGravity:0.000} m/s²  ({body.SurfaceGravity / G0:0.000} g)"),
            new("Escape velocity (surface)", Units.Speed(Math.Sqrt(2 * body.Mu / body.EquatorialRadiusM))),
            new("Sidereal day", Units.Duration(Math.Abs(body.RotationPeriodS)) + (body.RotationPeriodS < 0 ? "  retrograde" : "")),
            new("Equator rotation speed", Units.Speed(2 * Math.PI * body.EquatorialRadiusM / Math.Abs(body.RotationPeriodS))),
        };
        if (body.HighestElevationM is { } peak) physical.Add(new("Highest point", Units.Km(peak)));
        physical.Add(new("Sphere of influence", body.SoiRadiusM >= 1e14 ? "Whole system" : $"{Units.Distance(body.SoiRadiusM)} from centre"));
        physical.Add(new("Synchronous orbit", body.SynchronousAltitudeM is { } sync ? $"{Units.Km(sync)} altitude" : "Not possible (outside SOI or below surface)"));
        var groups = new List<FactGroup> { new("Physical", physical) };

        // Orbit around the parent
        if (body.Orbit is { } o && parent is not null)
        {
            var shape = new OrbitShape(parent.Mu, o.SemiMajorAxisM * (1 - o.Eccentricity), o.SemiMajorAxisM * (1 + o.Eccentricity));
            var orbit = new List<Fact>
            {
                new("Orbits", parent.Name),
                new("Semi-major axis", Units.Distance(o.SemiMajorAxisM)),
                new("Periapsis", $"{Units.Distance(shape.PeriapsisRadius)} from {parent.Name}'s centre"),
                new("Apoapsis", $"{Units.Distance(shape.ApoapsisRadius)} from {parent.Name}'s centre"),
                new("Eccentricity", o.Eccentricity.ToString("0.#####")),
                new("Inclination", Units.Degrees(o.InclinationDeg)),
                new("Longitude of ascending node", Units.Degrees(o.LongitudeOfAscendingNodeDeg)),
                new("Argument of periapsis", Units.Degrees(o.ArgumentOfPeriapsisDeg)),
                new("Mean anomaly at epoch (UT 0)", $"{o.MeanAnomalyAtEpochRad:0.####} rad"),
                new("Orbital period", Units.Duration(shape.PeriodS)),
                new("Mean orbital speed", Units.Speed(2 * Math.PI * o.SemiMajorAxisM / shape.PeriodS)),
                new("Speed at periapsis / apoapsis", $"{Units.Speed(shape.PeriapsisSpeed)} / {Units.Speed(shape.ApoapsisSpeed)}"),
            };
            if (kerbin?.Orbit is { } ko && kerbin.ParentId == body.ParentId && body.Name != "Kerbin")
            {
                var kerbinPeriod = OrbitMath.Period(parent.Mu, ko.SemiMajorAxisM);
                var synodic = 1 / Math.Abs(1 / kerbinPeriod - 1 / shape.PeriodS);
                var aTransfer = (ko.SemiMajorAxisM + o.SemiMajorAxisM) / 2;
                var transferTime = Math.PI * Math.Sqrt(aTransfer * aTransfer * aTransfer / parent.Mu);
                var phase = 180 - 360 * transferTime / shape.PeriodS;
                orbit.Add(new("Launch windows from Kerbin every", Units.Duration(synodic)));
                orbit.Add(new("Hohmann transfer from Kerbin", $"{GameCalendar.Kerbin.FormatDuration(transferTime)}, phase angle {((phase + 540) % 360) - 180:0.0}°"));
            }
            groups.Add(new FactGroup("Orbit", orbit));
        }

        // Low orbit (from the Δv map)
        var lowOrbit = map?.Nodes.FirstOrDefault(n => n.BodyName == body.Name && n.Kind == "LowOrbit" && !n.IsCustom);
        if (lowOrbit?.AltitudeM is { } loAlt)
        {
            var r = body.EquatorialRadiusM + loAlt;
            groups.Add(new FactGroup("Low orbit (Δv map)", [
                new("Altitude", Units.Km(loAlt)),
                new("Orbital speed", Units.Speed(OrbitMath.CircularVelocity(body.Mu, r))),
                new("Period", Units.Duration(OrbitMath.Period(body.Mu, r))),
                new("Escape from low orbit", Units.Dv(OrbitMath.HyperbolicBurnDv(body.Mu, r, 0))),
                new("Surface → low orbit (map)", body.SurfaceToLowOrbitDv is { } dv ? $"≈ {Units.Dv(dv)}" : "—"),
            ]));
        }

        // Atmosphere
        if (body.Atmosphere is { } atm)
        {
            var facts = new List<Fact> { new("Height", Units.Km(atm.HeightM)) };
            if (atm.SeaLevelPressureAtm is { } p) facts.Add(new("Sea-level pressure", $"{p:0.####} atm  ({p * Atmosphere.KilopascalsPerAtm:0.###} kPa)"));
            if (atm.ScaleHeightM is { } h) facts.Add(new("Scale height", Units.Km(h)));
            if (atm.MinTempC is { } tMin && atm.MaxTempC is { } tMax) facts.Add(new("Temperature", $"{tMin:0.#} to {tMax:0.#} °C"));
            if (atm.HasOxygen is { } ox) facts.Add(new("Oxygen", ox ? "Yes — jet engines work" : "No"));
            groups.Add(new FactGroup("Atmosphere", facts));
        }
        else
        {
            groups.Add(new FactGroup("Atmosphere", [new("Atmosphere", "None — no aerobraking or parachutes")]));
        }

        // Moons
        var moons = all.Where(b => b.ParentId == body.Id).OrderBy(b => b.Orbit?.SemiMajorAxisM ?? 0).ToList();
        if (moons.Count > 0)
            groups.Add(new FactGroup(body.ParentId is null ? "Planets" : "Moons",
                moons.Select(m => new Fact(m.Name, m.Orbit is { } mo ? Units.Distance(mo.SemiMajorAxisM) : "—")).ToList()));

        Groups = groups;

        // Stops on the Δv map for this body
        MapStops = map is null ? [] : map.Nodes
            .Where(n => n.BodyName == body.Name && !n.IsCustom)
            .Select(n => new MapStopRow(
                n.Label,
                map.ParentOf(n)?.DisplayName ?? "— (start of the map)",
                n.ParentId is null ? "—" : Units.Dv(n.DvFromParent) + (n.IsOverridden ? $"  (edited; map {n.MapDvFromParent:N0})" : ""),
                n.PlaneChangeDv > 0 ? $"+{n.PlaneChangeDv:N0}" : "",
                string.Join(", ", new[] { n.AerobrakeDown ? "arriving" : null, n.AerobrakeUp ? "returning" : null }.Where(s => s is not null)),
                n.FlightTime ?? ""))
            .ToList();

        CustomLocations = customHere.Select(l => new CustomLocationLink(l, () => openLocation(l.Id))).ToList();
    }

    public CelestialBody Body { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string Color { get; }
    public IReadOnlyList<FactGroup> Groups { get; }
    public IReadOnlyList<MapStopRow> MapStops { get; }
    public IReadOnlyList<CustomLocationLink> CustomLocations { get; }
    public bool HasCustomLocations => CustomLocations.Count > 0;
    public string Sources => "Sources: KSP wiki <Body>/Param pages (orbit, atmosphere, gravity); Eric Meyer's Resonant Orbit " +
                             "Calculator data (radius, SOI, day); community Δv map 1.0.4 (map stops). Derived values: see the Math guide.";
}

public sealed record MapStopRow(string Stop, string From, string DeltaV, string PlaneChange, string Aerobrake, string FlightTime);

public sealed class CustomLocationLink(CustomLocation location, Action open)
{
    public string Name => location.Name;
    public string Summary => CustomLocations.AltitudeText(location);
    public RelayCommand OpenCommand { get; } = new(open);
}
