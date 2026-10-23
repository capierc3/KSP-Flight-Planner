using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KspFlightPlanner.App.Controls;

/// <summary>One line on a <see cref="LineChartView"/>, in data units. Broken into segments where the data has gaps.</summary>
public sealed record ChartSeries(string Name, Color Color, IReadOnlyList<IReadOnlyList<Point>> Segments)
{
    /// <summary>Drawn dashed: worked out rather than measured.</summary>
    public bool Dashed { get; init; }
}

/// <summary>
/// What a <see cref="LineChartView"/> draws: lines, marked moments (staging…) and stretches without data (warp…).
/// <paramref name="XIsTime"/> labels the x axis as durations (seconds).
/// </summary>
public sealed record ChartData(string XTitle, string YTitle, IReadOnlyList<ChartSeries> Series, bool XIsTime,
    Func<double, string> FormatY)
{
    public IReadOnlyList<(double X, string Label)> Marks { get; init; } = [];
    public IReadOnlyList<(double From, double To)> Gaps { get; init; } = [];
    public Func<double, string>? FormatX { get; init; }
}

/// <summary>A line chart with a hover readout: the values under the mouse and the nearest marked moment.</summary>
public sealed class LineChartView : FrameworkElement
{
    private const double Left = 70, Bottom = 34, Top = 10, Right = 14;

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(ChartData), typeof(LineChartView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(LineChartView), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public ChartData? Data { get => (ChartData?)GetValue(DataProperty); set => SetValue(DataProperty, value); }

    /// <summary>Axis text and grid colour (the theme's secondary text).</summary>
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    private static readonly Brush GapFill = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xB7, 0x4D)));
    private static readonly Pen MarkPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0x90, 0xCA, 0xF9)), 1) { DashStyle = DashStyles.Dash });
    private static readonly Pen HoverPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)), 1));

    private Point? _hover;

    private Rect Area => new(Left, Top, Math.Max(1, ActualWidth - Left - Right), Math.Max(1, ActualHeight - Top - Bottom));

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // hit-testable everywhere
        if (Data is not { } data) return;
        var points = data.Series.SelectMany(s => s.Segments).SelectMany(p => p).ToList();
        if (points.Count == 0) return;
        var area = Area;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var (x0, x1) = (points.Min(p => p.X), points.Max(p => p.X));
        var (y0, y1) = (Math.Min(0, points.Min(p => p.Y)), points.Max(p => p.Y));
        if (y0 < 0 && -y0 < 0.02 * (y1 - y0)) y0 = 0; // a hair below zero (sea level, rounding) isn't worth a step below the axis
        if (x1 <= x0) x1 = x0 + 1;
        var yStep = NiceStep((y1 - y0) / 5);
        y0 = Math.Floor(y0 / yStep + 1e-9) * yStep;
        y1 = Math.Max(y0 + yStep, Math.Ceiling(y1 / yStep) * yStep);
        Point ToScreen(Point p) => new(area.Left + (p.X - x0) / (x1 - x0) * area.Width, area.Bottom - (p.Y - y0) / (y1 - y0) * area.Height);

        var grid = new Pen(Foreground, 0.5) { DashStyle = DashStyles.Dot };
        grid.Freeze();
        for (var y = y0; y <= y1 + yStep / 2; y += yStep)
        {
            var sy = ToScreen(new Point(x0, y)).Y;
            dc.DrawLine(grid, new Point(area.Left, sy), new Point(area.Right, sy));
            Text(dc, data.FormatY(y), new Point(area.Left - 6, sy), TextAlignment.Right, dpi);
        }
        var xStep = data.XIsTime ? TimeStep((x1 - x0) / 6) : NiceStep((x1 - x0) / 6);
        for (var x = Math.Ceiling(x0 / xStep) * xStep; x <= x1; x += xStep)
        {
            var sx = ToScreen(new Point(x, y0)).X;
            dc.DrawLine(grid, new Point(sx, area.Top), new Point(sx, area.Bottom));
            Text(dc, FormatX(data, x), new Point(sx, area.Bottom + 10), TextAlignment.Center, dpi);
        }
        Text(dc, data.XTitle, new Point(area.Left + area.Width / 2, area.Bottom + 26), TextAlignment.Center, dpi);
        var yTitle = Formatted(data.YTitle, dpi);
        dc.PushTransform(new RotateTransform(-90, 12, area.Top + area.Height / 2));
        dc.DrawText(yTitle, new Point(12 - yTitle.Width / 2, area.Top + area.Height / 2 - yTitle.Height / 2));
        dc.Pop();

        dc.PushClip(new RectangleGeometry(area));
        foreach (var (from, to) in data.Gaps)
        {
            var a = ToScreen(new Point(from, y0)).X;
            var b = ToScreen(new Point(to, y0)).X;
            if (b > a) dc.DrawRectangle(GapFill, null, new Rect(a, area.Top, Math.Max(1, b - a), area.Height));
        }
        foreach (var (x, _) in data.Marks)
        {
            var sx = ToScreen(new Point(x, y0)).X;
            dc.DrawLine(MarkPen, new Point(sx, area.Top), new Point(sx, area.Bottom));
        }
        foreach (var series in data.Series)
        {
            var pen = new Pen(new SolidColorBrush(series.Color), 1.6) { LineJoin = PenLineJoin.Round };
            if (series.Dashed) pen.DashStyle = new DashStyle([3, 2], 0);
            pen.Freeze();
            foreach (var segment in series.Segments)
            {
                if (segment.Count == 0) continue;
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(ToScreen(segment[0]), false, false);
                    g.PolyLineTo(Thin(segment.Select(ToScreen), area.Width).ToList(), true, true);
                }
                geometry.Freeze();
                dc.DrawGeometry(null, pen, geometry);
            }
        }
        dc.Pop();

        if (_hover is { } h && area.Contains(h)) DrawReadout(dc, data, area, h, x0, x1, y0, y1, dpi);
    }

    /// <summary>The line under the mouse, each series' value there, and a marked moment close by.</summary>
    private void DrawReadout(DrawingContext dc, ChartData data, Rect area, Point h, double x0, double x1, double y0, double y1, double dpi)
    {
        var x = x0 + (h.X - area.Left) / area.Width * (x1 - x0);
        dc.DrawLine(HoverPen, new Point(h.X, area.Top), new Point(h.X, area.Bottom));
        var lines = new List<string> { $"{data.XTitle}: {FormatX(data, x)}" };
        foreach (var series in data.Series)
        {
            var nearest = series.Segments.SelectMany(p => p).Where(p => Math.Abs(p.X - x) <= (x1 - x0) / 100)
                .OrderBy(p => Math.Abs(p.X - x)).Cast<Point?>().FirstOrDefault();
            if (nearest is not { } p) continue;
            lines.Add($"{(data.Series.Count(x => x.Segments.Count > 0) > 1 ? series.Name + ": " : "")}{data.FormatY(p.Y)}");
            var sp = new Point(area.Left + (p.X - x0) / (x1 - x0) * area.Width, area.Bottom - (p.Y - y0) / (y1 - y0) * area.Height);
            dc.DrawEllipse(new SolidColorBrush(series.Color), null, sp, 3, 3);
        }
        var pxPerX = area.Width / (x1 - x0);
        if (data.Marks.Where(m => Math.Abs(m.X - x) * pxPerX <= 5).OrderBy(m => Math.Abs(m.X - x)).Select(m => m.Label).FirstOrDefault() is { } mark)
            lines.Add(mark);
        if (data.Gaps.Any(g => x >= g.From && x <= g.To)) lines.Add("No data here");

        var ft = Formatted(string.Join("\n", lines), dpi, Brushes.White);
        var at = new Point(h.X + 14 + ft.Width + 8 > area.Right ? h.X - ft.Width - 18 : h.X + 14, Math.Max(area.Top + 2, h.Y - ft.Height - 10));
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xD8, 0x18, 0x1C, 0x22)), null,
            new Rect(at.X - 6, at.Y - 4, ft.Width + 12, ft.Height + 8), 3, 3);
        dc.DrawText(ft, at);
    }

    /// <summary>At most a couple of points per pixel column: long flights have tens of thousands of samples.</summary>
    private static IEnumerable<Point> Thin(IEnumerable<Point> points, double width)
    {
        var lastX = double.NegativeInfinity;
        Point? held = null;
        foreach (var p in points)
        {
            if (p.X - lastX >= 0.5) { if (held is { } k) yield return k; yield return p; lastX = p.X; held = null; }
            else held = p;
        }
        if (held is { } last) yield return last;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _hover = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = null;
        InvalidateVisual();
    }

    private static string FormatX(ChartData data, double x) =>
        data.FormatX?.Invoke(x) ?? (data.XIsTime ? TimeText(x) : x.ToString("N0", CultureInfo.CurrentCulture));

    /// <summary>"45s", "12m", "1h 30m", "2d 3h" (Kerbin days of 6 h).</summary>
    public static string TimeText(double seconds)
    {
        var s = Math.Max(0, seconds);
        if (s < 60) return $"{s:0}s";
        if (s < 3600) return s % 60 < 1 ? $"{s / 60:0}m" : $"{Math.Floor(s / 60):0}m {s % 60:00}s";
        if (s < 21600) return $"{Math.Floor(s / 3600):0}h" + (s % 3600 >= 60 ? $" {Math.Floor(s % 3600 / 60):00}m" : "");
        return $"{Math.Floor(s / 21600):0}d" + (s % 21600 >= 3600 ? $" {Math.Floor(s % 21600 / 3600):0}h" : "");
    }

    /// <summary>1, 2 or 5 × a power of ten at or above <paramref name="rough"/>.</summary>
    private static double NiceStep(double rough)
    {
        if (rough <= 0 || double.IsNaN(rough)) return 1;
        var power = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        return new[] { 1, 2, 5, 10 }.Select(m => m * power).First(step => step >= rough);
    }

    /// <summary>A round time step at or above <paramref name="rough"/> seconds.</summary>
    private static double TimeStep(double rough)
    {
        double[] steps = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400, 216000, 432000];
        return steps.FirstOrDefault(step => step >= rough, NiceStep(rough / 21600) * 21600);
    }

    private void Text(DrawingContext dc, string text, Point anchor, TextAlignment align, double dpi)
    {
        var ft = Formatted(text, dpi);
        var x = align switch { TextAlignment.Right => anchor.X - ft.Width, TextAlignment.Center => anchor.X - ft.Width / 2, _ => anchor.X };
        dc.DrawText(ft, new Point(x, anchor.Y - ft.Height / 2));
    }

    private FormattedText Formatted(string text, double dpi, Brush? brush = null) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, brush ?? Foreground, dpi);

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
