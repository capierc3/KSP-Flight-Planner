using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.App.Controls;

/// <summary>
/// The flight from above a body: the body, its moons' orbits and where they were, and the craft's path (burns orange,
/// coasting blue, worked out from the orbit dashed). Wheel zooms on the mouse, dragging pans, a double-click fits it again.
/// </summary>
public sealed class FlightMapCanvas : FrameworkElement
{
    public static readonly DependencyProperty MapProperty = DependencyProperty.Register(
        nameof(Map), typeof(FlightMapView), typeof(FlightMapCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((FlightMapCanvas)d)._fitted = false));

    public FlightMapView? Map { get => (FlightMapView?)GetValue(MapProperty); set => SetValue(MapProperty, value); }

    private static readonly Brush Space = Frozen(new SolidColorBrush(Color.FromRgb(0x0B, 0x10, 0x1E)));
    private static readonly Brush BodyFill = Frozen(new SolidColorBrush(Color.FromRgb(0x3A, 0x6E, 0x9E)));
    private static readonly Brush MoonFill = Frozen(new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0x98)));
    private static readonly Brush Air = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0x64, 0xB5, 0xF6)));
    private static readonly Brush Label = Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB8)));
    private static readonly Pen MoonOrbitPen = FrozenPen(Color.FromArgb(0x70, 0x9A, 0xA4, 0xB8), 1, [4, 4]);
    private static readonly Pen SoiPen = FrozenPen(Color.FromArgb(0x50, 0x9A, 0xA4, 0xB8), 1, [2, 3]);
    private static readonly Pen CoastPen = FrozenPen(Color.FromRgb(0x64, 0xB5, 0xF6), 1.6);
    private static readonly Pen PoweredPen = FrozenPen(Color.FromRgb(0xFF, 0xB7, 0x4D), 2.4);
    private static readonly Pen FilledPen = FrozenPen(Color.FromRgb(0x64, 0xB5, 0xF6), 1.4, [3, 2]);
    private static readonly Pen ScalePen = FrozenPen(Color.FromRgb(0x9A, 0xA4, 0xB8), 1);

    private bool _fitted;
    private double _scale = 1;           // pixels per metre
    private Point _origin;               // screen position of the body's centre
    private Point? _dragFrom;
    private Point? _hover;

    protected override void OnRender(DrawingContext dc)
    {
        var size = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Space, null, size);
        if (Map is not { } map || ActualWidth < 10 || ActualHeight < 10) return;
        if (!_fitted) Fit(map);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.PushClip(new RectangleGeometry(size));

        // The body (and its air), the moons' orbits, and each moon where it was when the craft came and went.
        if (map.AtmosphereM > 0) dc.DrawEllipse(Air, null, _origin, (map.RadiusM + map.AtmosphereM) * _scale, (map.RadiusM + map.AtmosphereM) * _scale);
        dc.DrawEllipse(BodyFill, null, _origin, Math.Max(2, map.RadiusM * _scale), Math.Max(2, map.RadiusM * _scale));
        Text(dc, map.Body, new Point(_origin.X, _origin.Y), dpi, center: true);
        foreach (var moon in map.Moons)
        {
            dc.DrawGeometry(null, MoonOrbitPen, Line(moon.Orbit.Select(p => ToScreen(p.X, p.Y))));
            foreach (var (_, x, y) in moon.Seen)
            {
                var c = ToScreen(x, y);
                dc.DrawEllipse(null, SoiPen, c, moon.SoiRadiusM * _scale, moon.SoiRadiusM * _scale);
                dc.DrawEllipse(MoonFill, null, c, Math.Max(3, moon.RadiusM * _scale), Math.Max(3, moon.RadiusM * _scale));
            }
            if (moon.Seen.Count > 0) Text(dc, moon.Name, ToScreen(moon.Seen[0].X, moon.Seen[0].Y) + new Vector(Math.Max(4, moon.RadiusM * _scale) + 4, -8), dpi);
        }

        // The path.
        foreach (var segment in map.Segments)
        {
            var pen = segment[^1].Leg switch { MapLeg.Powered => PoweredPen, MapLeg.Filled => FilledPen, _ => CoastPen };
            dc.DrawGeometry(null, pen, Line(segment.Select(p => ToScreen(p.X, p.Y))));
        }
        foreach (var mark in map.Marks)
        {
            var p = ToScreen(mark.X, mark.Y);
            dc.DrawEllipse(Brushes.White, null, p, 3.5, 3.5);
            Text(dc, mark.Label, p + new Vector(6, -6), dpi);
        }

        DrawScale(dc, dpi);
        if (_hover is { } h) DrawReadout(dc, map, h, dpi);
        dc.Pop();
    }

    /// <summary>The nearest path point under the mouse: when, where and how high.</summary>
    private void DrawReadout(DrawingContext dc, FlightMapView map, Point h, double dpi)
    {
        MapPoint? best = null;
        var bestD = 12.0;
        foreach (var p in map.Segments.SelectMany(s => s))
        {
            var d = (ToScreen(p.X, p.Y) - h).Length;
            if (d < bestD) (best, bestD) = (p, d);
        }
        string? text = null;
        Point at = h;
        if (best is { } b)
        {
            at = ToScreen(b.X, b.Y);
            dc.DrawEllipse(Brushes.White, null, at, 3, 3);
            text = $"MET {LineChartView.TimeText(b.MetS)}\n{b.AltitudeM / 1000:N1} km above {b.Body}" +
                   (b.Leg switch { MapLeg.Powered => "\nburning", MapLeg.Filled => "\nworked out from the orbit", _ => "" });
        }
        else
        {
            foreach (var moon in map.Moons)
                foreach (var (label, x, y) in moon.Seen)
                    if ((ToScreen(x, y) - h).Length <= Math.Max(8, moon.RadiusM * _scale)) text = label;
        }
        if (text is null) return;
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.White, dpi);
        var box = new Point(Math.Min(at.X + 12, ActualWidth - ft.Width - 10), Math.Max(4, at.Y - ft.Height - 10));
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xD8, 0x18, 0x1C, 0x22)), null, new Rect(box.X - 6, box.Y - 4, ft.Width + 12, ft.Height + 8), 3, 3);
        dc.DrawText(ft, box);
    }

    /// <summary>A round distance about a fifth of the width, bottom left.</summary>
    private void DrawScale(DrawingContext dc, double dpi)
    {
        var metres = ActualWidth / 5 / _scale;
        var power = Math.Pow(10, Math.Floor(Math.Log10(metres)));
        var nice = new[] { 1, 2, 5 }.Select(m => m * power).Last(v => v <= metres);
        var px = nice * _scale;
        var y = ActualHeight - 16;
        dc.DrawLine(ScalePen, new Point(12, y), new Point(12 + px, y));
        dc.DrawLine(ScalePen, new Point(12, y - 4), new Point(12, y + 4));
        dc.DrawLine(ScalePen, new Point(12 + px, y - 4), new Point(12 + px, y + 4));
        Text(dc, nice >= 1000 ? $"{nice / 1000:N0} km" : $"{nice:N0} m", new Point(16 + px, y - 8), dpi);
    }

    /// <summary>Fits the path (and the body) in view.</summary>
    private void Fit(FlightMapView map)
    {
        var xs = map.Segments.SelectMany(s => s).Select(p => p.X).Append(-map.RadiusM).Append(map.RadiusM).ToList();
        var ys = map.Segments.SelectMany(s => s).Select(p => p.Y).Append(-map.RadiusM).Append(map.RadiusM).ToList();
        var (x0, x1, y0, y1) = (xs.Min(), xs.Max(), ys.Min(), ys.Max());
        _scale = 0.9 * Math.Min(ActualWidth / Math.Max(1, x1 - x0), ActualHeight / Math.Max(1, y1 - y0));
        // Up on the screen is +Y on the map.
        _origin = new Point(ActualWidth / 2 - (x0 + x1) / 2 * _scale, ActualHeight / 2 + (y0 + y1) / 2 * _scale);
        _fitted = true;
    }

    private Point ToScreen(double x, double y) => new(_origin.X + x * _scale, _origin.Y - y * _scale);

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        _fitted = false;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var at = e.GetPosition(this);
        var factor = e.Delta > 0 ? 1.25 : 1 / 1.25;
        _origin = at + (_origin - at) * factor;
        _scale *= factor;
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _fitted = false;
            InvalidateVisual();
            return;
        }
        _dragFrom = e.GetPosition(this);
        CaptureMouse();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragFrom = null;
        ReleaseMouseCapture();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var at = e.GetPosition(this);
        if (_dragFrom is { } from && e.LeftButton == MouseButtonState.Pressed)
        {
            _origin += at - from;
            _dragFrom = at;
        }
        _hover = at;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = null;
        InvalidateVisual();
    }

    private static StreamGeometry Line(IEnumerable<Point> points)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var first = true;
            var last = new Point(double.NaN, double.NaN);
            foreach (var p in points)
            {
                if (first) { g.BeginFigure(p, false, false); first = false; last = p; continue; }
                if ((p - last).LengthSquared < 0.25) continue; // under half a pixel apart
                g.LineTo(p, true, true);
                last = p;
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private static void Text(DrawingContext dc, string text, Point at, double dpi, bool center = false)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Label, dpi);
        dc.DrawText(ft, center ? new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2) : at);
    }

    private static Pen FrozenPen(Color color, double thickness, double[]? dashes = null)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness) { LineJoin = PenLineJoin.Round };
        if (dashes is not null) pen.DashStyle = new DashStyle(dashes, 0);
        pen.Freeze();
        return pen;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
