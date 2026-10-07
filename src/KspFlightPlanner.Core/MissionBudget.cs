using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core;

public sealed record BudgetLine(string Stage, double? DeltaV, string? Note = null);

/// <summary>One-way mission Δv: launch, transfer, capture and landing.</summary>
public static class MissionBudget
{
    public static IReadOnlyList<BudgetLine> Build(TransferRequest request, TransferWindow window, bool includeLanding)
    {
        var origin = request.Origin;
        var dest = request.Destination;
        var lines = new List<BudgetLine>
        {
            new($"Launch: {origin.Name} surface → {request.ParkingAltitudeM / 1000:0} km orbit", origin.SurfaceToLowOrbitDv,
                origin.SurfaceToLowOrbitDv is null ? "No estimate for this body" : "Community Δv-map estimate; depends on your ascent"),
            new($"Transfer ejection burn", window.EjectionDv,
                Math.Abs(window.Ejection.InclinationDeg) > 0.5
                    ? $"Escape direction is {window.Ejection.InclinationDeg:0.0}° out of {origin.Name}'s orbital plane"
                    : null),
            new("Mid-course correction (allowance)", Math.Round(window.TotalDv * 0.02 + 20), "Rule-of-thumb margin for plane/timing fixes"),
        };

        lines.Add(request.Arrival switch
        {
            ArrivalMode.Circularize => new BudgetLine($"Capture into {request.ArrivalAltitudeM / 1000:0} km {dest.Name} orbit", window.CaptureDv),
            ArrivalMode.Aerobrake => new BudgetLine($"Capture at {dest.Name} by aerobraking", 0,
                dest.HasAtmosphere ? "Set periapsis inside the atmosphere; small correction burns not included"
                                   : $"{dest.Name} has no atmosphere — aerobraking is not possible"),
            _ => new BudgetLine($"Flyby of {dest.Name}", 0, "No capture"),
        });

        if (includeLanding && request.Arrival != ArrivalMode.Flyby)
        {
            lines.Add(new BudgetLine($"Landing on {dest.Name}", dest.SurfaceToLowOrbitDv,
                dest.SurfaceToLowOrbitDv is null ? "No estimate for this body"
                : dest.HasAtmosphere ? "Upper bound (≈ ascent Δv); parachutes and drag reduce this a lot"
                : "≈ ascent Δv (no atmosphere)"));
        }

        return lines;
    }

    public static double Total(IEnumerable<BudgetLine> lines) => lines.Sum(l => l.DeltaV ?? 0);
}
