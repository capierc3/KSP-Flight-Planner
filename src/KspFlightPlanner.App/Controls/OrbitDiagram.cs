using System.Globalization;
using System.Windows;
using System.Windows.Media;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Core;

namespace KspFlightPlanner.App.Controls;

/// <summary>
/// Top-down schematic of the body, target orbit, carrier orbit and satellite positions.
/// The release point (shared apsis) is drawn at the top.
/// </summary>
public sealed class OrbitDiagram : FrameworkElement
{
    public static readonly DependencyProperty BodyProperty = Register<CelestialBody?>(nameof(Body));
    public static readonly DependencyProperty ResultProperty = Register<ResonantOrbitResult?>(nameof(Result));
    public static readonly DependencyProperty SatelliteCountProperty = Register<int>(nameof(SatelliteCount));
    public static readonly DependencyProperty MinimumLineOfSightAltitudeProperty = Register<double?>(nameof(MinimumLineOfSightAltitude));
    public static readonly DependencyProperty ShowLineOfSightProperty = Register<bool>(nameof(ShowLineOfSight));

    public CelestialBody? Body { get => (CelestialBody?)GetValue(BodyProperty); set => SetValue(BodyProperty, value); }
    public ResonantOrbitResult? Result { get => (ResonantOrbitResult?)GetValue(ResultProperty); set => SetValue(ResultProperty, value); }
    public int SatelliteCount { get => (int)GetValue(SatelliteCountProperty); set => SetValue(SatelliteCountProperty, value); }
    public double? MinimumLineOfSightAltitude { get => (double?)GetValue(MinimumLineOfSightAltitudeProperty); set => SetValue(MinimumLineOfSightAltitudeProperty, value); }
    public bool ShowLineOfSight { get => (bool)GetValue(ShowLineOfSightProperty); set => SetValue(ShowLineOfSightProperty, value); }

    private static DependencyProperty Register<T>(string name) =>
        DependencyProperty.Register(name, typeof(T), typeof(OrbitDiagram),
            new FrameworkPropertyMetadata(default(T), FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Space = Frozen(new SolidColorBrush(Color.FromRgb(0x0B, 0x10, 0x1E)));
    private static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB8)));
    private static readonly Pen SatellitePen = FrozenPen(Color.FromRgb(0x4F, 0xC3, 0xF7), 2);
    private static readonly Pen CarrierPen = FrozenPen(Color.FromRgb(0xFF, 0xB7, 0x4D), 2);
    private static readonly Pen WarningPen = FrozenPen(Color.FromRgb(0xEF, 0x53, 0x50), 2);
    private static readonly Pen SoiPen = FrozenPen(Color.FromArgb(0x80, 0x9A, 0xA4, 0xB8), 1, dashed: true);
    private static readonly Pen SyncPen = FrozenPen(Color.FromArgb(0xA0, 0xBA, 0x68, 0xC8), 1, dashed: true);
    private static readonly Pen LosRingPen = FrozenPen(Color.FromArgb(0xA0, 0x81, 0xC7, 0x84), 1, dashed: true);
    private static readonly Pen LinkPen = FrozenPen(Color.FromArgb(0x90, 0x81, 0xC7, 0x84), 1);
    private static readonly Brush SatelliteFill = Frozen(new SolidColorBrush(Colors.White));

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
        dc.DrawRectangle(Space, null, new Rect(0, 0, w, h));

        if (Body is not { } body)
        {
            dc.Pop();
            return;
        }

        var R = body.EquatorialRadiusM;
        var result = Result;
        var extent = result is null
            ? (R + body.AtmosphereHeightM) * 3
            : Math.Max(result.SatelliteRadiusM, result.CarrierApoapsisAltitudeM + R) * 1.12;
        var scale = Math.Min(w, h) / 2 / extent;
        var centre = new Point(w / 2, h / 2);

        void Circle(double radiusM, Brush? fill, Pen? pen) =>
            dc.DrawEllipse(fill, pen, centre, radiusM * scale, radiusM * scale);

        // Sphere of influence and reference rings
        if (body.SoiRadiusM < extent * 1.5) Circle(body.SoiRadiusM, null, SoiPen);
        if (body.SynchronousRadiusM is { } sync && body.SynchronousAltitudeM is not null) Circle(sync, null, SyncPen);
        if (MinimumLineOfSightAltitude is > 0 and var los) Circle(R + los, null, LosRingPen);

        // Body with atmosphere halo
        var bodyColor = (Color)ColorConverter.ConvertFromString(body.Color);
        if (body.HasAtmosphere)
            Circle(R + body.AtmosphereHeightM, new SolidColorBrush(Color.FromArgb(0x50, bodyColor.R, bodyColor.G, bodyColor.B)), null);
        Circle(R, new SolidColorBrush(bodyColor), null);

        if (result is not null)
        {
            var r = result.SatelliteRadiusM;
            var belowLos = MinimumLineOfSightAltitude is { } minLos && (r - R) * 1.01 < minLos;

            // Target circular orbit
            Circle(r, null, r - R < body.AtmosphereHeightM ? WarningPen : SatellitePen);

            // Carrier ellipse: focus at body centre, shared apsis at the top.
            var a = result.CarrierSemiMajorAxisM;
            var e = result.CarrierEccentricity;
            var c = a * e;
            var b = a * Math.Sqrt(1 - e * e);
            var ellipseCentre = new Point(centre.X, centre.Y + (result.IsDiveOrbit ? -c : c) * scale);
            var carrierPen = result.CarrierPeriapsisAltitudeM < body.AtmosphereHeightM ? WarningPen : CarrierPen;
            dc.DrawEllipse(null, carrierPen, ellipseCentre, b * scale, a * scale);

            // Satellite slots, starting at the release point (top) and spaced 2π/n.
            var n = Math.Max(1, SatelliteCount);
            var positions = Enumerable.Range(0, n)
                .Select(i => i * 2 * Math.PI / n)
                .Select(ang => new Point(centre.X + Math.Sin(ang) * r * scale, centre.Y - Math.Cos(ang) * r * scale))
                .ToArray();

            if (ShowLineOfSight && n >= 3)
            {
                var linkPen = belowLos ? WarningPen : LinkPen;
                for (var i = 0; i < n; i++)
                    dc.DrawLine(linkPen, positions[i], positions[(i + 1) % n]);
            }

            foreach (var p in positions) dc.DrawEllipse(SatelliteFill, null, p, 4, 4);
            dc.DrawEllipse(null, CarrierPen, positions[0], 8, 8); // release point
        }

        DrawLegend(dc);
        dc.Pop();
    }

    private void DrawLegend(DrawingContext dc)
    {
        (Pen pen, string label)[] items =
        [
            (SatellitePen, "Target orbit"),
            (CarrierPen, "Carrier orbit"),
            (SyncPen, "Synchronous orbit"),
            (LosRingPen, "Min. line-of-sight"),
            (SoiPen, "Sphere of influence"),
        ];
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var y = 12.0;
        foreach (var (pen, label) in items)
        {
            dc.DrawLine(pen, new Point(12, y + 7), new Point(32, y + 7));
            var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 11, LabelBrush, dpi);
            dc.DrawText(text, new Point(38, y));
            y += 17;
        }
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness, bool dashed = false)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness);
        if (dashed) pen.DashStyle = new DashStyle([4, 4], 0);
        pen.Freeze();
        return pen;
    }
}
