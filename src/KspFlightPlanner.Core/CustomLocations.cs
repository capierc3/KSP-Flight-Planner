using System.Globalization;
using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core;

/// <summary>A user-defined place, e.g. "Station Alpha" in a 150 km circular orbit of Kerbin.</summary>
public sealed record CustomLocation(long Id, string Name, string BodyName, double AltitudeM, string? Note = null)
{
    public string Key => $"custom.{Id}";
}

/// <summary>
/// Attaches custom circular orbits to the Δv map.
/// <list type="bullet">
/// <item>Around a planet or moon: child of that body's low-orbit node; the edge is a Hohmann transfer (docs/MATH.md §4).</item>
/// <item>Around the star: an interplanetary destination, so a child of the home planet's escape node (next to the
/// Duna/Eve/Moho intercepts); the edge is the extra ejection Δv plus the circularization burn (docs/MATH.md §4b).</item>
/// </list>
/// </summary>
public static class CustomLocations
{
    /// <summary>Picks the attachment point for a location and builds its node. Throws ArgumentException if it can't be placed.</summary>
    public static DvMapNode Place(CustomLocation location, DeltaVMap map, IReadOnlyDictionary<string, CelestialBody> bodies)
    {
        if (!bodies.TryGetValue(location.BodyName, out var body))
            throw new ArgumentException($"Unknown body '{location.BodyName}'.");

        // The map's root is the home planet's surface (Kerbin). Orbiting that planet's star means an interplanetary trip.
        var homeName = map.Root.BodyName;
        if (body.Name != homeName && bodies.TryGetValue(homeName, out var home) && home.ParentId == body.Id)
        {
            var escape = map.Nodes.FirstOrDefault(n => n.BodyName == homeName && n.Kind == "Elliptical" && !n.IsCustom)
                ?? throw new ArgumentException($"The map has no escape node for {homeName}.");
            var homeLowOrbit = map.ParentOf(escape)!;
            return CreateStarOrbitNode(location, escape, homeLowOrbit, body, home);
        }

        var lowOrbit = map.Nodes.FirstOrDefault(n => n.BodyName == body.Name && n.Kind == "LowOrbit")
            ?? throw new ArgumentException($"{body.Name} has no low orbit on the map.");
        return CreateNode(location, lowOrbit, body);
    }

    /// <summary>Circular orbit around a planet or moon, reached by Hohmann transfer from the map's low orbit.</summary>
    public static DvMapNode CreateNode(CustomLocation location, DvMapNode lowOrbit, CelestialBody body)
    {
        if (lowOrbit.AltitudeM is not { } loAltitude)
            throw new ArgumentException($"{lowOrbit.DisplayName} has no altitude on the map.");
        Validate(location, body);

        var rLow = body.EquatorialRadiusM + loAltitude;
        var rCustom = body.EquatorialRadiusM + location.AltitudeM;
        var transfer = OrbitMath.Hohmann(body.Mu, rLow, rCustom);

        // Heading back down toward the surface, the atmosphere can replace the circularization burn:
        // only the deorbit burn (the transfer's burn at the custom orbit) is still needed.
        var deorbitBurn = rCustom > rLow ? transfer.Dv2 : transfer.Dv1;

        return Node(location, lowOrbit, body, transfer.Total, deorbitBurn, TimeText(transfer.TransferTimeS));
    }

    /// <summary>
    /// Circular orbit around the star, reached from the home planet's escape node:
    /// heliocentric Hohmann from the home planet's orbit gives the departure v∞ and the arrival circularization;
    /// the ejection from low orbit (Oberth effect included) minus the map's low-orbit→escape edge is the extra
    /// burn charged here.
    /// </summary>
    public static DvMapNode CreateStarOrbitNode(CustomLocation location, DvMapNode escape, DvMapNode homeLowOrbit,
        CelestialBody star, CelestialBody home)
    {
        Validate(location, star);
        if (home.Orbit is null || homeLowOrbit.AltitudeM is not { } parkingAltitude)
            throw new ArgumentException($"{home.Name} needs orbit data to plan a trip around {star.Name}.");

        var rHome = home.Orbit.SemiMajorAxisM;
        var rTarget = star.EquatorialRadiusM + location.AltitudeM;
        var a = (rHome + rTarget) / 2;

        var vInf = Math.Abs(OrbitMath.VisViva(star.Mu, rHome, a) - OrbitMath.CircularVelocity(star.Mu, rHome));
        var circularize = Math.Abs(OrbitMath.CircularVelocity(star.Mu, rTarget) - OrbitMath.VisViva(star.Mu, rTarget, a));
        var ejection = OrbitMath.HyperbolicBurnDv(home.Mu, home.EquatorialRadiusM + parkingAltitude, vInf);
        var beyondEscape = Math.Max(0, ejection - escape.MapDvFromParent);
        var transferTime = Math.PI * Math.Sqrt(a * a * a / star.Mu);

        // Returning, the home planet's atmosphere can capture you (as for every interplanetary return on the map),
        // so only the burn to leave the custom orbit remains.
        return Node(location, escape, star, beyondEscape + circularize, circularize,
            $"{transferTime / 21600:0} days", aerobrakeUp: home.HasAtmosphere);
    }

    private static DvMapNode Node(CustomLocation location, DvMapNode parent, CelestialBody body, double dv,
        double aerobrakeResidual, string flightTime, bool? aerobrakeUp = null) => new()
    {
        Id = -location.Id, // negative: never collides with seeded node ids
        Key = location.Key,
        ParentId = parent.Id,
        BodyName = body.Name,
        Kind = "Custom",
        Label = $"{location.Name} ({FormatKm(location.AltitudeM)})",
        DvFromParent = Math.Round(dv),
        MapDvFromParent = Math.Round(dv),
        AerobrakeUp = aerobrakeUp ?? body.HasAtmosphere,
        AerobrakeResidualDv = Math.Round(aerobrakeResidual),
        AltitudeM = location.AltitudeM,
        CustomLocationId = location.Id,
        FlightTime = flightTime,
    };

    private static void Validate(CustomLocation location, CelestialBody body)
    {
        if (location.AltitudeM <= body.AtmosphereHeightM)
            throw new ArgumentException($"{location.Name} is inside {body.Name}'s atmosphere ({body.AtmosphereHeightM / 1000:N0} km).");
        if (location.AltitudeM <= 0 || location.AltitudeM + body.EquatorialRadiusM >= body.SoiRadiusM)
            throw new ArgumentException($"{location.Name} must be above the surface and inside {body.Name}'s sphere of influence.");
    }

    private static string FormatKm(double metres) =>
        (metres / 1000).ToString(metres >= 10_000_000 ? "N0" : "0.#", CultureInfo.CurrentCulture) + " km";

    private static string TimeText(double seconds) =>
        seconds >= 3600 ? $"{seconds / 3600:0.#} hours" : $"{seconds / 60:0} minutes";
}
