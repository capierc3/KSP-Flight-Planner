using System.Globalization;
using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core;

/// <summary>
/// A user-defined place, e.g. "Station Alpha" in a 150 km circular orbit of Kerbin.
/// Only independent values are stored (periapsis, apoapsis, inclination); everything else is derived.
/// </summary>
public sealed record CustomLocation(
    long Id,
    string Name,
    string BodyName,
    double PeriapsisAltitudeM,
    double ApoapsisAltitudeM,
    double InclinationDeg = 0,
    string? Description = null)
{
    public string Key => $"custom.{Id}";
    public bool IsCircular => Math.Abs(ApoapsisAltitudeM - PeriapsisAltitudeM) < 1;

    public static CustomLocation Circular(long id, string name, string bodyName, double altitudeM, string? description = null) =>
        new(id, name, bodyName, altitudeM, altitudeM, 0, description);
}

/// <summary>Everything derived for a custom location: where it attaches to the map and what it costs.</summary>
public sealed record CustomLocationAnalysis(
    DvMapNode Node,
    DvMapNode AttachedTo,
    OrbitShape Orbit,
    bool IsInterplanetary,
    double DepartureDv,
    double ArrivalDv,
    double PlaneChangeDv,
    double TransferTimeS)
{
    /// <summary>Interplanetary only: the v∞ to leave the home planet with.</summary>
    public double? DepartureVInf { get; init; }

    /// <summary>Interplanetary only: total ejection burn from the home planet's low orbit.</summary>
    public double? EjectionFromLowOrbit { get; init; }
}

/// <summary>
/// Attaches custom orbits to the Δv map.
/// <list type="bullet">
/// <item>Around a planet or moon: child of that body's low-orbit node; the edge is a two-burn transfer from
/// the low orbit (a Hohmann transfer when circular) — docs/MATH.md §4, §4c.</item>
/// <item>Around the star: an interplanetary destination, so a child of the home planet's escape node (next to the
/// Duna/Eve/Moho intercepts); the edge is the extra ejection Δv plus the arrival burn — docs/MATH.md §4b.</item>
/// </list>
/// Inclination is charged as a worst-case plane change at the target's apoapsis (docs/MATH.md §14); the
/// Mission Builder adds it when "Include worst-case plane changes" is on.
/// </summary>
public static class CustomLocations
{
    /// <summary>Picks the attachment point for a location and builds its node. Throws ArgumentException if it can't be placed.</summary>
    public static DvMapNode Place(CustomLocation location, DeltaVMap map, IReadOnlyDictionary<string, CelestialBody> bodies) =>
        Analyze(location, map, bodies).Node;

    /// <summary>Full breakdown for the editor. Throws ArgumentException (with a user-facing message) if invalid.</summary>
    public static CustomLocationAnalysis Analyze(CustomLocation location, DeltaVMap map, IReadOnlyDictionary<string, CelestialBody> bodies)
    {
        if (!bodies.TryGetValue(location.BodyName, out var body))
            throw new ArgumentException($"Unknown body '{location.BodyName}'.");
        Validate(location, body);

        // The map's root is the home planet's surface (Kerbin). Orbiting that planet's star means an interplanetary trip.
        var homeName = map.Root.BodyName;
        if (body.Name != homeName && bodies.TryGetValue(homeName, out var home) && home.ParentId == body.Id)
        {
            var escape = map.Nodes.FirstOrDefault(n => n.BodyName == homeName && n.Kind == "Elliptical" && !n.IsCustom)
                ?? throw new ArgumentException($"The map has no escape node for {homeName}.");
            return AnalyzeStarOrbit(location, escape, map.ParentOf(escape)!, body, home);
        }

        var lowOrbit = map.Nodes.FirstOrDefault(n => n.BodyName == body.Name && n.Kind == "LowOrbit" && !n.IsCustom)
            ?? throw new ArgumentException($"{body.Name} has no low orbit on the map.");
        return AnalyzeBodyOrbit(location, lowOrbit, body);
    }

    private static CustomLocationAnalysis AnalyzeBodyOrbit(CustomLocation location, DvMapNode lowOrbit, CelestialBody body)
    {
        if (lowOrbit.AltitudeM is not { } loAltitude)
            throw new ArgumentException($"{lowOrbit.DisplayName} has no altitude on the map.");

        var orbit = Shape(location, body);
        var transfer = OrbitMath.TransferToEllipse(body.Mu, body.EquatorialRadiusM + loAltitude, orbit.PeriapsisRadius, orbit.ApoapsisRadius);
        var plane = OrbitMath.PlaneChangeDv(orbit.ApoapsisSpeed, location.InclinationDeg);

        // Heading back down toward the surface, the atmosphere can replace the burn at the low orbit:
        // only the burn at the custom orbit (lowering the far apsis toward the atmosphere) is still needed.
        var node = Node(location, lowOrbit, body, orbit, transfer.Total, transfer.ArrivalDv, plane, TimeText(transfer.TransferTimeS),
            aerobrakeUp: body.HasAtmosphere);
        return new CustomLocationAnalysis(node, lowOrbit, orbit, false, transfer.DepartureDv, transfer.ArrivalDv, plane, transfer.TransferTimeS);
    }

    /// <summary>
    /// Orbit around the star, reached from the home planet's escape node: a transfer around the star from the home
    /// planet's orbit gives the departure v∞ and the arrival burn; the ejection from low orbit (Oberth effect included)
    /// minus the map's low-orbit→escape edge is the extra burn charged on top of that edge.
    /// </summary>
    private static CustomLocationAnalysis AnalyzeStarOrbit(CustomLocation location, DvMapNode escape, DvMapNode homeLowOrbit,
        CelestialBody star, CelestialBody home)
    {
        if (home.Orbit is null || homeLowOrbit.AltitudeM is not { } parkingAltitude)
            throw new ArgumentException($"{home.Name} needs orbit data to plan a trip around {star.Name}.");

        var orbit = Shape(location, star);
        var transfer = OrbitMath.TransferToEllipse(star.Mu, home.Orbit.SemiMajorAxisM, orbit.PeriapsisRadius, orbit.ApoapsisRadius);
        var vInf = transfer.DepartureDv;
        var ejection = OrbitMath.HyperbolicBurnDv(home.Mu, home.EquatorialRadiusM + parkingAltitude, vInf);
        var beyondEscape = Math.Max(0, ejection - escape.MapDvFromParent);
        var plane = OrbitMath.PlaneChangeDv(orbit.ApoapsisSpeed, location.InclinationDeg - home.Orbit.InclinationDeg);

        // Returning, the home planet's atmosphere can capture you (as for every interplanetary return on the map),
        // so only the burn to leave the custom orbit remains.
        var node = Node(location, escape, star, orbit, beyondEscape + transfer.ArrivalDv, transfer.ArrivalDv, plane,
            $"{transfer.TransferTimeS / 21600:0} days", aerobrakeUp: home.HasAtmosphere);
        return new CustomLocationAnalysis(node, escape, orbit, true, beyondEscape, transfer.ArrivalDv, plane, transfer.TransferTimeS)
        {
            DepartureVInf = vInf,
            EjectionFromLowOrbit = ejection,
        };
    }

    private static OrbitShape Shape(CustomLocation location, CelestialBody body) =>
        new(body.Mu, body.EquatorialRadiusM + location.PeriapsisAltitudeM, body.EquatorialRadiusM + location.ApoapsisAltitudeM);

    private static DvMapNode Node(CustomLocation location, DvMapNode parent, CelestialBody body, OrbitShape orbit, double dv,
        double aerobrakeResidual, double planeChange, string flightTime, bool aerobrakeUp) => new()
    {
        Id = -location.Id, // negative: never collides with seeded node ids
        Key = location.Key,
        ParentId = parent.Id,
        BodyName = body.Name,
        Kind = "Custom",
        Label = $"{location.Name} ({AltitudeText(location)})",
        DvFromParent = Math.Round(dv),
        MapDvFromParent = Math.Round(dv),
        PlaneChangeDv = Math.Round(planeChange),
        AerobrakeUp = aerobrakeUp,
        AerobrakeResidualDv = Math.Round(aerobrakeResidual),
        AltitudeM = orbit.SemiMajorAxis - body.EquatorialRadiusM,
        CustomLocationId = location.Id,
        CustomName = location.Name,
        CustomOrbitText = AltitudeText(location),
        FlightTime = flightTime,
    };

    /// <summary>Checks the independent values; derived values can't conflict because they are never stored.</summary>
    public static void Validate(CustomLocation location, CelestialBody body)
    {
        if (string.IsNullOrWhiteSpace(location.Name))
            throw new ArgumentException("Give the location a name.");
        if (location.ApoapsisAltitudeM < location.PeriapsisAltitudeM)
            throw new ArgumentException("Apoapsis must be at least as high as periapsis.");
        if (location.PeriapsisAltitudeM <= body.AtmosphereHeightM)
            throw new ArgumentException(body.HasAtmosphere
                ? $"Periapsis is inside {body.Name}'s atmosphere ({body.AtmosphereHeightM / 1000:N0} km) — the orbit would decay."
                : $"Periapsis must be above {body.Name}'s surface.");
        if (body.HighestElevationM is { } peak && location.PeriapsisAltitudeM <= peak)
            throw new ArgumentException($"Periapsis is below {body.Name}'s highest terrain ({peak / 1000:N1} km).");
        if (location.ApoapsisAltitudeM + body.EquatorialRadiusM >= body.SoiRadiusM)
            throw new ArgumentException($"Apoapsis is outside {body.Name}'s sphere of influence ({(body.SoiRadiusM - body.EquatorialRadiusM) / 1000:N0} km).");
        if (location.InclinationDeg is < 0 or > 180)
            throw new ArgumentException("Inclination must be between 0° and 180°.");
    }

    public static string AltitudeText(CustomLocation location) =>
        location.IsCircular ? FormatKm(location.PeriapsisAltitudeM)
        : $"{FormatKm(location.PeriapsisAltitudeM, unit: false)} × {FormatKm(location.ApoapsisAltitudeM)}";

    private static string FormatKm(double metres, bool unit = true) =>
        (metres / 1000).ToString(metres >= 10_000_000 ? "N0" : "#,0.#", CultureInfo.CurrentCulture) + (unit ? " km" : "");

    private static string TimeText(double seconds) =>
        seconds >= 3600 ? $"{seconds / 3600:0.#} hours" : $"{seconds / 60:0} minutes";
}
