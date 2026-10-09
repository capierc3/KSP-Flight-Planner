using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Data;
using KRPC.Client;

namespace KspFlightPlanner.App.ViewModels;

/// <summary>A body in the bake window: tick it to bake it.</summary>
public sealed class BakeBodyRow(string name, string status) : ObservableObject
{
    private bool _isSelected;
    private string _status = status;

    public string Name { get; } = name;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    /// <summary>"Baked at 1° on 9 Oct 2026" or "Not baked".</summary>
    public string Status { get => _status; set => SetProperty(ref _status, value); }
}

/// <summary>A grid spacing to bake at, with what it costs.</summary>
public sealed record BakeStep(double Degrees, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// Tools › Bake Surface Data: samples each chosen body's terrain height and biome on a lat/lon grid, its rotation
/// at UT 0 and its air, over kRPC, and stores it as install data (docs/MATH.md §1.9, §2.6).
/// </summary>
public sealed class SurfaceBakeViewModel : ObservableObject
{
    private readonly KspLink _link;
    private readonly SurfaceBakeRepository _bakes;
    private readonly PlannerEvents _events;
    private readonly IReadOnlyDictionary<string, Core.Models.CelestialBody> _stock;
    private CancellationTokenSource? _cancel;
    private BakeStep _step;
    private double _progress;
    private string _progressText = "";
    private bool _isRunning;

    public SurfaceBakeViewModel(KspLink link, SurfaceBakeRepository bakes, PlannerEvents events,
        IReadOnlyDictionary<string, Core.Models.CelestialBody> stockBodies)
    {
        _link = link;
        _bakes = bakes;
        _events = events;
        _stock = stockBodies;
        Steps =
        [
            new(5, "5° — quick look (2,600 points a body)"),
            new(2, "2° — coarse (16,000 points)"),
            new(1, "1° — normal (65,000 points)"),
            new(0.5, "0.5° — fine (260,000 points; slow)"),
        ];
        _step = Steps[2];
        StartCommand = new RelayCommand(() => _ = RunAsync(), () => !_isRunning && _link.IsConnected && Bodies.Any(b => b.IsSelected));
        CancelCommand = new RelayCommand(() => _cancel?.Cancel(), () => _isRunning);
        SelectAllCommand = new RelayCommand(() => { foreach (var b in Bodies) b.IsSelected = true; }, () => !_isRunning);
        SelectNoneCommand = new RelayCommand(() => { foreach (var b in Bodies) b.IsSelected = false; }, () => !_isRunning);
        _link.PropertyChanged += OnLinkChanged;
        LoadBodies(stockBodies.Keys);
        if (_link.IsConnected) _ = LoadGameBodiesAsync();
    }

    public ObservableCollection<BakeBodyRow> Bodies { get; } = [];
    public IReadOnlyList<BakeStep> Steps { get; }
    public BakeStep Step { get => _step; set => SetProperty(ref _step, value); }

    public RelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }

    public bool IsRunning { get => _isRunning; private set { if (SetProperty(ref _isRunning, value)) OnPropertyChanged(nameof(IsIdle)); } }
    public bool IsIdle => !_isRunning;

    /// <summary>0–1 over all chosen bodies.</summary>
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }

    public string ConnectionText => _link.IsConnected
        ? $"Connected to KSP ({_link.Scene?.ToString() ?? "…"}). Heights may read 0 outside the flight scene; if they do, bake from flight."
        : "Not connected. Use Tools › Connect to KSP first; the bake asks the running game.";

    /// <summary>What happened, newest last: one line per body baked, plus problems.</summary>
    public ObservableCollection<string> Log { get; } = [];

    /// <summary>Set by the window: closing it cancels a running bake.</summary>
    public void Closing() => _cancel?.Cancel();

    private void OnLinkChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(KspLink.State) or nameof(KspLink.Scene)) OnPropertyChanged(nameof(ConnectionText));
        if (e.PropertyName == nameof(KspLink.State) && _link.IsConnected) _ = LoadGameBodiesAsync();
    }

    /// <summary>Lists the game's bodies (mods may add some), keeping ticks.</summary>
    private async Task LoadGameBodiesAsync()
    {
        try { LoadBodies(await _link.BodyNamesAsync()); }
        catch (Exception ex) when (ex is RPCException or InvalidOperationException) { }
    }

    private void LoadBodies(IEnumerable<string> names)
    {
        var ticked = Bodies.Where(b => b.IsSelected).Select(b => b.Name).ToHashSet();
        var order = Core.Ksp.SavedOrbit.StockBodyOrder;
        var baked = _bakes.List().ToDictionary(b => b.Body);
        Bodies.Clear();
        foreach (var name in names.Distinct().OrderBy(n => Array.IndexOf(order, n) is var i and >= 0 ? i : 99).ThenBy(n => n))
            Bodies.Add(new BakeBodyRow(name, StatusText(baked.TryGetValue(name, out var b) ? b : null)) { IsSelected = ticked.Contains(name) });
    }

    private static string StatusText((string Body, double StepDeg, DateTime BakedUtc)? bake) =>
        bake is { } b ? $"Baked at {b.StepDeg:0.#}° on {b.BakedUtc.ToLocalTime():d MMM yyyy, HH:mm}" : "Not baked";

    private async Task RunAsync()
    {
        var bodies = Bodies.Where(b => b.IsSelected).ToList();
        var step = Step.Degrees;
        var rows = SurfaceBake.GridRows(step);
        var total = bodies.Count * rows;
        var done = 0;
        _cancel = new CancellationTokenSource();
        IsRunning = true;
        var clock = Stopwatch.StartNew();
        try
        {
            foreach (var row in bodies)
            {
                row.Status = "Baking…";
                var started = clock.Elapsed;
                var bake = await BakeBodyAsync(row.Name, step, () =>
                {
                    done++;
                    Progress = (double)done / total;
                    var left = TimeSpan.FromSeconds(clock.Elapsed.TotalSeconds / done * (total - done));
                    ProgressText = $"{row.Name}: row {done - bodies.IndexOf(row) * rows} of {rows} · about {Format(left)} left";
                }, _cancel.Token);
                if (bake is null)
                {
                    row.Status = "Not baked";
                    done = (bodies.IndexOf(row) + 1) * rows;
                    continue;
                }
                _bakes.Save(bake);
                row.Status = StatusText((bake.BodyName, bake.StepDeg, bake.BakedUtc));
                Log.Add(Summary(bake, clock.Elapsed - started));
                _events.RaiseSurfaceDataChanged(bake.BodyName);
            }
            Progress = 1;
            ProgressText = $"Done in {Format(clock.Elapsed)}.";
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Cancelled. Bodies finished before that are kept; the one in progress isn't.";
            foreach (var row in bodies.Where(b => b.Status == "Baking…")) row.Status = StatusText(null);
        }
        catch (Exception ex)
        {
            // A kRPC error, or the connection dropping (KspLink has already disconnected and said why).
            ProgressText = "Stopped: " + ex.Message;
            Log.Add("Stopped: " + ex.Message);
            foreach (var row in bodies.Where(b => b.Status == "Baking…")) row.Status = StatusText(null);
        }
        finally
        {
            IsRunning = false;
            LoadBodies(Bodies.Select(b => b.Name).ToList());
        }
    }

    /// <summary>One body: constants, then the grid row by row, then the air. Null (with a log line) when it can't be baked.</summary>
    private async Task<SurfaceBake?> BakeBodyAsync(string name, double step, Action rowDone, CancellationToken cancel)
    {
        var info = await _link.ReadBodyAsync(name);
        var rows = SurfaceBake.GridRows(step);
        var columns = SurfaceBake.GridColumns(step);
        var heights = info.HasSolidSurface ? new float[rows * columns] : [];
        var biomes = info.HasSolidSurface ? new byte[rows * columns] : [];
        var biomeNames = info.Biomes.ToList();

        for (var r = 0; r < rows; r++)
        {
            cancel.ThrowIfCancellationRequested();
            if (info.HasSolidSurface)
            {
                var (rowHeights, rowBiomes) = await _link.SampleRowAsync(name, SurfaceBake.RowLatitude(step, r), step, biomeNames.Count > 0);
                Array.Copy(rowHeights, 0, heights, r * columns, columns);
                for (var c = 0; c < columns; c++)
                {
                    var biome = rowBiomes[c];
                    var index = biome is null ? -1 : biomeNames.IndexOf(biome);
                    if (biome is not null && index < 0 && biomeNames.Count < SurfaceBake.NoBiome) { biomeNames.Add(biome); index = biomeNames.Count - 1; }
                    biomes[r * columns + c] = index < 0 ? SurfaceBake.NoBiome : (byte)index;
                }
                // A whole band of exact zeros on a body with terrain means the game isn't giving real heights.
                if (r == rows / 2 && heights.AsSpan(r * columns, columns).ToArray().All(h => h == 0))
                {
                    Log.Add($"{name}: heights along the equator all read 0 m, so the game isn't giving terrain here. " +
                            "Try again from the flight scene.");
                    return null;
                }
            }
            rowDone();
        }

        var air = info.AtmosphereDepthM > 0 ? await _link.SampleAirAsync(name, info.AtmosphereDepthM, 1_000) : [];
        return new SurfaceBake(name, step, Wrap360(info.InitialRotationDeg), info.RotationPeriodS, info.RadiusM, info.Mu,
            DateTime.UtcNow, heights, biomes, biomeNames, air);
    }

    /// <summary>"Mun: 1° grid in 3m 12s · −1,203 to 7,061 m (wiki 7,061 m) · 17 biomes · rotated 230.0° at UT 0".</summary>
    private string Summary(SurfaceBake bake, TimeSpan took)
    {
        var parts = new List<string> { $"{bake.BodyName}: {bake.StepDeg:0.#}° grid in {Format(took)}" };
        if (bake.Extremes() is { } x)
        {
            var wiki = _stock.TryGetValue(bake.BodyName, out var body) && body.HighestElevationM is { } peak ? $" (wiki {peak:#,0} m)" : "";
            parts.Add($"{x.Lowest.H:#,0} to {x.Highest.H:#,0} m{wiki}");
        }
        if (bake.BiomeNames.Count > 0) parts.Add($"{bake.BiomeNames.Count} biomes");
        parts.Add($"rotated {bake.InitialRotationDeg:0.###}° at UT 0");
        if (bake.Atmosphere.Count > 0)
            parts.Add($"air {bake.Atmosphere[0].PressurePa / 1000:0.###} kPa at 0 m");
        if (_stock.TryGetValue(bake.BodyName, out var known) && Math.Abs(known.RotationPeriodS - bake.RotationPeriodS) > 0.01)
            parts.Add($"⚠ day {bake.RotationPeriodS:0.###} s, the planner has {known.RotationPeriodS:0.###} s");
        return string.Join(" · ", parts);
    }

    private static double Wrap360(double deg) => ((deg % 360) + 360) % 360;

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds:00}s" : $"{t.Seconds}s";
}
