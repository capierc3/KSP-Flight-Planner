using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;
using Xunit.Abstractions;

namespace KspFlightPlanner.Tests;

public sealed class TransferWindowsTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kspfp-test-{Guid.NewGuid():N}.db");
    private readonly Dictionary<string, CelestialBody> _stock;

    public TransferWindowsTests(ITestOutputHelper output)
    {
        _output = output;
        var db = new PlannerDatabase(_path);
        db.Initialize();
        var repo = new BodyRepository(db);
        _stock = repo.GetBodies(repo.GetSystems()[0].Id).ToDictionary(b => b.Name);
    }

    public void Dispose()
    {
        TestDb.Release(_path);
        File.Delete(_path);
    }

    [Fact]
    public void Lists_every_planet_from_kerbin_soonest_first()
    {
        var all = TransferWindows.All(_stock["Kerbin"], _stock["Kerbol"], _stock.Values, 0);
        Assert.All(all, w => Assert.True(w.Window.DepartureUt >= 0));
        foreach (var w in all)
            _output.WriteLine($"{w.Destination.Name,-6} {GameCalendar.Kerbin.FormatDay(w.Window.DepartureUt),-20} " +
                              $"{w.Window.TotalDv,8:N1} m/s  {w.Window.FlightTimeS / GameCalendar.Kerbin.SecondsPerDay,6:N0} d  phase {w.Window.PhaseAngleDeg:N1}°");

        Assert.Equal(new[] { "Moho", "Eve", "Duna", "Dres", "Jool", "Eeloo" }.Order(), all.Select(w => w.Destination.Name).Order());
        Assert.True(all.Zip(all.Skip(1)).All(p => p.First.Window.DepartureUt <= p.Second.Window.DepartureUt));
    }

    [Fact]
    public void The_duna_window_matches_the_transfer_planner_and_moves_on_once_gone()
    {
        var (kerbin, duna, sun) = (_stock["Kerbin"], _stock["Duna"], _stock["Kerbol"]);
        var next = TransferWindows.Next(kerbin, duna, sun, 0)!;
        var best = TransferPlanner.Plan(TransferWindows.Request(kerbin, duna, sun, 0)).BestWindow;
        Assert.Equal(best.DepartureUt, next.Window.DepartureUt, GameCalendar.Kerbin.SecondsPerDay * 3);
        Assert.Equal(best.TotalDv, next.Window.TotalDv, 15.0);

        // Once that window has gone, the next one is a synodic period (about 2.1 Kerbin years) later.
        var after = TransferWindows.Next(kerbin, duna, sun, next.StillShownUntilUt + 1)!;
        var synodic = TransferPlanner.Hohmann(TransferWindows.Request(kerbin, duna, sun, 0)).SynodicPeriodS;
        Assert.Equal(synodic, after.Window.DepartureUt - next.Window.DepartureUt, synodic * 0.1);
    }

    [Fact]
    public void A_window_open_now_is_shown_rather_than_the_next_one()
    {
        var (kerbin, duna, sun) = (_stock["Kerbin"], _stock["Duna"], _stock["Kerbol"]);
        var open = TransferWindows.Next(kerbin, duna, sun, 0)!.Window.DepartureUt;
        // Two days after its best day the window still shows (departure slightly in the past), not the one two years on.
        var now = open + 2 * GameCalendar.Kerbin.SecondsPerDay;
        var shown = TransferWindows.Next(kerbin, duna, sun, now)!;
        Assert.Equal(open, shown.Window.DepartureUt, GameCalendar.Kerbin.SecondsPerDay * 3);
        Assert.True(shown.StillShownUntilUt > now);
    }
}
