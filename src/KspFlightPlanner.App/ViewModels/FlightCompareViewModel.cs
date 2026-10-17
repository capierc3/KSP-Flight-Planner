using System.Globalization;
using System.Windows;
using System.Windows.Media;
using KspFlightPlanner.App.Controls;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App.ViewModels;

/// <summary>One figure in the comparison table; the best of its column is picked out.</summary>
public sealed record CompareCell(string Text, bool IsBest);

/// <summary>One flight in the comparison: its figures, its colour on the charts, and whether the charts show it.</summary>
public sealed class CompareRow(FlightLog log, FlightReport report, FlightFigures figures, Color color, Action changed) : ObservableObject
{
    private bool _isShown = true;

    public FlightLog Log { get; } = log;
    public FlightReport Report { get; } = report;
    public FlightFigures Figures { get; } = figures;
    public Color Color { get; } = color;
    public Brush Swatch { get; } = new SolidColorBrush(color);
    public string Launched => GameCalendar.Kerbin.FormatDate(Log.LaunchUt);
    public IReadOnlyList<CompareCell> Cells { get; set; } = [];

    /// <summary>Drawn on the charts.</summary>
    public bool IsShown
    {
        get => _isShown;
        set { if (SetProperty(ref _isShown, value)) changed(); }
    }
}

/// <summary>The Compare flights document: one rocket's flights side by side, figures in a table and overlaid on charts.</summary>
public sealed class FlightCompareViewModel : ObservableObject
{
    public static readonly IReadOnlyList<string> Columns =
        ["Design", "Outcome", "Δv used", "Δv to orbit", "Time to orbit", "Ascent losses", "Gravity", "Drag & steering", "Max Q", "Max g", "Touchdown"];

    public static readonly IReadOnlyList<string> RangeKinds = ["Ascent", "Whole flight", "Last landing"];

    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x64, 0xB5, 0xF6), Color.FromRgb(0xFF, 0xB7, 0x4D), Color.FromRgb(0x81, 0xC7, 0x84),
        Color.FromRgb(0xBA, 0x68, 0xC8), Color.FromRgb(0xEF, 0x53, 0x50), Color.FromRgb(0x4D, 0xD0, 0xE1),
        Color.FromRgb(0xFF, 0xF1, 0x76), Color.FromRgb(0xA1, 0x88, 0x7F),
    ];

    private readonly FlightLogRepository _logs;
    private readonly SaveSync _sync;
    private string? _rocket;
    private string _chartKind = FlightReportViewModel.ChartKinds[0];
    private string _rangeKind = RangeKinds[0];
    private Dictionary<string, CelestialBody> _bodies = [];

    public FlightCompareViewModel(FlightLogRepository logs, SaveSync sync)
    {
        _logs = logs;
        _sync = sync;
    }

    /// <summary>Every rocket with a logged flight.</summary>
    public IReadOnlyList<string> Rockets { get; private set; } = [];

    public string? Rocket
    {
        get => _rocket;
        set { if (SetProperty(ref _rocket, value)) Load(); }
    }

    public IReadOnlyList<CompareRow> Rows { get; private set; } = [];
    public bool HasRows => Rows.Count > 0;

    public string Summary => Rows.Count == 0 ? "Pick a rocket: its logged flights are compared here."
        : $"{Rows.Count} flight{(Rows.Count == 1 ? "" : "s")}" +
          (Rows.Select(r => r.Figures.Design).Where(d => d != "—").Distinct().Count() is > 1 and var n
              ? $" · {n} versions of the design (A, B… by part count and launch mass): compare like with like" : "") +
          ". The best of each column is in green.";

    public string ChartKind
    {
        get => _chartKind;
        set { if (SetProperty(ref _chartKind, value)) OnPropertyChanged(nameof(Chart)); }
    }

    public string RangeKind
    {
        get => _rangeKind;
        set { if (SetProperty(ref _rangeKind, value)) OnPropertyChanged(nameof(Chart)); }
    }

    public ChartData? Chart => Rows.Any(r => r.IsShown) ? BuildChart() : null;

    /// <summary>Each shown flight's climb: altitude against distance from the pad.</summary>
    public ChartData? AscentProfile { get; private set; }

    public bool HasAscentProfile => AscentProfile is not null;

    /// <summary>Re-reads the rockets (after a flight was stored or deleted), keeping the one shown.</summary>
    public void Refresh()
    {
        Rockets = _logs.GetAll().Select(l => l.VesselName).Distinct().Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        OnPropertyChanged(nameof(Rockets));
        if (_rocket is not null && !Rockets.Contains(_rocket)) _rocket = null;
        _rocket ??= Rockets.FirstOrDefault();
        OnPropertyChanged(nameof(Rocket));
        Load();
    }

    /// <summary>Shows a rocket's flights.</summary>
    public void Show(string rocket)
    {
        Refresh();
        Rocket = rocket;
    }

    private void Load()
    {
        _bodies = _sync.StockBodies();
        var ids = _logs.GetAll().Where(l => l.VesselName == _rocket).OrderBy(l => l.LaunchUt).Select(l => l.Id);
        var logs = ids.Select(_logs.Get).OfType<FlightLog>().Select(l => l.WithoutRailsSamples()).ToList();
        var designs = FlightComparison.Designs(logs);
        var rows = logs.Select((log, k) =>
        {
            var report = FlightAnalysis.Analyze(log, b => _bodies.GetValueOrDefault(b));
            return new CompareRow(log, report, FlightComparison.Figures(log, report, designs[log.Id]), Palette[k % Palette.Length], ChartsChanged);
        }).ToList();

        var figures = rows.Select(r => r.Figures).ToList();
        CompareCell Cell(FlightFigures f, Func<FlightFigures, double?> value, Func<double, string> text) =>
            value(f) is { } v ? new CompareCell(text(v), rows.Count > 1 && v == FlightComparison.Best(figures, value)) : new CompareCell("—", false);
        foreach (var row in rows)
        {
            var f = row.Figures;
            row.Cells =
            [
                new(f.Design, false),
                new(f.Outcome == FlightLogOutcome.InFlight ? "In flight" : FlightLogs.EndText(f.Outcome), false),
                new(Ms(f.DeltaVUsed), false),
                Cell(f, x => x.DeltaVToOrbit, Ms),
                Cell(f, x => x.TimeToOrbitS, FlightLogs.Duration),
                Cell(f, x => x.AscentLoss, Ms),
                Cell(f, x => x.GravityLoss, Ms),
                Cell(f, x => x.DragAndSteeringLoss, Ms),
                Cell(f, x => x.MaxQPa, v => $"{v / 1000:N1} kPa"),
                Cell(f, x => x.MaxG, v => $"{v:N1} g"),
                Cell(f, x => x.TouchdownSpeed, v => $"{v:N1} m/s"),
            ];
        }
        Rows = rows;
        AscentProfile = BuildAscentProfile();
        foreach (var name in new[] { nameof(Rows), nameof(HasRows), nameof(Summary), nameof(Chart), nameof(AscentProfile), nameof(HasAscentProfile) }) OnPropertyChanged(name);
    }

    private void ChartsChanged()
    {
        AscentProfile = BuildAscentProfile();
        OnPropertyChanged(nameof(Chart));
        OnPropertyChanged(nameof(AscentProfile));
        OnPropertyChanged(nameof(HasAscentProfile));
    }

    /// <summary>
    /// The shown flights over each other. The ascent and the whole flight line up at the launch; a landing lines up at
    /// the start of its descent (time since leaving orbit), so the approaches can be laid over each other.
    /// </summary>
    private ChartData BuildChart()
    {
        var series = new List<ChartSeries>();
        foreach (var row in Rows.Where(r => r.IsShown))
        {
            var (from, to, zero) = RangeKind switch
            {
                "Ascent" when row.Report.Ascent is { } a => (0.0, a.EndMetS, 0.0),
                "Last landing" when row.Report.Touchdowns.Count > 0 && row.Report.Touchdowns[^1] is var t => (t.DescentStartMetS, t.MetS + 10, t.DescentStartMetS),
                "Whole flight" => (double.NegativeInfinity, double.PositiveInfinity, 0.0),
                _ => (double.NaN, double.NaN, 0.0),
            };
            if (double.IsNaN(from)) continue; // this flight has no such part
            var samples = row.Log.Samples;
            var first = Enumerable.Range(0, samples.Count).FirstOrDefault(i => samples[i].MetS >= from);
            var dvBefore = RangeKind == "Last landing" ? row.Report.DeltaVSoFar[first] : 0; // a landing's Δv counts from its start
            var points = new List<Point>();
            for (var i = first; i < samples.Count; i++)
            {
                var s = samples[i];
                if (s.MetS > to) break;
                points.Add(new Point(s.MetS - zero, ChartKind == "Δv used" ? row.Report.DeltaVSoFar[i] - dvBefore : Value(s)));
            }
            if (points.Count > 1) series.Add(new ChartSeries(row.Launched, row.Color, [points]));
        }
        var (title, format) = ChartKind switch
        {
            "Speed" => ("Surface speed (m/s)", (Func<double, string>)(y => y.ToString("N0", CultureInfo.CurrentCulture) + " m/s")),
            "Δv used" => ("Δv used (m/s)", y => y.ToString("N0", CultureInfo.CurrentCulture) + " m/s"),
            "Mass" => ("Mass (t)", y => y.ToString("N2", CultureInfo.CurrentCulture) + " t"),
            "Throttle" => ("Throttle (%)", y => y.ToString("N0", CultureInfo.CurrentCulture) + " %"),
            "Dynamic pressure" => ("Dynamic pressure (kPa)", y => y.ToString("N1", CultureInfo.CurrentCulture) + " kPa"),
            "G-force" => ("G-force (g)", y => y.ToString("N1", CultureInfo.CurrentCulture) + " g"),
            _ => ("Altitude (km)", y => y.ToString(Math.Abs(y) >= 100 ? "N0" : "N1", CultureInfo.CurrentCulture) + " km"),
        };
        return new ChartData(RangeKind == "Last landing" ? "Time since leaving orbit" : "MET", title, series, XIsTime: true, format);
    }

    private double Value(FlightSample s) => ChartKind switch
    {
        "Speed" => s.SurfaceSpeed,
        "Mass" => s.MassKg / 1000,
        "Throttle" => s.Throttle * 100,
        "Dynamic pressure" => s.DynamicPressurePa / 1000,
        "G-force" => s.GForce,
        _ => s.AltitudeM / 1000,
    };

    private ChartData? BuildAscentProfile()
    {
        var series = new List<ChartSeries>();
        foreach (var row in Rows.Where(r => r.IsShown && r.Report.Ascent is not null && r.Log.Samples.Count > 0))
        {
            if (_bodies.GetValueOrDefault(row.Log.Samples[0].Body) is not { } home) continue;
            var end = row.Report.Ascent!.EndMetS;
            var pad = row.Log.Samples[0];
            series.Add(new ChartSeries(row.Launched, row.Color, [row.Log.Samples.TakeWhile(x => x.MetS <= end)
                .Select(x => new Point(FlightAnalysis.Downrange(pad, x, home.EquatorialRadiusM) / 1000, x.AltitudeM / 1000)).ToList()]));
        }
        return series.Count == 0 ? null
            : new ChartData("Downrange (km)", "Altitude (km)", series, XIsTime: false, y => y.ToString("N0", CultureInfo.CurrentCulture))
            { FormatX = x => x.ToString("N0", CultureInfo.CurrentCulture) };
    }

    private static string Ms(double v) => v.ToString("N0", CultureInfo.CurrentCulture) + " m/s";
}
