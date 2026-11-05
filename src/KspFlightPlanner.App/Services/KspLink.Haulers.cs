using KRPC.Client;
using KspFlightPlanner.Core;

namespace KspFlightPlanner.App.Services;

/// <summary>Background trips: the planner's kRPC add-on ("PlannerHaulers", mod/) flies a ship along planned burns while the player is elsewhere.</summary>
public sealed partial class KspLink
{
    private const string HaulerService = "PlannerHaulers";

    /// <summary>
    /// Hands <paramref name="vessel"/> the burns to fly on its own, then parks it beside <paramref name="target"/> at
    /// <paramref name="meetUt"/>. Returns the add-on's one-line summary. Throws RPCException with the add-on's reason
    /// (a ship not found, no fuel, a burn already passed), or when the add-on isn't installed.
    /// </summary>
    public Task<string> StartBackgroundTripAsync(string vessel, string target, IReadOnlyList<(double Ut, NodeBurn Burn, string Title)> burns, double meetUt) =>
        CallAsync(c =>
        {
            var numbers = burns.OrderBy(b => b.Ut).SelectMany(b => new[] { b.Ut, b.Burn.Prograde, b.Burn.Normal, b.Burn.Radial }).ToList();
            var titles = burns.OrderBy(b => b.Ut).Select(b => b.Title).ToList();
            return Haulers<string>(c, "StartRoute", vessel, target, (IList<double>)numbers, (IList<string>)titles, meetUt);
        });

    /// <summary>One line per background trip in the loaded save.</summary>
    public Task<IList<string>> BackgroundTripsAsync() => CallAsync(c => Haulers<IList<string>>(c, "Routes"));

    /// <summary>What has happened on a ship's background trip, one line per event.</summary>
    public Task<IList<string>> BackgroundTripLogAsync(string vessel) => CallAsync(c => Haulers<IList<string>>(c, "RouteLog", vessel));

    private static T Haulers<T>(Connection c, string procedure, params object[] args)
    {
        var encoded = args.Select(a => Encoder.Encode(a, a switch { IList<double> => typeof(IList<double>), IList<string> => typeof(IList<string>), _ => a.GetType() })).ToList();
        return (T)Encoder.Decode(c.Invoke(HaulerService, procedure, encoded), typeof(T), c);
    }
}
