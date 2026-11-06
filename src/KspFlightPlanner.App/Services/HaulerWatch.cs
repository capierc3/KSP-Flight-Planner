using System.Globalization;
using KRPC.Client;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.App.Services;

/// <summary>An off-course background trip as the add-on reports it: the ship and target, and both orbits now.</summary>
public sealed record HaulerReplanRequest(string Vessel, string Target, double Now, string ShipBody, OrbitalElements ShipOrbit, string TargetBody,
    OrbitalElements TargetOrbit)
{
    /// <summary>The way the trip was planned ("Meets soonest"…), to keep when the fuel allows; empty when there was one.</summary>
    public string Way { get; init; } = "";

    /// <summary>The Δv in the ship's tanks (m/s), as the add-on reckons it; null from an older add-on.</summary>
    public double? DeltaVLeft { get; init; }

    /// <summary>One of the add-on's tab-separated lines (PlannerHaulers.ReplanRequests); null if it can't be read.</summary>
    public static HaulerReplanRequest? Parse(string line)
    {
        var f = line.Split('\t');
        if (f.Length < 19) return null;
        try
        {
            double D(int i) => double.Parse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture);
            OrbitalElements Orbit(int i) => new SavedOrbit(D(i), D(i + 1), D(i + 2), D(i + 3), D(i + 4), D(i + 5), D(i + 6), -1, null).ToElements();
            return new HaulerReplanRequest(f[0], f[1], D(2), KspLink.PlannerBodyName(f[3]), Orbit(4), KspLink.PlannerBodyName(f[11]), Orbit(12))
            {
                Way = f.Length > 19 ? f[19] : "",
                DeltaVLeft = f.Length > 20 ? D(20) : null,
            };
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// Plans background trips again when the game finds them off course (docs/MATH.md §11.3). The add-on checks each trip
/// right after every burn and ten minutes before the next, pauses it if it would miss, and stops time warp; this asks it
/// every few seconds for paused trips, plans each from where the ship really is, and sends the new plan. When it can't
/// plan one yet (still leaving the Mun, say) the game is told why, and it tries again a little later.
/// </summary>
public sealed class HaulerWatch
{
    /// <summary>How long a new plan's first burn waits at least, as From here plans.</summary>
    public const double LeadS = 120;

    private static readonly TimeSpan AskEvery = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    private readonly SaveSync _sync;
    private readonly KspLink _link;
    private readonly Dictionary<string, (DateTime At, string Body)> _failed = new(StringComparer.Ordinal);
    private DateTime _askedAt;
    private bool _busy;

    public HaulerWatch(SaveSync sync, KspLink link)
    {
        _sync = sync;
        _link = link;
        link.GameUpdated += OnGameUpdated;
    }

    /// <summary>A trip was planned again (raised on the UI thread): the add-on's one-line summary.</summary>
    public event Action<string>? Replanned;

    private async void OnGameUpdated(LiveGame? game)
    {
        if (game is null || _busy || DateTime.UtcNow - _askedAt < AskEvery) return;
        _busy = true;
        _askedAt = DateTime.UtcNow;
        try
        {
            foreach (var request in await _link.HaulerReplanRequestsAsync())
                await ReplanAsync(request);
        }
        catch (Exception)
        {
            // No add-on, an older one, or the connection went: nothing to do (the link says so itself).
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ReplanAsync(HaulerReplanRequest r)
    {
        // After a failure, wait a little before planning again, unless the ship has moved on to another body.
        if (_failed.TryGetValue(r.Vessel, out var last) && last.Body == r.ShipBody && DateTime.UtcNow - last.At < RetryAfter) return;

        var bodies = _sync.StockBodies();
        if (!bodies.TryGetValue(r.ShipBody, out var shipBody) || !bodies.TryGetValue(r.TargetBody, out var targetBody))
        {
            await FailAsync(r, $"the planner doesn't know {r.ShipBody} or {r.TargetBody}.");
            return;
        }

        (HaulerTrip? Trip, string? Wait) planned;
        try
        {
            var all = bodies.Values.ToList();
            planned = await Task.Run(() =>
            {
                var trip = HaulerReplan.Plan(shipBody, r.ShipOrbit, r.Vessel, targetBody, new RendezvousTarget(r.Target, r.TargetOrbit), all, r.Now, LeadS, out var wait,
                    r.Way, r.DeltaVLeft);
                return (trip, wait);
            });
        }
        catch (ArgumentException ex)
        {
            await FailAsync(r, ex.Message);
            return;
        }
        if (planned.Trip is not { } t)
        {
            await FailAsync(r, planned.Wait ?? "it can't be planned from here yet.");
            return;
        }

        try
        {
            var burns = t.Burns.Where(b => !b.ByReleasedCraft).Select(b => (b.Ut, b.Burn, b.Title)).ToList();
            var summary = await _link.StartBackgroundTripAsync(r.Vessel, r.Target, burns, t.MeetUt, t.Way);
            if (t.Note is { } note) await _link.HaulerNoteAsync(r.Vessel, note);
            _failed.Remove(r.Vessel);
            Replanned?.Invoke(summary);
        }
        catch (RPCException ex)
        {
            await FailAsync(r, ex.Message);
        }
    }

    private async Task FailAsync(HaulerReplanRequest r, string why)
    {
        _failed[r.Vessel] = (DateTime.UtcNow, r.ShipBody);
        try { await _link.HaulerReplanProblemAsync(r.Vessel, why); }
        catch (Exception) { /* the game will ask again */ }
    }
}
