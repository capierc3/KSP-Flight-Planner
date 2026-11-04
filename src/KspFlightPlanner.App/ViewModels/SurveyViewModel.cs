using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App.ViewModels;

/// <summary>One requirement of the survey and whether the station meets it.</summary>
public sealed record SurveyCheck(string Text, bool Ok, string Detail = "")
{
    public string Mark => Ok ? "✓" : "✗";
}

/// <summary>A biome's line in the pane.</summary>
public sealed record SurveyBiomeRow(string Biome, string Coverage, string Report, string Spots, bool HasReport);

/// <summary>One of the engineer's spots to try.</summary>
public sealed record SurveySpotRow(string Title, string Position, string Advice);

/// <summary>A report in the pane's log.</summary>
public sealed record SurveyReportItem(string Date, string Body, string Text);

/// <summary>
/// Tools › Ore survey (docs/SURVEY.md): a station's survey of the body it orbits. What it needs, how much it has mapped,
/// what it has found per biome, and the reports as they came in.
/// </summary>
public sealed class SurveyViewModel : ObservableObject
{
    private readonly SurveyTracker _tracker;
    private readonly SaveSync _sync;
    private readonly KspLink _link;
    private SurveyStation? _station;
    private string? _stationId;
    private IReadOnlyList<SurveyCheck> _checks = [];
    private IReadOnlyList<SurveyBiomeRow> _biomes = [];
    private IReadOnlyList<SurveyReportItem> _reports = [];
    private string _coverageText = "";
    private string _status = "";
    private SurveyReportRow? _latest;
    private IReadOnlyList<SurveySpotRow> _spots = [];
    private bool _refreshingStations;
    private ImageSource? _mapImage;
    private bool _working;

    public SurveyViewModel(SurveyTracker tracker, SaveSync sync, KspLink link)
    {
        _tracker = tracker;
        _sync = sync;
        _link = link;
        RunSurveyCommand = new RelayCommand(() => _ = RunSurveyAsync(), () => !_working);
        FetchMapCommand = new RelayCommand(() => _ = FetchMapAsync(), () => !_working);
        SendWaypointsCommand = new RelayCommand(() => _ = SendWaypointsAsync(), () => !_working && _spots.Count > 0);
        tracker.Changed += Refresh;
        tracker.ReportsFiled += filed =>
        {
            _latest = filed[0];
            Refresh();
        };
        sync.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SaveSync.Save)) RefreshStations(); };
        link.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(KspLink.State)) Refresh(); };
        RefreshStations();
    }

    /// <summary>Vessels in orbit carrying a scanner.</summary>
    public ObservableCollection<SurveyStation> Stations { get; } = [];

    public SurveyStation? Station
    {
        get => _station;
        set
        {
            // While the list is rebuilt the box lets go of its pick for a moment: that isn't the player choosing.
            if (_refreshingStations || !SetProperty(ref _station, value)) return;
            _stationId = value?.VesselId;
            Refresh();
        }
    }

    public bool HasStations => Stations.Count > 0;
    public bool HasNoStations => !HasStations;

    public IReadOnlyList<SurveyCheck> Checks { get => _checks; private set => SetProperty(ref _checks, value); }
    public IReadOnlyList<SurveyBiomeRow> Biomes { get => _biomes; private set => SetProperty(ref _biomes, value); }
    public IReadOnlyList<SurveyReportItem> Reports { get => _reports; private set => SetProperty(ref _reports, value); }
    public string CoverageText { get => _coverageText; private set => SetProperty(ref _coverageText, value); }

    /// <summary>What the last button press did.</summary>
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>The newest report filed while the pane was open, on the picked station's body.</summary>
    public string? Latest => _latest is { } r && r.BodyName == Station?.BodyName ? r.Text : null;

    public bool HasLatest => Latest is not null;

    /// <summary>The engineer's top spots to try, best first.</summary>
    public IReadOnlyList<SurveySpotRow> Spots
    {
        get => _spots;
        private set { if (SetProperty(ref _spots, value)) { OnPropertyChanged(nameof(HasSpots)); OnPropertyChanged(nameof(SpotsTitle)); } }
    }

    public bool HasSpots => Spots.Count > 0;

    /// <summary>"Stabur Kerman's spots to try".</summary>
    public string SpotsTitle => Station is { Engineers.Count: > 0 } s ? $"{s.Engineers[0]}'s spots to try" : "Spots to try";

    /// <summary>The body's map: grey where nothing's been mapped, the estimate's colour where it has, true ore where analysed.</summary>
    public ImageSource? MapImage { get => _mapImage; private set => SetProperty(ref _mapImage, value); }

    /// <summary>The body hasn't had its orbital survey and the station has the scanner for it.</summary>
    public bool CanRunSurvey => Station is { HasSurveyScanner: true } s && _sync.Save?.ScannedBodies.Contains(s.BodyName) == false;

    /// <summary>The body's ore map hasn't been fetched from the game yet.</summary>
    public bool NeedsMap => Station is { } s && _tracker.Map(s.BodyName) is null;

    public RelayCommand RunSurveyCommand { get; }
    public RelayCommand FetchMapCommand { get; }

    /// <summary>Puts the spots on the game's map as waypoints.</summary>
    public RelayCommand SendWaypointsCommand { get; }

    private void RefreshStations()
    {
        var save = _sync.Save;
        var stations = save?.Vessels.Select(v => SurveyStation.From(v, save)).OfType<SurveyStation>()
            .OrderByDescending(s => s.IsSurveying).ThenBy(s => s.Name, StringComparer.CurrentCulture).ToList() ?? [];
        _refreshingStations = true;
        try
        {
            Stations.Clear();
            foreach (var s in stations) Stations.Add(s);
        }
        finally { _refreshingStations = false; }
        OnPropertyChanged(nameof(HasStations));
        OnPropertyChanged(nameof(HasNoStations));
        // Keep the same vessel picked (its record changes with every update).
        _station = stations.FirstOrDefault(s => s.VesselId == _stationId) ?? stations.FirstOrDefault();
        _stationId = _station?.VesselId;
        OnPropertyChanged(nameof(Station));
        Refresh();
    }

    private void Refresh()
    {
        Reports = _tracker.Reports().Select(r => new SurveyReportItem(GameCalendar.Kerbin.FormatDate(r.Ut), r.BodyName, r.Text)).ToList();
        OnPropertyChanged(nameof(CanRunSurvey));
        OnPropertyChanged(nameof(NeedsMap));
        OnPropertyChanged(nameof(Latest));
        OnPropertyChanged(nameof(HasLatest));
        OnPropertyChanged(nameof(SpotsTitle));
        if (Station is not { } s || _sync.Save is not { } save)
        {
            Spots = [];
            Checks = [];
            Biomes = [];
            CoverageText = "";
            MapImage = null;
            return;
        }

        var scanned = save.ScannedBodies.Contains(s.BodyName);
        var altitude = s.Orbit.SemiMajorAxisM * (1 + s.Orbit.Eccentricity) - (_tracker.Map(s.BodyName)?.RadiusM ?? 0);
        var map = _tracker.Map(s.BodyName);
        Checks =
        [
            new("Engineer aboard", s.Engineers.Count > 0, s.Engineers.Count > 0 ? string.Join(", ", s.Engineers) : "Nothing is mapped without one"),
            new("Survey scanner (M700)", s.HasSurveyScanner, s.HasSurveyScanner ? "Maps rough ore: Poor, Fair, Good…" : "Needed for the rough map"),
            new($"{s.BodyName}'s orbital survey done in game", scanned, scanned ? ""
                : s.HasSurveyScanner ? "Run it from a polar orbit (below)" : "Needs any craft with a survey scanner in a polar orbit (a probe will do): pick it above"),
            new("Narrow-band scanner (M4435)", s.HasNarrowBand, !s.HasNarrowBand ? "Needed for estimates (≈ 7%)"
                : map is not null && altitude > SurveyCoverage.NarrowBandMaxAltitudeM ? "Maps only below 500 km; part of this orbit is higher" : "Maps estimates below 500 km"),
            new($"{s.BodyName}'s ore data from the game", map is not null, map is not null ? $"Fetched {map.FetchedUtc.ToLocalTime():g}" : "Needs the live link and the planner's add-on"),
        ];

        if (map is null)
        {
            Spots = [];
            Biomes = [];
            CoverageText = "";
            MapImage = null;
            return;
        }
        var coverage = _tracker.Coverage(s.BodyName);
        var biomes = SurveyReports.Build(map, coverage, s.BodyName, save.AnalysedBiomes);
        CoverageText = $"Mapped of {s.BodyName}: rough {Percent(coverage.Rough)} · estimate {Percent(coverage.Estimate)}";
        Biomes = biomes.Select(b => new SurveyBiomeRow(b.Biome,
            $"rough {b.RoughCovered:P0} · estimate {b.EstimateCovered:P0}",
            b.Level is null ? "Not mapped enough yet" : b.Report,
            string.Join("   ", b.Spots.Select(p => $"{SurveyReports.Percent(p.Abundance)} at {Math.Abs(p.LatitudeDeg):0}° {(p.LatitudeDeg < 0 ? "S" : "N")} {Math.Abs(p.LongitudeDeg):0}° {(p.LongitudeDeg < 0 ? "W" : "E")}")),
            b.Level is not null)).ToList();
        MapImage = Draw(map, coverage, save.AnalysedBiomes);
        Spots = SurveyReports.Candidates(map, coverage, biomes).Select((c, i) => new SurveySpotRow(
            SurveyTracker.SpotName(i, c), c.Position,
            c.Analysed ? "Analysed: the true value here" : "Land here and run a biome analysis to find its best spots")).ToList();
    }

    private async Task SendWaypointsAsync()
    {
        if (Station is not { } s) return;
        if (!_link.IsConnected) { Status = "Connect to KSP first (KSP › Connect to KSP)."; return; }
        var spots = _tracker.Candidates(s.BodyName).Select((c, i) => (SurveyTracker.SpotName(i, c), c.LatitudeDeg, c.LongitudeDeg)).ToList();
        SetWorking(true);
        try
        {
            var added = await _link.SendSurveyWaypointsAsync(s.BodyName, spots);
            Status = $"{added} waypoints on {s.BodyName} in KSP (named Ore 1 to Ore {added}); sending again replaces them.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or KRPC.Client.RPCException)
        {
            Status = $"Couldn't add the waypoints: {ex.Message}";
        }
        finally { SetWorking(false); }
    }

    private static string Percent(float[] grid) => $"{grid.Count(t => !float.IsNaN(t)) / (double)grid.Length:P0}";

    private async Task RunSurveyAsync()
    {
        if (Station is not { } s || _sync.StockBodies().GetValueOrDefault(s.BodyName) is not { } body) return;
        var problems = OrbitalSurveyRules.Problems(s, body).ToList();
        if (!_link.IsConnected) problems.Insert(0, "Connect to KSP (KSP › Connect to KSP).");
        else if (_sync.ActiveStack?.Id != s.VesselId) problems.Insert(0, $"Switch to {s.Name} in KSP: the survey runs on the craft you're flying.");
        if (problems.Count > 0)
        {
            MessageBox.Show(Application.Current.MainWindow!, $"The orbital survey of {s.BodyName} can't start yet:\n\n• " + string.Join("\n• ", problems),
                "Run orbital survey", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SetWorking(true);
        try
        {
            var problem = await _link.PerformOrbitalSurveyAsync();
            if (problem is not null)
                MessageBox.Show(Application.Current.MainWindow!, problem, "Run orbital survey", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                Status = $"Survey of {s.BodyName} started. The game sends the data home; this pane sees it the next time KSP saves (a quicksave does it now).";
        }
        catch (Exception ex) when (ex is InvalidOperationException or KRPC.Client.RPCException)
        {
            Status = $"Couldn't start the survey: {ex.Message}";
        }
        finally { SetWorking(false); }
    }

    private async Task FetchMapAsync()
    {
        if (Station is not { } s) return;
        SetWorking(true);
        Status = $"Fetching {s.BodyName}'s ore map from the game…";
        try { Status = await _tracker.FetchMapAsync(s.BodyName) ?? $"{s.BodyName}'s ore map is in."; }
        finally { SetWorking(false); }
        Refresh();
    }

    private void SetWorking(bool working)
    {
        _working = working;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// The body flattened (north up, 180° W on the left): dark where unmapped, rough-only biomes in a dull tint, estimates
    /// coloured by their figure, analysed biomes by true ore; the best spots as white dots.
    /// </summary>
    private static WriteableBitmap Draw(SurveyMap map, SurveyCoverage coverage, IReadOnlySet<(string Body, string Biome)> analysed)
    {
        int width = map.Columns, height = map.Rows;
        var pixels = new uint[width * height];
        var top = Math.Max(0.01, map.Abundance.Max());
        for (var i = 0; i < map.Count; i++)
        {
            var row = height - 1 - i / width; // north at the top
            var column = i % width;
            var biome = map.BiomeOf(i);
            uint color;
            if (!float.IsNaN(coverage.Estimate[i]))
                color = biome is not null && analysed.Contains((map.BodyName, biome)) ? Heat(map.Abundance[i] / top) : Heat(map.Estimate[i] / top);
            else if (!float.IsNaN(coverage.Rough[i]))
                color = Dull(Heat(map.Estimate[i] / top));
            else
                color = 0xFF2B2B2B;
            pixels[row * width + column] = color;
        }
        foreach (var b in SurveyReports.Build(map, coverage, map.BodyName, analysed))
            foreach (var spot in b.Spots)
            {
                var column = (int)Math.Round((spot.LongitudeDeg + 180) / map.StepDeg) % width;
                var row = height - 1 - (int)Math.Round((spot.LatitudeDeg + 90) / map.StepDeg);
                for (var dr = -1; dr <= 1; dr++)
                    for (var dc = -1; dc <= 1; dc++)
                        if (row + dr >= 0 && row + dr < height)
                            pixels[(row + dr) * width + ((column + dc) % width + width) % width] = 0xFFFFFFFF;
            }
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Blue (none) through green and yellow to red (the body's richest).</summary>
    private static uint Heat(double t)
    {
        t = Math.Clamp(t, 0, 1);
        double r, g, b;
        if (t < 0.5) { r = 0; g = t * 2; b = 1 - t * 2; }
        else { r = (t - 0.5) * 2; g = 1 - (t - 0.5) * 2 * 0.6; b = 0; }
        return 0xFF000000 | (uint)(r * 230 + 25) << 16 | (uint)(g * 200 + 30) << 8 | (uint)(b * 200 + 40);
    }

    private static uint Dull(uint color)
    {
        uint Mix(int shift) => ((((color >> shift) & 0xFF) + 0x50) / 2) << shift;
        return 0xFF000000 | Mix(16) | Mix(8) | Mix(0);
    }
}
