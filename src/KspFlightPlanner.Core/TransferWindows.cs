using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core;

/// <summary>The next launch window from one body to another, and how long it lasts on the list.</summary>
/// <param name="StillShownUntilUt">After this the window has gone and the next one should be searched.</param>
public sealed record UpcomingWindow(CelestialBody Destination, TransferWindow Window, double SearchedFromUt, double StillShownUntilUt);

/// <summary>
/// The next transfer window from a body to each of its siblings (docs/MATH.md §3.10): the first dip in the porkchop plot's
/// cheapest Δv by departure date, not just the cheapest date in the search, so a window that is open right now isn't
/// skipped for a slightly cheaper one a synodic period later.
/// </summary>
public static class TransferWindows
{
    /// <summary>How far back a window still counts as open, as a share of the synodic period.</summary>
    public const double OpenShare = 0.03;

    /// <summary>The next window to every body orbiting the same parent as <paramref name="origin"/>, soonest first.</summary>
    public static IReadOnlyList<UpcomingWindow> All(CelestialBody origin, CelestialBody parent, IEnumerable<CelestialBody> bodies, double nowUt) =>
        bodies.Where(b => b.Id != origin.Id && b.ParentId == parent.Id && b.Orbit is not null && !b.IsCustomLocation)
            .AsParallel()
            .Select(b => Next(origin, b, parent, nowUt))
            .OfType<UpcomingWindow>()
            .OrderBy(w => w.Window.DepartureUt)
            .ToList();

    /// <summary>
    /// The next window to <paramref name="destination"/> from a parking orbit at the origin's default height, captured into
    /// one at the destination's. Null when no transfer solves (a body with no orbit data).
    /// </summary>
    public static UpcomingWindow? Next(CelestialBody origin, CelestialBody destination, CelestialBody parent, double nowUt)
    {
        try
        {
            var probe = Request(origin, destination, parent, nowUt);
            var synodic = TransferPlanner.Hohmann(probe).SynodicPeriodS;
            var lookBack = synodic * OpenShare;
            // The game starts at UT 0: nothing leaves before then.
            var request = probe with { EarliestDepartureUt = Math.Max(0, nowUt - lookBack) };
            // Columns every ~1% of the synodic period are fine enough to see each dip; refining finds its bottom.
            var plot = TransferPlanner.Plan(request, columns: 120, rows: 40).Porkchop!;

            var byDeparture = Enumerable.Range(0, plot.Columns).Select(c => CheapestIn(plot, c)).ToArray();
            var floor = byDeparture.Where(double.IsFinite).DefaultIfEmpty(double.NaN).Min();
            if (!double.IsFinite(floor)) return null;

            // The first dip: a column cheaper than both neighbours and within half again of the cheapest. The search
            // starts a little in the past, so a window that is just open is a dip too, not the edge of the plot.
            var column = Enumerable.Range(1, plot.Columns - 2).FirstOrDefault(c =>
                byDeparture[c] <= byDeparture[c - 1] && byDeparture[c] <= byDeparture[c + 1] && byDeparture[c] <= floor * 1.5, -1);
            if (column < 0) column = Array.IndexOf(byDeparture, floor);

            var window = Refine(request, plot, column);
            if (window is null) return null;
            return new UpcomingWindow(destination, window, nowUt, Math.Max(window.DepartureUt, nowUt) + lookBack);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    public static TransferRequest Request(CelestialBody origin, CelestialBody destination, CelestialBody parent, double earliestUt) =>
        new(origin, destination, parent, origin.DefaultParkingAltitudeM, destination.DefaultParkingAltitudeM, earliestUt);

    private static double CheapestIn(PorkchopPlot plot, int column)
    {
        var best = double.NaN;
        for (var r = 0; r < plot.Rows; r++)
            if (plot[column, r] is var v && double.IsFinite(v) && !(v >= best)) best = v;
        return best;
    }

    /// <summary>Zooms in around one porkchop cell, keeping the departure within the dip (a column either side).</summary>
    private static TransferWindow? Refine(TransferRequest request, PorkchopPlot plot, int column)
    {
        var row = Enumerable.Range(0, plot.Rows).MinBy(r => double.IsFinite(plot[column, r]) ? plot[column, r] : double.PositiveInfinity);
        double depStep = plot.DepartureAt(1) - plot.DepartureAt(0), tofStep = plot.FlightTimeAt(1) - plot.FlightTimeAt(0);
        double lo = plot.DepartureAt(Math.Max(0, column - 1)), hi = plot.DepartureAt(Math.Min(plot.Columns - 1, column + 1));
        var best = TransferPlanner.Evaluate(request, plot.DepartureAt(column), plot.FlightTimeAt(row));
        for (var iteration = 0; iteration < 8; iteration++)
        {
            double centreDep = best?.DepartureUt ?? plot.DepartureAt(column), centreTof = best?.FlightTimeS ?? plot.FlightTimeAt(row);
            for (var i = -5; i <= 5; i++)
            for (var j = -5; j <= 5; j++)
            {
                var dep = Math.Clamp(centreDep + i * depStep / 5, lo, hi);
                var tof = Math.Max(1, centreTof + j * tofStep / 5);
                if (TransferPlanner.Evaluate(request, dep, tof) is { } w && double.IsFinite(w.TotalDv) && (best is null || w.TotalDv < best.TotalDv))
                    best = w;
            }
            depStep /= 3;
            tofStep /= 3;
        }
        return best;
    }
}
