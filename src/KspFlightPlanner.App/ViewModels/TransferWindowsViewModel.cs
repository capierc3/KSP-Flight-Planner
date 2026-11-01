using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App.ViewModels;

/// <summary>One body on the Transfer windows list.</summary>
public sealed class WindowRow(UpcomingWindow upcoming) : ObservableObject
{
    private string _countdown = "";
    private bool _isOpen;

    public UpcomingWindow Upcoming { get; } = upcoming;
    public string Name => Upcoming.Destination.Name;
    public string Date => GameCalendar.Kerbin.FormatDay(Upcoming.Window.DepartureUt);
    public string Details =>
        $"{Format.Speed0(Upcoming.Window.TotalDv)} · {Days(Upcoming.Window.FlightTimeS)} flight";

    /// <summary>A flight time to the day: "270d", "3y 201d".</summary>
    private static string Days(double seconds)
    {
        var c = GameCalendar.Kerbin;
        var days = (int)Math.Round(seconds / c.SecondsPerDay);
        return days >= c.DaysPerYear ? $"{days / c.DaysPerYear}y {days % c.DaysPerYear}d" : $"{days}d";
    }

    /// <summary>"Now", or how long until the window.</summary>
    public string Countdown { get => _countdown; private set => SetProperty(ref _countdown, value); }

    /// <summary>The window is open: its best day is within a game day, or just past.</summary>
    public bool IsOpen { get => _isOpen; private set => SetProperty(ref _isOpen, value); }

    public void Tick(double nowUt)
    {
        var wait = Upcoming.Window.DepartureUt - nowUt;
        IsOpen = wait < GameCalendar.Kerbin.SecondsPerDay;
        Countdown = IsOpen ? "Now" : "in " + GameCalendar.Kerbin.FormatDuration(wait);
    }
}

/// <summary>
/// The next transfer window to every planet (or moon) from a body, soonest first, counting down on the game clock: the live
/// game's while connected to KSP, else the save's. A body's window is searched again once it has passed.
/// </summary>
public sealed class TransferWindowsViewModel : ObservableObject
{
    private readonly SaveSync _sync;
    private readonly Dictionary<long, CelestialBody> _byId;
    private readonly DispatcherTimer _timer;
    private CelestialBody? _origin;
    private bool _searching;
    private bool _stale = true;
    private string _clock = "";

    public TransferWindowsViewModel(BodyRepository bodies, SaveSync sync)
    {
        _sync = sync;
        var stock = bodies.GetSystems().FirstOrDefault(s => s.Name == BodyRepository.StockSystemName) ?? bodies.GetSystems().FirstOrDefault();
        var all = stock is null ? [] : bodies.GetBodies(stock.Id);
        _byId = all.ToDictionary(b => b.Id);
        foreach (var b in all.Where(b => b.Orbit is not null && b.ParentId is { } p && all.Count(s => s.ParentId == p && s.Orbit is not null) >= 2))
            Origins.Add(b);
        _origin = Origins.FirstOrDefault(b => b.IsDefault) ?? Origins.FirstOrDefault();

        PlanCommand = new RelayCommand<WindowRow>(row => { if (row is not null && _origin is not null) PlanRequested?.Invoke(_origin, row.Upcoming.Destination, Now); });
        // Another save, or a quickload back in time: search again from the new date.
        sync.PropertyChanged += OnSyncChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    /// <summary>Planets and moons with at least one sibling to go to.</summary>
    public ObservableCollection<CelestialBody> Origins { get; } = [];

    public CelestialBody? Origin
    {
        get => _origin;
        set
        {
            if (!SetProperty(ref _origin, value)) return;
            Rows.Clear();
            _stale = true;
            Tick();
        }
    }

    public ObservableCollection<WindowRow> Rows { get; } = [];

    /// <summary>Opens a row's trip in the Transfer Planner, searched from the game's date.</summary>
    public RelayCommand<WindowRow> PlanCommand { get; }

    /// <summary>Origin, destination, and the game time to search from.</summary>
    public event Action<CelestialBody, CelestialBody, double>? PlanRequested;

    /// <summary>Where the time comes from, and the date.</summary>
    public string Clock { get => _clock; private set => SetProperty(ref _clock, value); }

    public bool IsSearching
    {
        get => _searching;
        private set => SetProperty(ref _searching, value);
    }

    private double Now => _sync.Now ?? 0;

    private void OnSyncChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SaveSync.Save) or nameof(SaveSync.SaveName)) Tick();
    }

    private void Tick()
    {
        var now = Now;
        Clock = _sync.Now is null ? "No save chosen: counting from Year 1, Day 1"
            : _sync.Link.Game is not null ? $"{GameCalendar.Kerbin.FormatDate(now)} · live from KSP"
            : $"{GameCalendar.Kerbin.FormatDate(now)} · from the save";
        foreach (var row in Rows) row.Tick(now);
        // A window that has gone, or a clock that went back past where the search began.
        if (_stale || Rows.Any(r => now > r.Upcoming.StillShownUntilUt || now < r.Upcoming.SearchedFromUt - 1)) _ = SearchAsync(now);
    }

    private async Task SearchAsync(double now)
    {
        if (IsSearching || _origin is not { ParentId: { } parentId } origin || !_byId.TryGetValue(parentId, out var parent)) return;
        IsSearching = true;
        _stale = false;
        try
        {
            // Keep the windows still on the list; search the rest.
            var keep = Rows.Where(r => now <= r.Upcoming.StillShownUntilUt && now >= r.Upcoming.SearchedFromUt - 1)
                .Select(r => r.Upcoming).ToList();
            var bodies = _byId.Values.Where(b => keep.All(k => k.Destination.Id != b.Id)).ToList();
            var found = await Task.Run(() => TransferWindows.All(origin, parent, bodies, now));
            if (origin != _origin) return; // picked another origin meanwhile; its own search follows
            Rows.Clear();
            foreach (var w in keep.Concat(found).OrderBy(w => w.Window.DepartureUt))
            {
                var row = new WindowRow(w);
                row.Tick(Now);
                Rows.Add(row);
            }
        }
        finally
        {
            IsSearching = false;
            if (origin != _origin) _stale = true;
        }
    }
}
