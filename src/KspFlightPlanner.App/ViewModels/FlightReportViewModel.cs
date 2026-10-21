using System.Globalization;
using System.Windows;
using System.Windows.Media;
using KspFlightPlanner.App.Controls;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App.ViewModels;

public sealed record ReportCard(string Title, string Value, string Detail);
public sealed record ReportRow(string When, string What, string Value);

/// <summary>A line of ascent advice: ✓ went well, ✗ cost Δv, • a remark.</summary>
public sealed record AdviceRow(string Mark, string Title, string Detail, bool? Good);

/// <summary>A part of the flight a chart can show.</summary>
public sealed record ChartRange(string Title, double FromMetS, double ToMetS)
{
    public override string ToString() => Title;
}

/// <summary>The Flight report document: one logged flight's figures, charts, burns and timeline.</summary>
public sealed class FlightReportViewModel : ObservableObject
{
    public static readonly IReadOnlyList<string> ChartKinds =
        ["Altitude", "Speed", "Δv used", "Mass", "Throttle", "Dynamic pressure", "G-force"];

    private static readonly Color Blue = Color.FromRgb(0x64, 0xB5, 0xF6), Green = Color.FromRgb(0x81, 0xC7, 0x84),
        Orange = Color.FromRgb(0xFF, 0xB7, 0x4D), Purple = Color.FromRgb(0xBA, 0x68, 0xC8);

    /// <summary>A stretch between samples longer than this (game seconds) that isn't a noted gap is drawn joined.</summary>
    private const double SegmentBreakS = 1;

    /// <summary>Burns smaller than this (a tap of the throttle, RCS) are left out of the table.</summary>
    private const double MinBurnDv = 1;

    private readonly FlightLogRepository _logs;
    private readonly SaveSync _sync;
    private FlightLog? _log;
    private FlightReport? _report;
    private IReadOnlyList<FilledPoint> _filled = [];
    private Dictionary<string, CelestialBody> _bodies = [];
    private string? _mapBody;
    private AscentDiagnosis? _diagnosis;
    private OptimalAscent? _optimum;
    private string? _optimumStatus;
    private readonly Dictionary<(long, double), OptimalAscent?> _optima = []; // per flight and ascent end: the search takes seconds
    private string _chartKind = ChartKinds[0];
    private ChartRange? _range;

    public FlightReportViewModel(FlightLogRepository logs, SaveSync sync)
    {
        _logs = logs;
        _sync = sync;
    }

    public long? LogId => _log?.Id;
    public bool HasLog => _log is not null;
    public string Title => _log?.VesselName ?? "Flight report";

    public string Subtitle => _log is not { } log ? "Pick a flight in the Flight log pane."
        : string.Join(" · ", new[]
        {
            _sync.Save is { } save && FlightLogs.VesselOf(log.Summary, save) is { } vessel && vessel.Name != log.VesselName ? $"Now {vessel.Name}" : null,
            (log.FromLaunch ? "Launched " : "Joined in flight, launched ") + Core.GameCalendar.Kerbin.FormatDate(log.LaunchUt),
            log.Outcome == FlightLogOutcome.InFlight ? "still flying" : FlightLogs.EndText(log.Outcome),
            log.Route,
            FlightLogs.Duration(log.DurationS),
        }.Where(t => !string.IsNullOrEmpty(t)));

    public IReadOnlyList<ReportCard> Cards { get; private set; } = [];
    public IReadOnlyList<ReportRow> Burns { get; private set; } = [];
    public IReadOnlyList<ReportRow> DeltaVSplit { get; private set; } = [];
    public IReadOnlyList<ReportRow> Timeline { get; private set; } = [];
    public IReadOnlyList<ChartRange> Ranges { get; private set; } = [];
    public bool HasBurns => Burns.Count > 0;

    public string ChartKind
    {
        get => _chartKind;
        set { if (SetProperty(ref _chartKind, value)) OnPropertyChanged(nameof(Chart)); }
    }

    public ChartRange? Range
    {
        get => _range;
        set { if (SetProperty(ref _range, value)) OnPropertyChanged(nameof(Chart)); }
    }

    public ChartData? Chart => _log is { } log && _report is { } report ? BuildChart(log, report, _filled, ChartKind, Range) : null;

    // ---------------- Ascent coach ----------------

    public bool HasCoach => _diagnosis is not null;

    public IReadOnlyList<ReportCard> CoachCards => _diagnosis is not { } d || _report?.Ascent is not { } a ? [] :
    [
        new("Gravity loss", Ms(a.GravityLoss), "climbing against gravity"),
        new("Steering loss", d.SteeringLoss is { } st ? Ms(st) : "—", d.HasPitch ? "nose off the path while burning" : "no pitch recorded"),
        new(d.HasPitch ? "Drag loss" : "Drag & steering", Ms(d.DragLoss ?? a.DragAndSteeringLoss), "the air slowing it"),
        new("Launch TWR", d.LaunchTwr is { } twr ? twr.ToString("N2", CultureInfo.CurrentCulture) : "—", "thrust ÷ weight on the pad"),
    ];

    public IReadOnlyList<AdviceRow> Advice => _diagnosis?.Advice
        .Select(a => new AdviceRow(a.Good switch { true => "✓", false => "✗", _ => "•" }, a.Title, a.Detail, a.Good)).ToList() ?? [];

    /// <summary>The best climb for this rocket, or what the search is doing.</summary>
    public string? BestText => _optimum is { Best.ReachedOrbit: true } o && _report?.Ascent is { } a
        ? $"Best for this rocket: about {o.BestEstimate:N0} m/s to orbit, against {a.DeltaVSpent:N0} m/s flown " +
          (o.Saving >= 10 ? $"({o.Saving:N0} m/s to gain). " : "(you're already there). ") +
          $"Start the turn at {o.Best.Program!.TurnStartSpeed:N0} m/s, reach 45° at {Pitch45(o.Best) / 1000:N1} km and level by {o.Best.Program.TurnEndAltitudeM / 1000:N0} km, " +
          "following prograde through the thick air."
        : _optimumStatus;

    /// <summary>How far to trust <see cref="BestText"/>.</summary>
    public string? ModelText => _optimum is not { Best.ReachedOrbit: true } o ? null
        : o.ModelError is { } err
            ? $"Model check: flying your own steering through the model takes {o.Replay!.DeltaV:N0} m/s against {o.FlownDeltaV:N0} flown ({err:+0.0 %;-0.0 %}). " +
              (Math.Abs(err) <= 0.05 ? "Close, so the gain is a fair estimate." : "Not that close: take the gain as rough.")
            : o.Replay is not null
                ? "Rough: flying your own steering through the model didn't reach orbit, so the gain compares the model with the real flight."
                : "Rough: this flight has no pitch recorded to check the model against, so the gain compares the model with the real flight.";

    /// <summary>Pitch against altitude: the nose, where the craft was going, and the best program.</summary>
    public ChartData? PitchChart { get; private set; }

    /// <summary>The flown climb and the best one, altitude against distance from the pad.</summary>
    public ChartData? CoachProfile { get; private set; }

    private static double Pitch45(SimAscent run) => run.Path.FirstOrDefault(p => p.PitchDeg <= 45)?.AltitudeM ?? double.NaN;

    private void StartCoach(Dictionary<string, CelestialBody> bodies)
    {
        _diagnosis = null;
        _optimum = null;
        _optimumStatus = null;
        if (_log is not { } log || _report?.Ascent is not { } ascent || log.Samples.Count == 0 || bodies.GetValueOrDefault(log.Samples[0].Body) is not { } home)
            return;
        _diagnosis = AscentCoach.Diagnose(log, ascent, home);
        if (AscentSimulator.For(log, ascent, home) is not { } sim)
        {
            _optimumStatus = ascent.ReachedOrbit ? null : "No best climb: this flight didn't reach orbit.";
            return;
        }
        var key = (log.Id, ascent.EndMetS);
        if (_optima.TryGetValue(key, out var known))
        {
            _optimum = known;
            return;
        }
        _optimumStatus = "Working out the best climb for this rocket…";
        var flown = ascent.DeltaVSpent;
        Task.Run(() => sim.Optimize(flown)).ContinueWith(t =>
        {
            var result = t.IsCompletedSuccessfully ? t.Result : null;
            _optima[key] = result;
            if (_log?.Id != key.Id) return;
            _optimum = result;
            _optimumStatus = result is null ? "Couldn't work out a best climb for this flight." : null;
            BuildCoachCharts();
            foreach (var name in new[] { nameof(BestText), nameof(ModelText), nameof(PitchChart), nameof(CoachProfile) }) OnPropertyChanged(name);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void BuildCoachCharts()
    {
        PitchChart = CoachProfile = null;
        if (_diagnosis is not { } d || _log is not { } log) return;
        var km = (Func<double, string>)(x => x.ToString("N0", CultureInfo.CurrentCulture));
        var pitch = new List<ChartSeries>();
        if (d.HasPitch) pitch.Add(new ChartSeries("Nose", Blue, [d.Pitch.Select(p => new Point(p.AltitudeM / 1000, p.PitchDeg)).ToList()]));
        pitch.Add(new ChartSeries("Prograde", Color.FromRgb(0x9A, 0xA4, 0xB8), [d.Pitch.Select(p => new Point(p.AltitudeM / 1000, p.ProgradeDeg)).ToList()]) { Dashed = true });
        if (_optimum is { Best.ReachedOrbit: true } o)
            pitch.Add(new ChartSeries("Best", Green, [o.Best.Path.Where(p => !double.IsNaN(p.PitchDeg) && p.AltitudeM < 70_000)
                .Select(p => new Point(p.AltitudeM / 1000, p.PitchDeg)).ToList()]));
        PitchChart = new ChartData("Altitude (km)", "Pitch (°)", pitch, XIsTime: false, y => $"{y:N0}°") { FormatX = km };

        if (_optimum is { Best.ReachedOrbit: true } best && AscentProfile is { } flown)
            CoachProfile = flown with
            {
                Series = [flown.Series[0] with { Name = "Flown" },
                    new ChartSeries("Best", Green, [best.Best.Path.Select(p => new Point(p.DownrangeM / 1000, p.AltitudeM / 1000)).ToList()])],
            };
    }

    /// <summary>Bodies the flight can be drawn around (a planet's map shows its moons too).</summary>
    public IReadOnlyList<string> MapBodies { get; private set; } = [];

    public string? MapBody
    {
        get => _mapBody;
        set { if (SetProperty(ref _mapBody, value)) OnPropertyChanged(nameof(Map)); }
    }

    /// <summary>The flight from above <see cref="MapBody"/>.</summary>
    public FlightMapView? Map => _log is { } log && MapBody is { } at
        ? FlightMap.Build(log, _filled, at, b => _bodies.GetValueOrDefault(b), Parent)
        : null;

    public bool HasMap => MapBodies.Count > 0;

    /// <summary>Why there's no map, or null.</summary>
    public string? MapNote => _log is null || HasMap ? null
        : "No map for this flight: it was recorded before the planner kept which way the orbit faced. Flights recorded from now on have one.";

    private CelestialBody? Parent(CelestialBody body) =>
        body.ParentId is { } id ? _bodies.Values.FirstOrDefault(b => b.Id == id) : null;

    /// <summary>Altitude against distance along the ground from the pad, up to orbit.</summary>
    public ChartData? AscentProfile { get; private set; }

    public bool HasAscentProfile => AscentProfile is not null;

    /// <summary>Shows a log (or nothing). Keeps the chart picks when it's the same flight again (refreshed while recording).</summary>
    public void Show(long? id)
    {
        var same = id is not null && id == _log?.Id;
        _log = id is { } i ? _logs.Get(i)?.WithoutRailsSamples() : null;
        var bodies = _bodies = _sync.StockBodies();
        _report = _log is { } log ? FlightAnalysis.Analyze(log, b => bodies.GetValueOrDefault(b)) : null;
        _filled = _log is { } l ? FlightAnalysis.FillGaps(l.Samples, b => bodies.GetValueOrDefault(b), Parent) : [];
        Build(bodies);
        var keep = same ? _range?.Title : null;
        _range = Ranges.FirstOrDefault(r => r.Title == keep) ?? Ranges.FirstOrDefault();
        StartCoach(bodies);
        BuildCoachCharts();
        MapBodies = _log is { } shown ? FlightMap.Views(shown, b => bodies.GetValueOrDefault(b), Parent) : [];
        var keepMap = same ? _mapBody : null;
        _mapBody = MapBodies.Contains(keepMap ?? "") ? keepMap : MapBodies.FirstOrDefault();
        foreach (var name in new[] { nameof(LogId), nameof(HasLog), nameof(Title), nameof(Subtitle), nameof(Cards), nameof(Burns), nameof(HasBurns),
                     nameof(DeltaVSplit), nameof(Timeline), nameof(Ranges), nameof(Range), nameof(Chart), nameof(AscentProfile), nameof(HasAscentProfile),
                     nameof(MapBodies), nameof(MapBody), nameof(Map), nameof(HasMap), nameof(MapNote),
                     nameof(HasCoach), nameof(CoachCards), nameof(Advice), nameof(BestText), nameof(ModelText), nameof(PitchChart), nameof(CoachProfile) })
            OnPropertyChanged(name);
    }

    private void Build(Dictionary<string, CelestialBody> bodies)
    {
        if (_log is not { } log || _report is not { } r)
        {
            Cards = [];
            Burns = DeltaVSplit = Timeline = [];
            Ranges = [];
            AscentProfile = null;
            return;
        }

        var cards = new List<ReportCard> { new("Δv used", Ms(log.DeltaVUsed), $"{r.Burns.Count(b => b.DeltaV >= MinBurnDv)} burns") };
        if (r.Ascent is { } a)
        {
            cards.Add(new(a.ReachedOrbit ? "Δv to orbit" : "Δv on the way up", Ms(a.DeltaVSpent),
                a.ReachedOrbit ? $"in {Mmss(a.EndMetS)} · {Km(a.ApoapsisAltitudeM)} × {Km(a.PeriapsisAltitudeM)}" : "didn't reach orbit"));
            cards.Add(new("Ascent losses", Ms(a.TotalLoss), $"gravity {Ms(a.GravityLoss)} · drag & steering {Ms(a.DragAndSteeringLoss)}"));
            if (a.MaxQPa > 0) cards.Add(new("Max Q", $"{a.MaxQPa / 1000:N1} kPa", $"at {Km(a.MaxQAltitudeM)}, {Mmss(a.MaxQMetS)} in"));
        }
        if (r.MaxG > 0) cards.Add(new("Max g", $"{r.MaxG:N1} g", $"at MET {Controls.LineChartView.TimeText(r.MaxGMetS)}"));
        foreach (var t in r.Touchdowns)
            cards.Add(new(t.Splashed ? $"Splashdown on {t.Body}" : $"Touchdown on {t.Body}", Ms(Math.Abs(t.VerticalSpeed)),
                $"down · {Ms(t.SurfaceSpeed)} across · descent {Ms(t.DeltaVSpent)}"));
        Cards = cards;

        Burns = r.Burns.Where(b => b.DeltaV >= MinBurnDv).Select(b => new ReportRow(Controls.LineChartView.TimeText(b.StartMetS),
            $"{b.Body} · {Mmss(b.DurationS)} · stage {(b.FirstStage == b.LastStage ? $"{b.FirstStage}" : $"{b.FirstStage}→{b.LastStage}")}",
            Ms(b.DeltaV))).ToList();
        DeltaVSplit = r.ByBody.Select(b => new ReportRow("", $"Around {b.Body}", Ms(b.DeltaV)))
            .Concat(r.ByStage.Select(st => new ReportRow("", $"Stage {st.Stage}", Ms(st.DeltaV)))).ToList();
        Timeline = log.Events.Select(e => new ReportRow(Controls.LineChartView.TimeText(e.MetS), e.Text, "")).ToList();

        var ranges = new List<ChartRange> { new("Whole flight", double.NegativeInfinity, double.PositiveInfinity) };
        if (r.Ascent is { } up) ranges.Add(new(up.ReachedOrbit ? "Ascent to orbit" : "Ascent", 0, up.EndMetS));
        foreach (var t in r.Touchdowns)
            ranges.Add(new($"{(t.Splashed ? "Splashdown" : "Landing")} on {t.Body} ({Controls.LineChartView.TimeText(t.MetS)})", t.DescentStartMetS, t.MetS + 10));
        Ranges = ranges;

        AscentProfile = r.Ascent is { } asc && bodies.GetValueOrDefault(log.Samples[0].Body) is { } home
            ? new ChartData("Downrange (km)", "Altitude (km)",
                [new ChartSeries("Altitude", Blue, [log.Samples.TakeWhile(x => x.MetS <= asc.EndMetS)
                    .Select(x => new Point(FlightAnalysis.Downrange(log.Samples[0], x, home.EquatorialRadiusM) / 1000, x.AltitudeM / 1000)).ToList()])],
                XIsTime: false, FormatY: y => y.ToString("N0", CultureInfo.CurrentCulture))
            { FormatX = x => x.ToString("N0", CultureInfo.CurrentCulture) }
            : null;
    }

    private static ChartData BuildChart(FlightLog log, FlightReport report, IReadOnlyList<FilledPoint> filled, string kind, ChartRange? range)
    {
        var from = range?.FromMetS ?? double.NegativeInfinity;
        var to = range?.ToMetS ?? double.PositiveInfinity;
        var samples = log.Samples;
        var indexes = Enumerable.Range(0, samples.Count).Where(i => samples[i].MetS >= from && samples[i].MetS <= to).ToList();

        // Where the recording has holes: warp (noted where it ended) and not recorded (noted where it began).
        static (double From, double To) Span(FlightLogEvent e) =>
            e.Kind == FlightLogEventKind.Warp ? (e.MetS - e.DurationS, e.MetS) : (e.MetS, e.MetS + e.DurationS);
        // Only stretches nothing covers are shaded: warp on rails is worked out from the orbit (drawn dashed).
        var holes = log.Events.Where(e => e.DurationS > 0 && e.Kind != FlightLogEventKind.Warp).Select(Span).ToList(); // lines break here
        var gaps = holes.Where(h => !filled.Any(f => f.MetS > h.From && f.MetS < h.To)).ToList();

        ChartSeries Series(string name, Color color, Func<FlightSample, int, double> value)
        {
            var segments = new List<IReadOnlyList<Point>>();
            var current = new List<Point>();
            FlightSample? last = null;
            foreach (var i in indexes)
            {
                var x = samples[i];
                if (last is not null && (x.Body != last.Body || holes.Any(h => last.MetS <= h.From + SegmentBreakS && x.MetS >= h.To - SegmentBreakS)
                                         || filled.Any(f => f.Ut > last.Ut && f.Ut < x.Ut)))
                {
                    if (current.Count > 0) segments.Add(current);
                    current = [];
                }
                current.Add(new Point(x.MetS, value(x, i)));
                last = x;
            }
            if (current.Count > 0) segments.Add(current);
            return new ChartSeries(name, color, segments);
        }

        // The path worked out from the orbit across a gap, joined to the samples either side of it (on the same body).
        ChartSeries Filled(Color color, Func<FlightSample, double> value, Func<FilledPoint, double> fill)
        {
            var segments = new List<IReadOnlyList<Point>>();
            for (var k = 1; k < indexes.Count; k++)
            {
                var (a, b) = (samples[indexes[k - 1]], samples[indexes[k]]);
                var between = filled.Where(f => f.Ut > a.Ut && f.Ut < b.Ut).ToList();
                if (between.Count == 0) continue;
                var before = between.Where(f => f.Body == a.Body).Select(f => new Point(f.MetS, fill(f))).Prepend(new Point(a.MetS, value(a))).ToList();
                var after = between.Where(f => f.Body == b.Body).Select(f => new Point(f.MetS, fill(f))).Append(new Point(b.MetS, value(b))).ToList();
                if (a.Body == b.Body) segments.Add([.. before, new Point(b.MetS, value(b))]);
                else
                {
                    if (before.Count > 1) segments.Add(before);
                    if (after.Count > 1) segments.Add(after);
                }
            }
            return new ChartSeries("From the orbit", color, segments) { Dashed = true };
        }

        var (title, series, format) = kind switch
        {
            "Speed" => ("Speed (m/s)", new[] { Series("Surface", Blue, (x, _) => x.SurfaceSpeed), Series("Orbital", Green, (x, _) => x.OrbitalSpeed),
                    Filled(Green, x => x.OrbitalSpeed, f => f.OrbitalSpeed) },
                (Func<double, string>)(y => y.ToString("N0", CultureInfo.CurrentCulture) + " m/s")),
            "Δv used" => ("Δv used (m/s)", [Series("Δv used", Orange, (_, i) => report.DeltaVSoFar[i])], y => y.ToString("N0", CultureInfo.CurrentCulture) + " m/s"),
            "Mass" => ("Mass (t)", [Series("Mass", Purple, (x, _) => x.MassKg / 1000)], y => y.ToString("N2", CultureInfo.CurrentCulture) + " t"),
            "Throttle" => ("Throttle (%)", [Series("Throttle", Orange, (x, _) => x.Throttle * 100)], y => y.ToString("N0", CultureInfo.CurrentCulture) + " %"),
            "Dynamic pressure" => ("Dynamic pressure (kPa)", [Series("Q", Orange, (x, _) => x.DynamicPressurePa / 1000)], y => y.ToString("N1", CultureInfo.CurrentCulture) + " kPa"),
            "G-force" => ("G-force (g)", [Series("g", Orange, (x, _) => x.GForce)], y => y.ToString("N1", CultureInfo.CurrentCulture) + " g"),
            _ => ("Altitude (km)", [Series("Altitude", Blue, (x, _) => x.AltitudeM / 1000), Filled(Blue, x => x.AltitudeM / 1000, f => f.AltitudeM / 1000)], y => y.ToString(Math.Abs(y) >= 100 ? "N0" : "N1", CultureInfo.CurrentCulture) + " km"),
        };
        return new ChartData("MET", title, series, XIsTime: true, format)
        {
            Marks = log.Events.Where(e => e.Kind is FlightLogEventKind.Stage or FlightLogEventKind.Soi or FlightLogEventKind.Situation
                    or FlightLogEventKind.Docked or FlightLogEventKind.Undocked
                    && e.MetS >= from && e.MetS <= to).Select(e => (e.MetS, e.Text)).ToList(),
            Gaps = gaps.Where(g => g.To >= from && g.From <= to).ToList(),
        };
    }

    private static string Ms(double v) => v.ToString("N0", CultureInfo.CurrentCulture) + " m/s";
    private static string Km(double m) => double.IsNaN(m) ? "—" : (m / 1000).ToString("N1", CultureInfo.CurrentCulture) + " km";
    private static string Mmss(double s) => FlightLogs.Duration(s);
}
