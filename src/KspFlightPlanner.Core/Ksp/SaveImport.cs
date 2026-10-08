using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core.Ksp;

/// <summary>A vessel from a save turned into a placed custom location, or the reason it can't be.</summary>
public sealed record VesselImport(SavedVessel Vessel, CustomLocation? Location, string? Problem)
{
    public bool CanImport => Location is not null;
}

/// <summary>
/// Turns vessels in a KSP save into placed custom locations (docs/MATH.md §8c). KSP stores the same Keplerian
/// elements custom locations use, so the conversion is: periapsis/apoapsis altitude = a(1 ∓ e) − R, mean anomaly
/// radians → degrees, angles wrapped to 0–360°, and the epoch is the save's EPH.
/// </summary>
public static class SaveImport
{
    private const double RadToDeg = 180 / Math.PI;

    public static VesselImport Convert(SavedVessel vessel, string saveName, IReadOnlyDictionary<string, CelestialBody> bodies)
    {
        VesselImport No(string problem) => new(vessel, null, problem);

        if (!vessel.IsOrbiting || vessel.Orbit is not { } o)
            return No(vessel.Situation switch
            {
                "LANDED" or "SPLASHED" or "PRELAUNCH" => "On the surface — only orbits can be imported",
                "SUB_ORBITAL" or "FLYING" => "Sub-orbital — not in a stable orbit",
                "ESCAPING" => "Escaping — leaving the sphere of influence",
                "DOCKED" => "Docked to another vessel",
                _ => $"Not in orbit ({vessel.Situation.ToLowerInvariant()})",
            });
        if (!double.IsFinite(o.SemiMajorAxisM) || o.SemiMajorAxisM <= 0 || o.Eccentricity is < 0 or >= 1)
            return No("Not a closed orbit");
        if (o.BodyName is not { } bodyName || !bodies.TryGetValue(bodyName, out var body))
            return No($"Orbits {o.BodyName ?? o.BodyIdent ?? "an unknown body"}, which isn't in the stock system");

        var location = new CustomLocation(
            Id: 0,
            Name: vessel.Name,
            BodyName: body.Name,
            PeriapsisAltitudeM: o.SemiMajorAxisM * (1 - o.Eccentricity) - body.EquatorialRadiusM,
            ApoapsisAltitudeM: o.SemiMajorAxisM * (1 + o.Eccentricity) - body.EquatorialRadiusM,
            InclinationDeg: o.InclinationDeg,
            Description: $"Imported from the KSP save \"{saveName}\" ({TypeText(vessel.Type)}).",
            IsPlaced: true,
            Mode: PositionMode.Elements,
            LanDeg: Wrap(o.LanDeg),
            ArgumentOfPeriapsisDeg: Wrap(o.ArgumentOfPeriapsisDeg),
            MeanAnomalyDeg: Wrap(o.MeanAnomalyRad * RadToDeg),
            EpochUt: Math.Max(0, o.EpochUt))
        {
            KspSave = saveName,
            KspVesselId = vessel.Id,
        };

        try
        {
            CustomLocations.Validate(location, body);
        }
        catch (ArgumentException ex)
        {
            return No(ex.Message);
        }
        return new VesselImport(vessel, location, null);
    }

    /// <summary>
    /// The linked location with its orbit and position replaced by the vessel's current ones. The name,
    /// description and images stay the user's.
    /// </summary>
    public static CustomLocation Refresh(CustomLocation linked, CustomLocation fromSave) => linked with
    {
        BodyName = fromSave.BodyName,
        PeriapsisAltitudeM = fromSave.PeriapsisAltitudeM,
        ApoapsisAltitudeM = fromSave.ApoapsisAltitudeM,
        InclinationDeg = fromSave.InclinationDeg,
        IsPlaced = true,
        Mode = PositionMode.Elements,
        LanDeg = fromSave.LanDeg,
        ArgumentOfPeriapsisDeg = fromSave.ArgumentOfPeriapsisDeg,
        MeanAnomalyDeg = fromSave.MeanAnomalyDeg,
        EpochUt = fromSave.EpochUt,
    };

    /// <summary>Whether a vessel of this type is pre-ticked for import (not debris, asteroids or flags).</summary>
    public static bool IsUsuallyWanted(SavedVessel vessel) =>
        vessel.Type is not ("Debris" or "SpaceObject" or "Flag" or "Unknown" or "DeployedSciencePart"
            or "DeployedScienceController" or "DeployedGroundPart");

    public static string TypeText(string type) => type switch
    {
        "SpaceObject" => "asteroid / comet",
        "EVA" => "kerbal on EVA",
        "DeployedSciencePart" or "DeployedScienceController" or "DeployedGroundPart" => "surface experiment",
        _ => type.ToLowerInvariant(),
    };

    private static double Wrap(double degrees) => degrees is >= 0 and < 360 ? degrees : (degrees % 360 + 360) % 360;
}
