using KRPC.Client;
using KRPC.Client.Services.SpaceCenter;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.App.Services;

/// <summary>Background trips: the planner's kRPC add-on ("PlannerTraffic", mod/) flies a ship along planned burns while the player is elsewhere.</summary>
public sealed partial class KspLink
{
    private const string TrafficService = "PlannerTraffic";

    /// <summary>
    /// Hands <paramref name="vessel"/> the burns to fly on its own, then parks it beside <paramref name="target"/> at
    /// <paramref name="meetUt"/>. Returns the add-on's one-line summary. Throws RPCException with the add-on's reason
    /// (a ship not found, no fuel, a burn already passed), or when the add-on isn't installed.
    /// </summary>
    public Task<string> StartBackgroundTripAsync(string vessel, string target, IReadOnlyList<(double Ut, NodeBurn Burn, string Title)> burns, double meetUt, string way = "") =>
        CallAsync(c =>
        {
            var numbers = burns.OrderBy(b => b.Ut).SelectMany(b => new[] { b.Ut, b.Burn.Prograde, b.Burn.Normal, b.Burn.Radial }).ToList();
            var titles = burns.OrderBy(b => b.Ut).Select(b => b.Title).ToList();
            return Traffic<string>(c, "StartRoute", vessel, target, (IList<double>)numbers, (IList<string>)titles, meetUt, way ?? "");
        });

    /// <summary>
    /// Hands <paramref name="vessel"/> the burns of a trip that ends in <paramref name="orbit"/> round <paramref name="body"/>
    /// (no craft to meet): once the last burn is made, it's there. Throws as <see cref="StartBackgroundTripAsync"/> does.
    /// </summary>
    public Task<string> StartBackgroundOrbitTripAsync(string vessel, string body, IReadOnlyList<(double Ut, NodeBurn Burn, string Title)> burns, OrbitalElements orbit, string way = "") =>
        CallAsync(c =>
        {
            var numbers = burns.OrderBy(b => b.Ut).SelectMany(b => new[] { b.Ut, b.Burn.Prograde, b.Burn.Normal, b.Burn.Radial }).ToList();
            var titles = burns.OrderBy(b => b.Ut).Select(b => b.Title).ToList();
            var elements = new List<double>
            {
                orbit.SemiMajorAxisM, orbit.Eccentricity, orbit.InclinationDeg, orbit.LongitudeOfAscendingNodeDeg, orbit.ArgumentOfPeriapsisDeg,
                orbit.MeanAnomalyAtEpochRad, orbit.EpochS,
            };
            return Traffic<string>(c, "StartOrbitRoute", vessel, body, (IList<double>)numbers, (IList<string>)titles, (IList<double>)elements, way ?? "");
        });

    /// <summary>One line per background trip in the loaded save.</summary>
    public Task<IList<string>> BackgroundTripsAsync() => CallAsync(c => Traffic<IList<string>>(c, "Routes"));

    /// <summary>What has happened on a ship's background trip, one line per event.</summary>
    public Task<IList<string>> BackgroundTripLogAsync(string vessel) => CallAsync(c => Traffic<IList<string>>(c, "RouteLog", vessel));

    /// <summary>
    /// Sets KSP's target to this vessel (by kRPC id), as clicking it in map view does, then reads the vessels again so the
    /// planner sees the new target straight away. Needs the flight scene.
    /// </summary>
    public async Task SetTargetVesselAsync(ulong krpcId)
    {
        await CallAsync(c =>
        {
            var space = c.SpaceCenter();
            space.TargetVessel = space.Vessels.FirstOrDefault(v => v.id == krpcId)
                                 ?? throw new System.ArgumentException("That craft isn't in the game any more.");
            return true;
        });
        await RefreshAsync();
    }

    /// <summary>The background trips the add-on found off course, waiting to be planned again from where each ship is.</summary>
    public Task<IReadOnlyList<RouteReplanRequest>> RouteReplanRequestsAsync() =>
        CallAsync<IReadOnlyList<RouteReplanRequest>>(c => Traffic<IList<string>>(c, "ReplanRequests").Select(RouteReplanRequest.Parse).OfType<RouteReplanRequest>().ToList());

    /// <summary>A line about a ship's trip, shown in the game and kept on its log.</summary>
    public Task RouteNoteAsync(string vessel, string text) => CallAsync(c =>
    {
        c.Invoke(TrafficService, "RouteNote", [Encoder.Encode(vessel, typeof(string)), Encoder.Encode(text, typeof(string))]);
        return true;
    });

    /// <summary>Tells the game why an off-course trip couldn't be planned again yet (shown there once per reason).</summary>
    public Task RouteReplanProblemAsync(string vessel, string why) => CallAsync(c =>
    {
        c.Invoke(TrafficService, "ReplanProblem", [Encoder.Encode(vessel, typeof(string)), Encoder.Encode(why, typeof(string))]);
        return true;
    });

    private static T Traffic<T>(Connection c, string procedure, params object[] args)
    {
        var encoded = args.Select(a => Encoder.Encode(a, a switch { IList<double> => typeof(IList<double>), IList<string> => typeof(IList<string>), _ => a.GetType() })).ToList();
        return (T)Encoder.Decode(c.Invoke(TrafficService, procedure, encoded), typeof(T), c);
    }
}
