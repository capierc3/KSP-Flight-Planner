using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using KspFlightPlanner.Core;

namespace KspFlightPlanner.App.Controls;

/// <summary>
/// Subway-style drawing of the Δv map: a tree from Kerbin's surface (left) outward.
/// Edge labels are editable; clicking a stop raises <see cref="NodeClicked"/>.
/// The current route is highlighted and stops get numbered badges.
/// </summary>
public sealed class DeltaVMapView : Canvas
{
    public static readonly DependencyProperty MapProperty = Register<DeltaVMap?>(nameof(Map));
    public static readonly DependencyProperty RouteEdgeKeysProperty = Register<IReadOnlyCollection<string>?>(nameof(RouteEdgeKeys));
    public static readonly DependencyProperty StopKeysProperty = Register<IReadOnlyList<string>?>(nameof(StopKeys));

    public DeltaVMap? Map { get => (DeltaVMap?)GetValue(MapProperty); set => SetValue(MapProperty, value); }
    public IReadOnlyCollection<string>? RouteEdgeKeys { get => (IReadOnlyCollection<string>?)GetValue(RouteEdgeKeysProperty); set => SetValue(RouteEdgeKeysProperty, value); }
    public IReadOnlyList<string>? StopKeys { get => (IReadOnlyList<string>?)GetValue(StopKeysProperty); set => SetValue(StopKeysProperty, value); }

    /// <summary>A stop was clicked.</summary>
    public event EventHandler<DvMapNode>? NodeClicked;

    /// <summary>An edge value was edited: (child node key, new Δv or null to restore the map value).</summary>
    public event EventHandler<(string Key, double? DeltaV)>? EdgeEdited;

    private static DependencyProperty Register<T>(string name) =>
        DependencyProperty.Register(name, typeof(T), typeof(DeltaVMapView),
            new PropertyMetadata(default(T), (d, _) => ((DeltaVMapView)d).Rebuild()));

    private const double ColumnWidth = 150;
    private const double RowHeight = 64;
    private const double Margin0 = 36;
    private const double TrunkOffset = 30;

    private static readonly Dictionary<string, Color> BodyColors = new()
    {
        ["Kerbin"] = C("#3D8BFF"), ["Mun"] = C("#A0A0A0"), ["Minmus"] = C("#5FD3A6"), ["Kerbol"] = C("#F2B705"),
        ["Moho"] = C("#D2955F"), ["Eve"] = C("#8E63D6"), ["Gilly"] = C("#B39DDB"), ["Duna"] = C("#FF5A2A"),
        ["Ike"] = C("#FF9A70"), ["Dres"] = C("#BDBDBD"), ["Jool"] = C("#7CC242"), ["Laythe"] = C("#A6D96A"),
        ["Vall"] = C("#8BC34A"), ["Tylo"] = C("#6FA83A"), ["Bop"] = C("#5C8F35"), ["Pol"] = C("#4C7A2A"),
        ["Eeloo"] = C("#B0BEC5"),
    };

    private static readonly Brush Background0 = Frozen(new SolidColorBrush(C("#0B101E")));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(C("#E6E9F0")));
    private static readonly Brush DimText = Frozen(new SolidColorBrush(C("#8A93A6")));
    private static readonly Brush EditedBrush = Frozen(new SolidColorBrush(C("#FFB74D")));

    /// <summary>A bare TextBox template (no theme chrome such as the clear button) for the small edge labels.</summary>
    private static readonly ControlTemplate PlainTextBox = CreatePlainTextBoxTemplate();

    private static ControlTemplate CreatePlainTextBoxTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        border.AppendChild(host);
        var template = new ControlTemplate(typeof(TextBox)) { VisualTree = border };
        template.Seal();
        return template;
    }

    public DeltaVMapView()
    {
        Background = Background0;
        ClipToBounds = true;
    }

    private void Rebuild()
    {
        Children.Clear();
        if (Map is not { } map) return;

        var route = RouteEdgeKeys is { Count: > 0 } r ? r.ToHashSet() : null;
        var positions = Layout(map, out var width, out var height);
        Width = width;
        Height = height;

        // Edges first so nodes draw on top.
        foreach (var node in map.Nodes)
        {
            if (map.ParentOf(node) is not { } parent) continue;
            var p = positions[parent.Key];
            var c = positions[node.Key];
            var onRoute = route?.Contains(node.Key) ?? true;
            var color = ColorOf(node.BodyName);
            var trunkX = p.X + TrunkOffset;

            var line = new Polyline
            {
                Points = [p, new Point(trunkX, p.Y), new Point(trunkX, c.Y), c],
                Stroke = new SolidColorBrush(color),
                StrokeThickness = onRoute && route is not null ? 7 : 5,
                Opacity = onRoute ? 1 : 0.28,
                StrokeLineJoin = PenLineJoin.Round,
            };
            Children.Add(line);
            AddEdgeLabel(node, new Point((trunkX + c.X) / 2, c.Y), color, onRoute);
        }

        var stopNumbers = StopNumbers();
        foreach (var node in map.Nodes)
        {
            var pos = positions[node.Key];
            var onRoute = route is null || route.Contains(node.Key)
                || map.ChildrenOf(node).Any(ch => route.Contains(ch.Key))
                || (StopKeys?.Contains(node.Key) ?? false);
            AddNode(node, pos, onRoute);

            // Body name at the first node of each body.
            if (map.ParentOf(node) is not { } parent || parent.BodyName != node.BodyName)
                AddText(node.BodyName, pos.X + 12, pos.Y - 30, 13, new SolidColorBrush(ColorOf(node.BodyName)), FontWeights.Bold);

            if (stopNumbers.TryGetValue(node.Key, out var numbers)) AddBadge(numbers, pos);
        }
    }

    /// <summary>Leaves get consecutive rows; parents sit midway between their first and last child.</summary>
    private static Dictionary<string, Point> Layout(DeltaVMap map, out double width, out double height)
    {
        var positions = new Dictionary<string, Point>();
        var row = 0;
        var maxDepth = 0;

        double Place(DvMapNode node, int depth)
        {
            maxDepth = Math.Max(maxDepth, depth);
            // Custom locations first, so they sit right next to their parent instead of below its whole subtree.
            var children = map.ChildrenOf(node).OrderByDescending(c => c.IsCustom).ToList();
            double y;
            if (children.Count == 0)
            {
                y = Margin0 + 20 + row++ * RowHeight;
            }
            else
            {
                var ys = children.Select(ch => Place(ch, depth + 1)).ToList();
                y = (ys[0] + ys[^1]) / 2;
            }
            positions[node.Key] = new Point(Margin0 + depth * ColumnWidth, y);
            return y;
        }

        Place(map.Root, 0);
        width = Margin0 * 2 + maxDepth * ColumnWidth + 60;
        height = Margin0 + 20 + row * RowHeight;
        return positions;
    }

    private void AddEdgeLabel(DvMapNode node, Point at, Color color, bool onRoute)
    {
        var box = new TextBox
        {
            Template = PlainTextBox,
            CaretBrush = TextBrush,
            Text = node.DvFromParent.ToString("0", CultureInfo.CurrentCulture),
            Width = 62,
            TextAlignment = TextAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Padding = new Thickness(0),
            MinHeight = 0,
            Background = Background0,
            Foreground = node.IsOverridden ? EditedBrush : TextBrush,
            BorderBrush = new SolidColorBrush(color),
            BorderThickness = new Thickness(node.IsOverridden ? 1.5 : 0),
            Opacity = onRoute ? 1 : 0.55,
            ToolTip = EdgeTooltip(node),
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Commit(box, node);
            if (e.Key == Key.Escape) box.Text = node.DvFromParent.ToString("0", CultureInfo.CurrentCulture);
        };
        box.LostKeyboardFocus += (_, _) => Commit(box, node);
        SetLeft(box, at.X - 31);
        SetTop(box, at.Y - 26);
        Children.Add(box);

        var extras = new List<string>();
        if (node.PlaneChangeDv > 0) extras.Add($"∠{node.PlaneChangeDv:0}");
        if (node.AerobrakeDown || node.AerobrakeUp) extras.Add("aero");
        if (extras.Count > 0) AddText(string.Join("  ", extras), at.X - 30, at.Y + 6, 10, DimText);
    }

    /// <summary>Empty text restores the map value; an unparseable value is discarded.</summary>
    private void Commit(TextBox box, DvMapNode node)
    {
        var text = box.Text.Trim();
        double target;
        if (text.Length == 0)
            target = node.MapDvFromParent;
        else if (!double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out target) || target < 0)
            target = node.DvFromParent;

        if (target == node.DvFromParent)
        {
            box.Text = node.DvFromParent.ToString("0", CultureInfo.CurrentCulture);
            return;
        }
        // Deferred: handling the edit rebuilds this canvas, which must not happen inside the TextBox's focus event.
        var edit = (node.Key, target == node.MapDvFromParent ? (double?)null : target);
        Dispatcher.BeginInvoke(() => EdgeEdited?.Invoke(this, edit));
    }

    private static string EdgeTooltip(DvMapNode node)
    {
        var lines = new List<string> { $"{node.DisplayName}: {node.DvFromParent:N0} m/s (either direction)" };
        lines.Add(node.IsOverridden ? $"Edited — map value is {node.MapDvFromParent:N0} m/s" : "Map value");
        if (node.PlaneChangeDv > 0) lines.Add($"Worst-case plane change: +{node.PlaneChangeDv:N0} m/s");
        if (node.AerobrakeDown) lines.Add($"Aerobraking possible on the way to {node.BodyName}");
        if (node.AerobrakeUp) lines.Add("Aerobraking possible on the way back toward Kerbin");
        if (node.FlightTime is { } t) lines.Add($"Flight time ≈ {t}");
        lines.Add("Click to edit (applies to all missions). Clear the box to restore the map value.");
        return string.Join("\n", lines);
    }

    private void AddNode(DvMapNode node, Point pos, bool onRoute)
    {
        var color = ColorOf(node.BodyName);
        var brush = new SolidColorBrush(color);
        var (w, h) = node.IsCustom ? (16.0, 16.0) : node.Kind == "Elliptical" ? (30.0, 18.0) : (20.0, 20.0);

        // Custom locations are diamonds; everything else follows the Δv map's circles.
        Shape shape = node.IsCustom
            ? new Rectangle { Width = w, Height = h, RenderTransform = new RotateTransform(45, w / 2, h / 2) }
            : new Ellipse { Width = w, Height = h };
        shape.Fill = node.Kind == "Surface" ? brush : Background0;
        shape.Stroke = node.Kind == "Surface" ? Brushes.White : brush;
        shape.StrokeThickness = 3;
        shape.Cursor = Cursors.Hand;
        shape.Opacity = onRoute ? 1 : 0.5;
        shape.ToolTip = $"{node.DisplayName}\nClick to add as a stop";
        shape.MouseLeftButtonUp += (_, _) => NodeClicked?.Invoke(this, node);
        SetLeft(shape, pos.X - w / 2);
        SetTop(shape, pos.Y - h / 2);
        Children.Add(shape);

        if (node.Kind is "LowOrbit" or "Synchronous")
        {
            var dot = new Ellipse { Width = 7, Height = 7, Fill = brush, IsHitTestVisible = false };
            SetLeft(dot, pos.X - 3.5);
            SetTop(dot, pos.Y - 3.5);
            Children.Add(dot);
        }

        AddText(ShortLabel(node), pos.X - 4, pos.Y + 12, 10, onRoute ? DimText : new SolidColorBrush(C("#5A6275")));
    }

    private void AddBadge(string text, Point pos)
    {
        var badge = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(9),
            MinWidth = 18,
            Height = 18,
            Padding = new Thickness(5, 0, 5, 0),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = text, Foreground = Brushes.Black, FontWeight = FontWeights.Bold, FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            },
        };
        SetLeft(badge, pos.X - 30);
        SetTop(badge, pos.Y - 26);
        Children.Add(badge);
    }

    private void AddText(string text, double x, double y, double size, Brush brush, FontWeight? weight = null)
    {
        var block = new TextBlock { Text = text, FontSize = size, Foreground = brush, FontWeight = weight ?? FontWeights.Normal, IsHitTestVisible = false };
        SetLeft(block, x);
        SetTop(block, y);
        Children.Add(block);
    }

    /// <summary>Stop numbers per node key, e.g. "1, 4" when the route starts and ends at the same place.</summary>
    private Dictionary<string, string> StopNumbers() =>
        (StopKeys ?? [])
            .Select((key, i) => (key, number: i + 1))
            .GroupBy(x => x.key)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(x => x.number)));

    private static string ShortLabel(DvMapNode node) => node.Kind switch
    {
        "Custom" => node.Label,
        "LowOrbit" => node.Label.Replace("Low orbit ", "LO "),
        "Elliptical" => "Elliptical",
        "Synchronous" => "Keostationary",
        _ => node.Label,
    };

    private static Color ColorOf(string body) => BodyColors.TryGetValue(body, out var c) ? c : Colors.Gray;
    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
