using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.App.ViewModels;

/// <summary>How a rendezvous added to the mission starts: from where the mission is before its last stop.</summary>
public enum RendezvousStartKind
{
    /// <summary>On Kerbin's surface: launch from KSC to the low orbit, then meet the craft.</summary>
    Launch,
    /// <summary>At a craft (or placed location) around the same body: leave it to meet the next one.</summary>
    Craft,
    /// <summary>Anywhere else: the map's steps reach the target body's low orbit, and the rendezvous starts there.</summary>
    Parking,
}

/// <summary>Where a rendezvous added to the mission would start, worked out for the body of the craft to meet.</summary>
/// <param name="After">"stop 2: Kerbin — Low orbit (80 km)", the stop it's added after.</param>
/// <param name="ParkingAltitudeM">The low orbit a launch climbs to, or the mission arrives in.</param>
/// <param name="Chaser">The craft it leaves from (<see cref="RendezvousStartKind.Craft"/> only).</param>
/// <param name="EarliestUt">When the mission gets there, if a leg tool knows it.</param>
public sealed record RendezvousStart(string After, RendezvousStartKind Kind, string BodyName, double ParkingAltitudeM,
    CustomLocation? Chaser, double? EarliestUt)
{
    public string Text => Kind switch
    {
        RendezvousStartKind.Launch => $"After {After}: launch from the KSC to {ParkingAltitudeM / 1000:0} km",
        RendezvousStartKind.Craft => $"After {After}: leave {Chaser!.Name}",
        _ => $"After {After}: from {BodyName}'s {ParkingAltitudeM / 1000:0} km orbit, where the mission gets to",
    };
}

/// <summary>A rendezvous leg to add: the craft met and the plan made for it on the Rendezvous tab.</summary>
/// <param name="Target">The craft: a location on the Locations tab (kept as it is), or a new place for the mission (Id 0).</param>
public sealed record RendezvousStopToAdd(CustomLocation Target, RendezvousLeg Plan);

public sealed partial class MissionBuilderViewModel
{
    /// <summary>The Δv map as stored (with the Locations tab's custom locations), before the mission's own places.</summary>
    private DeltaVMap? _baseMap;

    /// <summary>The mission's own places (crafts met, constellation orbits), by id; kept with the mission.</summary>
    private readonly Dictionary<long, CustomLocation> _places = [];

    private DeltaVMap? MapWithPlaces() =>
        _baseMap is null ? null : CustomLocations.WithPlaces(_baseMap, _places.Values, _bodies);

    private bool IsPlaceKey(string key) => _places.Values.Any(p => p.Key == key);

    /// <summary>A custom location by id: one of the mission's own places, or one on the Locations tab.</summary>
    private CustomLocation? Location(long id) => _places.TryGetValue(id, out var place) ? place : _locations.Get(id);

    /// <summary>The mission's places, replacing the last mission's (when one is opened).</summary>
    private void SetPlaces(IEnumerable<CustomLocation> places)
    {
        _places.Clear();
        foreach (var place in places.Where(p => p.IsMissionPlace)) _places[place.Id] = place;
        _map = MapWithPlaces();
        SyncNodes();
        OnPropertyChanged(nameof(Map));
    }

    /// <summary>The places a stop is at, to save with the mission; null when there are none.</summary>
    private IReadOnlyList<CustomLocation>? UsedPlaces()
    {
        var keys = Stops.Select(s => s.NodeKey).ToHashSet();
        var used = _places.Values.Where(p => keys.Contains(p.Key)).OrderBy(p => p.Id).ToList();
        return used.Count == 0 ? null : used;
    }

    /// <summary>
    /// Keeps <paramref name="place"/> with the mission and returns it with its id. A craft from the save that the
    /// mission already has (same save and vessel) is the same place, updated to where the craft is now.
    /// </summary>
    private CustomLocation AddPlace(CustomLocation place)
    {
        var existing = place.IsLinked
            ? _places.Values.FirstOrDefault(p => p.KspSave == place.KspSave && p.KspVesselId == place.KspVesselId)
            : null;
        var id = existing?.Id ?? (_places.Count == 0 ? CustomLocation.MissionPlaceIdBase + 1 : _places.Keys.Max() + 1);
        var kept = place with { Id = id };
        _places[id] = kept;
        _map = MapWithPlaces();
        SyncNodes();
        OnPropertyChanged(nameof(Map));
        return kept;
    }

    // ---------------- Adding legs from the calculators ----------------

    public RelayCommand AddRendezvousCommand { get; private set; } = null!;
    public RelayCommand AddTransferCommand { get; private set; } = null!;
    public RelayCommand AddDeployCommand { get; private set; } = null!;

    /// <summary>The Rendezvous tab should start planning a leg to add (its start: where the mission is).</summary>
    public event Action? AddRendezvousRequested;

    /// <summary>The Transfer Planner should start choosing a window for a leg to add (from the planet the mission is at).</summary>
    public event Action? AddTransferRequested;

    /// <summary>Resonant Orbits should start planning a deployment to add (around the body the mission is at).</summary>
    public event Action? AddDeployRequested;

    private void InitAdding()
    {
        AddRendezvousCommand = new RelayCommand(() => AddRendezvousRequested?.Invoke(), () => _map is not null && Stops.Count >= 2);
        AddTransferCommand = new RelayCommand(() => AddTransferRequested?.Invoke(), () => _map is not null && Stops.Count >= 2);
        AddDeployCommand = new RelayCommand(() => AddDeployRequested?.Invoke(), () => _map is not null && Stops.Count >= 2);
    }

    /// <summary>The body the mission is at (or around) before its last stop.</summary>
    public string? AddAfterBody => _map is null || Stops.Count < 2 ? null : _map[Stops[AddAfterIndex].NodeKey].BodyName;

    /// <summary>
    /// The planet a transfer added before the last stop leaves from: the body the mission is at, or the planet its moon
    /// orbits (the Δv map climbs out to it first). Null around the star.
    /// </summary>
    public string? TransferOrigin
    {
        get
        {
            if (AddAfterBody is not { } name || !_bodies.TryGetValue(name, out var body) || body.ParentId is not { } parentId) return null;
            var parent = _bodies.Values.FirstOrDefault(b => b.Id == parentId);
            return parent?.ParentId is null ? body.Name : parent.Name;
        }
    }

    /// <summary>
    /// Adds a stop at <paramref name="destination"/> before the last stop, the leg flown in <paramref name="window"/>: its
    /// orbit at <paramref name="arrivalAltitudeM"/> (a map orbit if there's one at that height, else the mission's own),
    /// or its intercept for a flyby. The Δv map supplies the steps on either side. Returns the new stop.
    /// </summary>
    public StopViewModel? AddTransferStop(CelestialBody destination, bool flyby, double arrivalAltitudeM, TransferLeg window)
    {
        if (_map is null) return null;
        var key = flyby
            ? _map.Nodes.FirstOrDefault(n => n.BodyName == destination.Name && n.Kind == "Intercept" && !n.IsCustom)?.Key
            : OrbitStopKey(destination.Name, arrivalAltitudeM);
        return key is null ? null : InsertStop(new MissionStop(key, Transfer: window));
    }

    /// <summary>Adds a stop in a circular orbit of <paramref name="body"/> before the last stop, releasing a constellation there.</summary>
    public StopViewModel? AddDeployStop(CelestialBody body, double altitudeM, DeployStep step) =>
        OrbitStopKey(body.Name, altitudeM) is { } key ? InsertStop(new MissionStop(key, Deploy: step)) : null;

    /// <summary>
    /// A stop key for a circular orbit: the map's low or synchronous orbit at that height (within a kilometre), a place
    /// the mission already has, or a new place for it. Null if the orbit can't be flown (inside the atmosphere, say).
    /// </summary>
    private string? OrbitStopKey(string bodyName, double altitudeM)
    {
        if (_map is null) return null;
        if (_map.Nodes.FirstOrDefault(n => n.BodyName == bodyName && n.Kind is "LowOrbit" or "Synchronous" && !n.IsCustom
                && n.AltitudeM is { } a && Math.Abs(a - altitudeM) < 1_000) is { } node)
            return node.Key;
        var place = _places.Values.FirstOrDefault(p => p.BodyName == bodyName && !p.IsPlaced && p.IsCircular
                        && Math.Abs(p.PeriapsisAltitudeM - altitudeM) < 1)
            ?? AddPlace(CustomLocation.Circular(0, "Orbit", bodyName, altitudeM));
        return _map.TryGet(place.Key, out _) ? place.Key : null;
    }

    private StopViewModel InsertStop(MissionStop definition)
    {
        var stop = new StopViewModel(definition, StopChanged, RefreshLegs);
        Stops.Insert(Math.Max(1, Stops.Count - 1), stop);
        StopChanged();
        return stop;
    }

    /// <summary>Index of the stop new legs leave from: the one before the last stop (new stops go before the last).</summary>
    private int AddAfterIndex => Math.Max(0, Stops.Count - 2);

    /// <summary>"stop 2: Kerbin — Low orbit (80 km)", the stop new legs are added after.</summary>
    public string AddAfterText => _map is null || Stops.Count < 2 ? ""
        : $"stop {AddAfterIndex + 1}: {_map[Stops[AddAfterIndex].NodeKey].DisplayName}";

    /// <summary>
    /// Where a rendezvous with a craft around <paramref name="bodyName"/> starts if added before the last stop, by the
    /// same rule as <see cref="LegTools.FindRendezvous"/>. Null around the star or where the map has no low orbit.
    /// </summary>
    public RendezvousStart? RendezvousStartFor(string bodyName)
    {
        // Around the star, a craft is an interplanetary destination (see CustomLocations), not one to meet in orbit.
        if (_map is null || Stops.Count < 2 || _bodies.GetValueOrDefault(bodyName)?.ParentId is null) return null;
        var lowOrbit = _map.Nodes.FirstOrDefault(n => n.BodyName == bodyName && n.Kind == "LowOrbit" && !n.IsCustom);
        if (lowOrbit?.AltitudeM is not { } parking) return null;
        var index = AddAfterIndex;
        var from = _map[Stops[index].NodeKey];
        var after = AddAfterText;
        var earliest = ArrivalAt(index);
        if (from.BodyName == bodyName && from.CustomLocationId is { } id && Location(id) is { IsPlaced: true } craft)
            return new RendezvousStart(after, RendezvousStartKind.Craft, bodyName, parking, craft, earliest);
        if (from.BodyName == bodyName && from.Kind == "Surface" && bodyName == LaunchSite.Ksc.BodyName)
            return new RendezvousStart(after, RendezvousStartKind.Launch, bodyName, parking, null, earliest);
        return new RendezvousStart(after, RendezvousStartKind.Parking, bodyName, parking, null, earliest);
    }

    /// <summary>
    /// Adds a stop at each craft, in order, before the last stop, each leg flown as the rendezvous planned. Crafts not on
    /// the Locations tab become the mission's own places. Returns the first new stop.
    /// </summary>
    public StopViewModel? AddRendezvousStops(IReadOnlyList<RendezvousStopToAdd> legs)
    {
        if (_map is null || legs.Count == 0) return null;
        var insertAt = Math.Max(1, Stops.Count - 1);
        var added = new List<StopViewModel>();
        foreach (var (target, plan) in legs)
        {
            var place = target.Id > 0 && _locations.Get(target.Id) is not null ? target : AddPlace(target);
            if (!_map.TryGet(place.Key, out _)) continue; // couldn't be placed on the map
            added.Add(new StopViewModel(new MissionStop(place.Key, Rendezvous: plan with { TargetLocationId = place.Id }), StopChanged, RefreshLegs));
        }
        if (added.Count == 0) return null;
        _suspend = true;
        foreach (var stop in added) Stops.Insert(insertAt++, stop);
        _suspend = false;
        StopChanged();
        return added[0];
    }
}
