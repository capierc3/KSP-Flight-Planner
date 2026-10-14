using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App.Services;

/// <summary>
/// What times a landing at a spot: each body's rotation angle at UT 0 (θ₀, docs/MATH.md §2.9), and its surface bake.
/// θ₀ comes from the bake, else from the live game (asked once per body), else, for Kerbin, the stock 90°.
/// </summary>
public sealed class BodyRotation(SurfaceBakeRepository bakes, KspLink link)
{
    private readonly Dictionary<string, double> _live = [];

    public SurfaceBake? Bake(string bodyName) => bakes.Get(bodyName);

    /// <summary>θ₀ in degrees, or null when there's no bake, no game to ask, and it isn't Kerbin.</summary>
    public async Task<double?> InitialRotationDegAsync(CelestialBody body)
    {
        if (Bake(body.Name) is { } bake) return bake.InitialRotationDeg;
        if (_live.TryGetValue(body.Name, out var known)) return known;
        if (link.IsConnected)
        {
            try
            {
                var deg = await link.InitialRotationAsync(body.Name);
                _live[body.Name] = deg;
                return deg;
            }
            catch (Exception) { /* fall through to the stock value */ }
        }
        return body.Name == LaunchSite.Ksc.BodyName ? LaunchSite.Ksc.BodyInitialRotationDeg : null;
    }

    /// <summary>Why a landing can't be timed on <paramref name="body"/>.</summary>
    public static string Missing(CelestialBody body) =>
        $"Landing at a spot needs {body.Name}'s rotation: connect to KSP (or bake {body.Name} with Tools › Bake Surface Data) and plan again.";
}
