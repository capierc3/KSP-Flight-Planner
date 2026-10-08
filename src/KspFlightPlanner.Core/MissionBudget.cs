using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core;

public sealed record BudgetLine(string Stage, double? DeltaV, string? Note = null, bool IsHeader = false);

/// <summary>Mission Δv: launch, transfer, capture and landing, optionally followed by the trip home.</summary>
public static class MissionBudget
{
    /// <param name="returnLeg">Return request and window, or null for a one-way trip.</param>
    public static IReadOnlyList<BudgetLine> Build(
        TransferRequest outbound, TransferWindow outboundWindow, bool includeLanding,
        (TransferRequest Request, TransferWindow Window)? returnLeg = null)
    {
        var origin = outbound.Origin;
        var dest = outbound.Destination;
        // Custom locations have no surface: no launch from them, no landing on them.
        var landing = includeLanding && outbound.Arrival != ArrivalMode.Flyby && !dest.IsCustomLocation;
        var lines = new List<BudgetLine>();

        if (returnLeg is not null) lines.Add(new BudgetLine($"Outbound: {origin.Name} → {dest.Name}", null, IsHeader: true));
        if (!origin.IsCustomLocation)
            lines.Add(new($"Launch: {origin.Name} surface → {outbound.ParkingAltitudeM / 1000:0} km orbit", origin.SurfaceToLowOrbitDv,
                origin.SurfaceToLowOrbitDv is null ? "No estimate for this body" : "Community Δv-map estimate; depends on your ascent"));
        AddTransfer(lines, outbound, outboundWindow, "Transfer ejection burn");

        if (landing)
        {
            lines.Add(new BudgetLine($"Landing on {dest.Name}", dest.SurfaceToLowOrbitDv,
                dest.SurfaceToLowOrbitDv is null ? "No estimate for this body"
                : dest.HasAtmosphere ? "Upper bound (≈ ascent Δv); parachutes and drag reduce this a lot"
                : "≈ ascent Δv (no atmosphere)"));
        }

        if (returnLeg is { } ret)
        {
            lines.Add(new BudgetLine($"Return: {dest.Name} → {origin.Name}", null, IsHeader: true));
            if (landing)
            {
                lines.Add(new BudgetLine($"Launch: {dest.Name} surface → {ret.Request.ParkingAltitudeM / 1000:0} km orbit",
                    dest.SurfaceToLowOrbitDv,
                    dest.SurfaceToLowOrbitDv is null ? "No estimate for this body" : "Community Δv-map estimate"));
            }
            AddTransfer(lines, ret.Request, ret.Window, "Return ejection burn");
        }

        return lines;
    }

    private static void AddTransfer(List<BudgetLine> lines, TransferRequest request, TransferWindow window, string ejectionLabel)
    {
        var origin = request.Origin;
        var dest = request.Destination;

        lines.Add(new(origin.IsCustomLocation ? $"Departure burn from {origin.Name}" : ejectionLabel, window.EjectionDv,
            Math.Abs(window.Ejection.InclinationDeg) > 0.5
                ? $"Escape direction is {window.Ejection.InclinationDeg:0.0}° out of {origin.Name}'s orbital plane"
                : null));
        lines.Add(new("Mid-course correction (allowance)", Math.Round(window.TotalDv * 0.02 + 20),
            "Rule-of-thumb margin for plane/timing fixes"));

        lines.Add(request.Arrival switch
        {
            ArrivalMode.Circularize when dest.IsCustomLocation => new BudgetLine($"Match orbit with {dest.Name} (rendezvous)", window.CaptureDv,
                "No gravity well to help: the whole speed difference is burned directly"),
            ArrivalMode.Circularize => new BudgetLine($"Capture into {request.ArrivalAltitudeM / 1000:0} km {dest.Name} orbit", window.CaptureDv),
            ArrivalMode.Aerobrake => new BudgetLine($"Capture at {dest.Name} by aerobraking", 0,
                dest.HasAtmosphere
                    ? $"Atmosphere entry at ≈ {EntrySpeed(dest, window.ArrivalVInf):N0} m/s; heat shield recommended"
                    : $"{dest.Name} has no atmosphere — aerobraking is not possible"),
            _ => new BudgetLine($"Flyby of {dest.Name}", 0, "No capture"),
        });
    }

    /// <summary>Speed when reaching the top of the atmosphere on a hyperbolic approach: √(v∞² + 2μ/r).</summary>
    public static double EntrySpeed(CelestialBody body, double vInf) =>
        Math.Sqrt(vInf * vInf + 2 * body.Mu / (body.EquatorialRadiusM + body.AtmosphereHeightM));

    public static double Total(IEnumerable<BudgetLine> lines) => lines.Sum(l => l.DeltaV ?? 0);
}
