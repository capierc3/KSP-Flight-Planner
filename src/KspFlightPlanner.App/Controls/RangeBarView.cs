using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace KspFlightPlanner.App.Controls;

/// <summary>
/// A distance as a bar on a shared log scale (fractions 0 to 1 from the view model), with the craft's and the relay's
/// range marked as ticks; or, with <see cref="Ticks"/> set, the scale's labels for a header.
/// </summary>
public sealed class RangeBarView : FrameworkElement
{
    public static readonly DependencyProperty FractionProperty = Register(nameof(Fraction), typeof(double?));
    public static readonly DependencyProperty CraftMarkerProperty = Register(nameof(CraftMarker), typeof(double?));
    public static readonly DependencyProperty RelayMarkerProperty = Register(nameof(RelayMarker), typeof(double?));
    public static readonly DependencyProperty TicksProperty = Register(nameof(Ticks), typeof(IReadOnlyList<(string Label, double Fraction)>));
    public static readonly DependencyProperty ForegroundProperty = Register(nameof(Foreground), typeof(Brush), Brushes.Gray);

    public double? Fraction { get => (double?)GetValue(FractionProperty); set => SetValue(FractionProperty, value); }
    public double? CraftMarker { get => (double?)GetValue(CraftMarkerProperty); set => SetValue(CraftMarkerProperty, value); }
    public double? RelayMarker { get => (double?)GetValue(RelayMarkerProperty); set => SetValue(RelayMarkerProperty, value); }
    public IReadOnlyList<(string Label, double Fraction)>? Ticks
    {
        get => (IReadOnlyList<(string, double)>?)GetValue(TicksProperty);
        set => SetValue(TicksProperty, value);
    }

    /// <summary>Track and label colour (the theme's secondary text).</summary>
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public static readonly Brush CraftBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x4E, 0x79, 0xA7)));
    public static readonly Brush RelayBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0x8E, 0x2B)));
    private static readonly Brush BarBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0x90, 0xA4, 0xAE)));

    private static DependencyProperty Register(string name, Type type, object? fallback = null) =>
        DependencyProperty.Register(name, type, typeof(RangeBarView), new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        if (Ticks is { } ticks)
        {
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            foreach (var (label, at) in ticks)
            {
                var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Foreground, dpi);
                var x = Math.Clamp(at * width - text.Width / 2, 0, width - text.Width);
                dc.DrawText(text, new Point(x, (height - text.Height) / 2));
            }
            return;
        }

        var mid = height / 2;
        var track = new Pen(Foreground, 1) { DashStyle = DashStyles.Dot };
        track.Freeze();
        dc.DrawLine(track, new Point(0, mid), new Point(width, mid));
        if (Fraction is { } f) dc.DrawRoundedRectangle(BarBrush, null, new Rect(0, mid - 4, Math.Max(2, f * width), 8), 2, 2);
        Marker(dc, CraftMarker, CraftBrush, width, height);
        Marker(dc, RelayMarker, RelayBrush, width, height);
    }

    private static void Marker(DrawingContext dc, double? at, Brush brush, double width, double height)
    {
        if (at is not { } f) return;
        var x = Math.Clamp(f * width, 1.5, width - 1.5);
        dc.DrawRectangle(brush, null, new Rect(x - 1.5, 1, 3, height - 2));
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
