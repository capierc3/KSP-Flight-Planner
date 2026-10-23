using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KspFlightPlanner.App.ViewModels;

namespace KspFlightPlanner.App.Controls;

/// <summary>
/// The engine chart: payload across (log scale), Δv up, each area coloured by the engine that wins there. The inputs'
/// payload and Δv are marked; hovering names the winner, and a click (or drag) picks that payload and Δv.
/// </summary>
public sealed class EngineChartPlotView : FrameworkElement
{
    private const double Left = 64, Bottom = 34, Top = 8, Right = 12;

    public static readonly DependencyProperty PlotProperty = DependencyProperty.Register(
        nameof(Plot), typeof(EngineChartPlot), typeof(EngineChartPlotView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((EngineChartPlotView)d)._bitmap = null));

    public static readonly DependencyProperty MarkerProperty = DependencyProperty.Register(
        nameof(Marker), typeof((double, double)?), typeof(EngineChartPlotView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(EngineChartPlotView),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public EngineChartPlot? Plot { get => (EngineChartPlot?)GetValue(PlotProperty); set => SetValue(PlotProperty, value); }

    /// <summary>The payload (t) and Δv (m/s) to mark.</summary>
    public (double PayloadT, double DeltaV)? Marker { get => ((double, double)?)GetValue(MarkerProperty); set => SetValue(MarkerProperty, value); }

    /// <summary>Axis text and grid colour (the theme's secondary text).</summary>
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    /// <summary>A point was picked: (payload t, Δv m/s).</summary>
    public event Action<double, double>? PointPicked;

    private BitmapSource? _bitmap;
    private Point? _hover;

    private static readonly Pen MarkerPen = Frozen(new Pen(Brushes.White, 1.5));
    private static readonly Pen MarkerShadow = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)), 3.5));
    private static readonly Color Nothing = Color.FromRgb(0x30, 0x30, 0x36);

    private Rect Area => new(Left, Top, Math.Max(1, ActualWidth - Left - Right), Math.Max(1, ActualHeight - Top - Bottom));

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // hit-testable everywhere
        if (Plot is not { } plot) return;
        var area = Area;
        _bitmap ??= BuildBitmap(plot);
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        dc.DrawImage(_bitmap, area);

        var grid = new Pen(Foreground, 0.5) { DashStyle = DashStyles.Dot };
        grid.Freeze();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Δv: every 500 or 1,000 m/s
        var step = plot.DeltaVMax <= 4000 ? 500 : 1000;
        for (var dv = 0.0; dv <= plot.DeltaVMax + 1; dv += step)
        {
            var y = area.Bottom - dv / plot.DeltaVMax * area.Height;
            dc.DrawLine(grid, new Point(area.Left, y), new Point(area.Right, y));
            Text(dc, dv.ToString("N0", CultureInfo.CurrentCulture), new Point(area.Left - 6, y), TextAlignment.Right, dpi);
        }
        // Payload: 1-2-5 steps on the log scale
        foreach (var t in new[] { 0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000 })
        {
            if (t < plot.PayloadMinT - 1e-9 || t > plot.PayloadMaxT + 1e-9) continue;
            var x = area.Left + plot.FractionOf(t) * area.Width;
            dc.DrawLine(grid, new Point(x, area.Top), new Point(x, area.Bottom));
            Text(dc, t.ToString("0.#", CultureInfo.CurrentCulture), new Point(x, area.Bottom + 10), TextAlignment.Center, dpi);
        }
        Text(dc, "Payload (t)", new Point(area.Left + area.Width / 2, area.Bottom + 26), TextAlignment.Center, dpi);
        var label = Formatted("Δv (m/s)", dpi);
        dc.PushTransform(new RotateTransform(-90, 12, area.Top + area.Height / 2));
        dc.DrawText(label, new Point(12 - label.Width / 2, area.Top + area.Height / 2 - label.Height / 2));
        dc.Pop();

        if (Marker is { } m && m.PayloadT >= plot.PayloadMinT && m.PayloadT <= plot.PayloadMaxT && m.DeltaV <= plot.DeltaVMax)
        {
            var p = new Point(area.Left + plot.FractionOf(m.PayloadT) * area.Width, area.Bottom - m.DeltaV / plot.DeltaVMax * area.Height);
            foreach (var pen in new[] { MarkerShadow, MarkerPen })
            {
                dc.DrawLine(pen, new Point(p.X - 12, p.Y), new Point(p.X - 4, p.Y));
                dc.DrawLine(pen, new Point(p.X + 4, p.Y), new Point(p.X + 12, p.Y));
                dc.DrawLine(pen, new Point(p.X, p.Y - 12), new Point(p.X, p.Y - 4));
                dc.DrawLine(pen, new Point(p.X, p.Y + 4), new Point(p.X, p.Y + 12));
                dc.DrawEllipse(null, pen, p, 4, 4);
            }
        }

        // What wins under the mouse
        if (_hover is { } h && area.Contains(h) && Cell(h) is { } cell)
        {
            var winner = plot.Winner(cell.Column, cell.Row);
            var text = (winner < 0 ? "Nothing can" : $"{plot.Legend[winner].Title} × {plot.Count(cell.Column, cell.Row)}") +
                       $"\n{cell.PayloadT.ToString(cell.PayloadT < 10 ? "0.##" : "0.#", CultureInfo.CurrentCulture)} t · {cell.DeltaV:N0} m/s";
            var ft = Formatted(text, dpi, Brushes.White);
            var at = new Point(Math.Min(h.X + 14, area.Right - ft.Width - 8), Math.Max(h.Y - ft.Height - 10, area.Top + 2));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xD8, 0x18, 0x1C, 0x22)), null,
                new Rect(at.X - 6, at.Y - 4, ft.Width + 12, ft.Height + 8), 3, 3);
            dc.DrawText(ft, at);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _hover = e.GetPosition(this);
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) Pick(_hover.Value);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        Pick(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();

    private void Pick(Point p)
    {
        if (Cell(p) is { } cell) PointPicked?.Invoke(cell.PayloadT, cell.DeltaV);
    }

    /// <summary>The cell under a point, with the payload and Δv there (clamped to the chart).</summary>
    private (int Column, int Row, double PayloadT, double DeltaV)? Cell(Point p)
    {
        if (Plot is not { } plot) return null;
        var area = Area;
        var fx = Math.Clamp((p.X - area.Left) / area.Width, 0, 1);
        var fy = Math.Clamp((area.Bottom - p.Y) / area.Height, 0, 1);
        return (Math.Min(plot.Columns - 1, (int)(fx * plot.Columns)), Math.Min(plot.Rows - 1, (int)(fy * plot.Rows)),
            plot.PayloadAt(fx), fy * plot.DeltaVMax);
    }

    private void Text(DrawingContext dc, string text, Point anchor, TextAlignment align, double dpi)
    {
        var ft = Formatted(text, dpi);
        var x = align switch { TextAlignment.Right => anchor.X - ft.Width, TextAlignment.Center => anchor.X - ft.Width / 2, _ => anchor.X };
        dc.DrawText(ft, new Point(x, anchor.Y - ft.Height / 2));
    }

    private FormattedText Formatted(string text, double dpi, Brush? brush = null) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, brush ?? Foreground, dpi);

    private static BitmapSource BuildBitmap(EngineChartPlot plot)
    {
        var pixels = new int[plot.Columns * plot.Rows];
        for (var r = 0; r < plot.Rows; r++)
        for (var c = 0; c < plot.Columns; c++)
        {
            var winner = plot.Winner(c, r);
            var color = winner < 0 ? Nothing : plot.Colors[winner];
            pixels[(plot.Rows - 1 - r) * plot.Columns + c] = (255 << 24) | (color.R << 16) | (color.G << 8) | color.B; // Δv up
        }
        var bmp = BitmapSource.Create(plot.Columns, plot.Rows, 96, 96, PixelFormats.Bgra32, null, pixels, plot.Columns * 4);
        bmp.Freeze();
        return bmp;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
