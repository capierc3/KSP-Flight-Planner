using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KspFlightPlanner.Core;

namespace KspFlightPlanner.App.Controls;

/// <summary>
/// Heat map of total Δv by departure date (x) and flight time (y, increasing upward).
/// Click or drag to pick a transfer; the selected transfer is marked with a crosshair.
/// </summary>
public sealed class PorkchopPlotView : FrameworkElement
{
    /// <summary>Colour range spans the cheapest transfer up to this multiple of it.</summary>
    public const double ScaleMultiple = 3.0;

    public static readonly DependencyProperty PlotProperty = DependencyProperty.Register(
        nameof(Plot), typeof(PorkchopPlot), typeof(PorkchopPlotView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((PorkchopPlotView)d)._bitmap = null));

    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(
        nameof(Selected), typeof(IPorkchopPoint), typeof(PorkchopPlotView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public PorkchopPlot? Plot { get => (PorkchopPlot?)GetValue(PlotProperty); set => SetValue(PlotProperty, value); }
    public IPorkchopPoint? Selected { get => (IPorkchopPoint?)GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }

    /// <summary>Raised with (departure UT, flight time seconds) when the user picks a point.</summary>
    public event EventHandler<(double DepartureUt, double FlightTimeS)>? PointPicked;

    private BitmapSource? _bitmap;

    private static readonly Pen MarkerPen = Frozen(new Pen(Brushes.White, 1.5));
    private static readonly Pen MarkerShadow = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)), 3.5));
    private static readonly Brush Empty = Frozen(new SolidColorBrush(Color.FromRgb(0x0B, 0x10, 0x1E)));

    // Diverging ramp: cheap = deep blue, expensive = dark red.
    private static readonly (double At, Color Color)[] Ramp =
    [
        (0.00, Color.FromRgb(49, 54, 149)),
        (0.15, Color.FromRgb(69, 117, 180)),
        (0.30, Color.FromRgb(116, 173, 209)),
        (0.45, Color.FromRgb(171, 217, 233)),
        (0.60, Color.FromRgb(254, 224, 144)),
        (0.75, Color.FromRgb(253, 174, 97)),
        (0.88, Color.FromRgb(244, 109, 67)),
        (1.00, Color.FromRgb(165, 0, 38)),
    ];

    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Empty, null, rect);
        if (Plot is not { } plot) return;

        _bitmap ??= BuildBitmap(plot);
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        dc.DrawImage(_bitmap, rect);

        if (Selected is { } w)
        {
            var x = (w.DepartureUt - plot.DepartureStartUt) / (plot.DepartureEndUt - plot.DepartureStartUt) * rect.Width;
            var y = rect.Height - (w.FlightTimeS - plot.FlightTimeMinS) / (plot.FlightTimeMaxS - plot.FlightTimeMinS) * rect.Height;
            var p = new Point(x, y);
            foreach (var pen in new[] { MarkerShadow, MarkerPen })
            {
                dc.DrawLine(pen, new Point(x - 12, y), new Point(x - 4, y));
                dc.DrawLine(pen, new Point(x + 4, y), new Point(x + 12, y));
                dc.DrawLine(pen, new Point(x, y - 12), new Point(x, y - 4));
                dc.DrawLine(pen, new Point(x, y + 4), new Point(x, y + 12));
                dc.DrawEllipse(null, pen, p, 4, 4);
            }
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        Pick(e.GetPosition(this));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) Pick(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();

    private void Pick(Point p)
    {
        if (Plot is not { } plot || ActualWidth <= 0 || ActualHeight <= 0) return;
        var fx = Math.Clamp(p.X / ActualWidth, 0, 1);
        var fy = Math.Clamp(1 - p.Y / ActualHeight, 0, 1);
        PointPicked?.Invoke(this, (
            plot.DepartureStartUt + fx * (plot.DepartureEndUt - plot.DepartureStartUt),
            plot.FlightTimeMinS + fy * (plot.FlightTimeMaxS - plot.FlightTimeMinS)));
    }

    private static BitmapSource BuildBitmap(PorkchopPlot plot)
    {
        var min = plot.TotalDv.Where(double.IsFinite).DefaultIfEmpty(1).Min();
        var logRange = Math.Log(ScaleMultiple);
        var pixels = new int[plot.Columns * plot.Rows];

        for (var r = 0; r < plot.Rows; r++)
        for (var c = 0; c < plot.Columns; c++)
        {
            var v = plot[c, r];
            var color = double.IsFinite(v)
                ? Sample(Math.Clamp(Math.Log(v / min) / logRange, 0, 1))
                : Color.FromRgb(0x20, 0x20, 0x28);
            var row = plot.Rows - 1 - r; // flight time increases upward
            pixels[row * plot.Columns + c] = (255 << 24) | (color.R << 16) | (color.G << 8) | color.B;
        }

        var bmp = BitmapSource.Create(plot.Columns, plot.Rows, 96, 96, PixelFormats.Bgra32, null, pixels, plot.Columns * 4);
        bmp.Freeze();
        return bmp;
    }

    private static Color Sample(double t)
    {
        for (var i = 1; i < Ramp.Length; i++)
        {
            if (t > Ramp[i].At) continue;
            var (a0, c0) = Ramp[i - 1];
            var (a1, c1) = Ramp[i];
            var f = (t - a0) / (a1 - a0);
            return Color.FromRgb(
                (byte)(c0.R + (c1.R - c0.R) * f),
                (byte)(c0.G + (c1.G - c0.G) * f),
                (byte)(c0.B + (c1.B - c0.B) * f));
        }
        return Ramp[^1].Color;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
