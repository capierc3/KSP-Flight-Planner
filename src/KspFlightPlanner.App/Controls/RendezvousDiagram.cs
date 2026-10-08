using System.Globalization;
using System.Windows;
using System.Windows.Media;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.App.Controls;

/// <summary>
/// Top-down view of one rendezvous leg, looking down on the target's orbital plane (orbits run anticlockwise):
/// your orbit at the start, any phasing orbit, the target's orbit, the transfer arc and a numbered marker at each burn.
/// The meeting point is drawn at the top.
/// </summary>
public sealed class RendezvousDiagram : FrameworkElement
{
    public static readonly DependencyProperty BodyProperty = Register<CelestialBody?>(nameof(Body));
    public static readonly DependencyProperty LegProperty = Register<LegPlan?>(nameof(Leg));
    public static readonly DependencyProperty FirstStepProperty = Register<int>(nameof(FirstStep));

    public CelestialBody? Body { get => (CelestialBody?)GetValue(BodyProperty); set => SetValue(BodyProperty, value); }
    public LegPlan? Leg { get => (LegPlan?)GetValue(LegProperty); set => SetValue(LegProperty, value); }

    /// <summary>The flight plan's number for the leg's first burn, so the markers match the steps.</summary>
    public int FirstStep { get => (int)GetValue(FirstStepProperty); set => SetValue(FirstStepProperty, value); }

    private static DependencyProperty Register<T>(string name) =>
        DependencyProperty.Register(name, typeof(T), typeof(RendezvousDiagram),
            new FrameworkPropertyMetadata(default(T), FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Space = Frozen(new SolidColorBrush(Color.FromRgb(0x0B, 0x10, 0x1E)));
    private static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB8)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE9, 0xF0)));
    private static readonly Color ChaserColor = Color.FromRgb(0xFF, 0xB7, 0x4D);
    private static readonly Color TargetColor = Color.FromRgb(0x4F, 0xC3, 0xF7);
    private static readonly Color TransferColor = Color.FromRgb(0x81, 0xC7, 0x84);
    private static readonly Pen ChaserPen = FrozenPen(ChaserColor, 1.5);
    private static readonly Pen PhasingPen = FrozenPen(ChaserColor, 1.5, dashed: true);
    private static readonly Pen TargetPen = FrozenPen(TargetColor, 1.5);
    private static readonly Pen TransferPen = FrozenPen(TransferColor, 3);
    private static readonly Pen MarkerOutline = FrozenPen(Color.FromRgb(0x0B, 0x10, 0x1E), 1.5);
    private static readonly Brush BurnFill = Frozen(new SolidColorBrush(Color.FromRgb(0xF4, 0x6D, 0x43)));

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
        dc.DrawRectangle(Space, null, new Rect(0, 0, w, h));
        if (Body is not { } body || Leg is not { } leg)
        {
            dc.Pop();
            return;
        }
        var mu = body.Mu;

        // The orbits flown, burn by burn (the last burn, match velocity, leaves you on the target's orbit).
        var flown = new List<(Maneuver Burn, OrbitalElements Before, OrbitalElements After)>();
        var orbit = leg.StartOrbit;
        foreach (var burn in leg.Maneuvers.Where(m => m.Kind != ManeuverKind.MatchVelocity))
        {
            var after = Rendezvous.OrbitAfter(burn, orbit, mu);
            flown.Add((burn, orbit, after));
            orbit = after;
        }
        var match = leg.Maneuvers.Last(m => m.Kind == ManeuverKind.MatchVelocity);
        var transfer = flown.LastOrDefault(f => f.Burn.Kind == ManeuverKind.Transfer);
        var phasing = flown.FirstOrDefault(f => f.Burn.Kind == ManeuverKind.Phasing);
        var startOrbit = flown.FirstOrDefault(f => f.Burn.Kind == ManeuverKind.PlaneChange).After ?? leg.StartOrbit;

        // Frame: looking down on the target's plane, the meeting point straight up.
        var meet = Kepler.StateAt(leg.Target.Orbit, mu, leg.ArrivalUt);
        var normal = Vector3d.Cross(meet.Position, meet.Velocity).Normalized;
        var up = meet.Position.Normalized;
        var left = Vector3d.Cross(normal, up); // 90° ahead along the orbit: anticlockwise on screen

        var extent = new[] { leg.StartOrbit, leg.Target.Orbit, orbit, phasing.After ?? orbit }
            .Max(o => o.SemiMajorAxisM * (1 + o.Eccentricity));
        var margin = 34.0;
        var scale = (Math.Min(w, h) / 2 - margin) / extent;
        var centre = new Point(w / 2, h / 2 + 10);
        Point Screen(Vector3d p) => new(centre.X - Vector3d.Dot(p, left) * scale, centre.Y - Vector3d.Dot(p, up) * scale);

        void Circle(double radius, Brush? fill) => dc.DrawEllipse(fill, null, centre, radius * scale, radius * scale);

        // Body and atmosphere.
        var bodyColor = (Color)ColorConverter.ConvertFromString(body.Color);
        if (body.HasAtmosphere)
            Circle(body.EquatorialRadiusM + body.AtmosphereHeightM, new SolidColorBrush(Color.FromArgb(0x50, bodyColor.R, bodyColor.G, bodyColor.B)));
        Circle(body.EquatorialRadiusM, new SolidColorBrush(bodyColor));

        // Orbits: whole loops for the start, phasing and target orbits; only the flown arc of the transfer.
        Path(dc, startOrbit, mu, startOrbit.EpochS, Kepler.OrbitalPeriod(startOrbit, mu), Screen, ChaserPen);
        if (phasing.After is { } p) Path(dc, p, mu, phasing.Burn.Ut, Kepler.OrbitalPeriod(p, mu), Screen, PhasingPen);
        Path(dc, leg.Target.Orbit, mu, leg.ArrivalUt, Kepler.OrbitalPeriod(leg.Target.Orbit, mu), Screen, TargetPen);
        if (transfer.After is { } t) Path(dc, t, mu, transfer.Burn.Ut, match.Ut - transfer.Burn.Ut, Screen, TransferPen);

        // Where you and the target are at the start, and the target when you burn for the transfer.
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Craft(dc, Screen(Kepler.StateAt(leg.StartOrbit, mu, leg.StartUt).Position), centre, ChaserColor, "you now", dpi);
        Craft(dc, Screen(Kepler.StateAt(leg.Target.Orbit, mu, leg.StartUt).Position), centre, TargetColor, "target now", dpi);
        if (transfer.After is not null)
            Craft(dc, Screen(Kepler.StateAt(leg.Target.Orbit, mu, transfer.Burn.Ut).Position), centre, TargetColor, "target at transfer burn", dpi, hollow: true);

        // Burn markers, numbered like the flight plan; the meeting point last. Burns at the same spot (a phasing lap ends
        // where it began) share one marker: "1·2·4".
        var markers = new List<(Point At, List<int> Numbers)>();
        var number = FirstStep;
        var points = flown.Select(f => Screen(Kepler.StateAt(f.Before, mu, f.Burn.Ut).Position)).Append(Screen(meet.Position));
        foreach (var at in points)
        {
            var same = markers.FindIndex(m => (m.At - at).Length < 14);
            if (same >= 0) markers[same].Numbers.Add(number++);
            else markers.Add((at, [number++]));
        }
        foreach (var (at, numbers) in markers) Marker(dc, at, string.Join("·", numbers), dpi);

        DrawLegend(dc, dpi, phasing.After is not null, transfer.After is not null);
        dc.Pop();
    }

    /// <summary>The orbit's path from <paramref name="fromUt"/> over <paramref name="durationS"/> seconds, projected on the view plane.</summary>
    private static void Path(DrawingContext dc, OrbitalElements orbit, double mu, double fromUt, double durationS, Func<Vector3d, Point> screen, Pen pen)
    {
        const int segments = 240;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (var i = 0; i <= segments; i++)
            {
                var point = screen(Kepler.StateAt(orbit, mu, fromUt + durationS * i / segments).Position);
                if (i == 0) ctx.BeginFigure(point, false, false);
                else ctx.LineTo(point, true, false);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    /// <summary>A craft's position with its label on the side away from the planet, flipped inward if it would run off the edge.</summary>
    private void Craft(DrawingContext dc, Point at, Point centre, Color color, string label, double dpi, bool hollow = false)
    {
        var brush = new SolidColorBrush(color);
        dc.DrawEllipse(hollow ? null : brush, hollow ? new Pen(brush, 1.5) : MarkerOutline, at, 5, 5);
        var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10.5, LabelBrush, dpi);
        var right = at.X >= centre.X;
        if (right && at.X + 8 + text.Width > ActualWidth) right = false;
        else if (!right && at.X - 8 - text.Width < 0) right = true;
        dc.DrawText(text, new Point(right ? at.X + 8 : at.X - 8 - text.Width, at.Y - text.Height / 2));
    }

    /// <summary>A round marker with the step number, stretched into a pill when it holds several.</summary>
    private static void Marker(DrawingContext dc, Point at, string label, double dpi)
    {
        var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 11, TextBrush, dpi);
        var halfWidth = Math.Max(9, text.Width / 2 + 5);
        dc.DrawRoundedRectangle(BurnFill, MarkerOutline, new Rect(at.X - halfWidth, at.Y - 9, 2 * halfWidth, 18), 9, 9);
        dc.DrawText(text, new Point(at.X - text.Width / 2, at.Y - text.Height / 2));
    }

    private void DrawLegend(DrawingContext dc, double dpi, bool phasing, bool transfer)
    {
        var items = new List<(Pen Pen, string Label)> { (ChaserPen, "Your orbit"), (TargetPen, "Target's orbit") };
        if (phasing) items.Add((PhasingPen, "Phasing orbit"));
        if (transfer) items.Add((TransferPen, "Transfer"));
        var y = 10.0;
        foreach (var (pen, label) in items)
        {
            dc.DrawLine(pen, new Point(12, y + 7), new Point(32, y + 7));
            var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, LabelBrush, dpi);
            dc.DrawText(text, new Point(38, y));
            y += 17;
        }
        dc.DrawEllipse(BurnFill, MarkerOutline, new Point(22, y + 7), 6, 6);
        dc.DrawText(new FormattedText("Burn (step number)", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, LabelBrush, dpi),
            new Point(38, y));
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
