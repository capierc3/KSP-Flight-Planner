using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.App.ViewModels;

/// <summary>A part as the tech tree and the Parts pane list it.</summary>
public sealed class PartItem(KspPart part, PartStatus status, string? nodeTitle)
{
    public KspPart Part { get; } = part;
    public string Title => Part.Title;
    public PartStatus Status { get; } = status;
    public bool IsUnlocked => Status == PartStatus.Unlocked;

    /// <summary>Category heading in the Parts pane.</summary>
    public string Category { get; } = PartCategories.Title(part.Category);

    public string StatusText => Status switch
    {
        PartStatus.Unlocked => "",
        PartStatus.Planned => "planned",
        PartStatus.NotBought => "not bought",
        _ => "locked",
    };

    /// <summary>Mass and cost, with thrust and Isp for engines.</summary>
    public string Facts
    {
        get
        {
            var c = CultureInfo.CurrentCulture;
            var facts = $"{Part.MassT.ToString("0.###", c)} t · √{Part.Cost.ToString("N0", c)}";
            // Jets' Isp curve is over air pressure differently, so a sea-level/vacuum pair means nothing for them.
            if (Part.Engines.FirstOrDefault() is { } e && e.MaxThrustKn > 0)
                facts += $" · {e.MaxThrustKn.ToString("N0", c)} kN · " +
                         (e.BreathesAir ? "jet" : $"Isp {e.IspSeaLevelS:0}/{e.IspVacuumS:0} s");
            return facts;
        }
    }

    public string? NodeTitle { get; } = nodeTitle;
}

/// <summary>KSP's part categories as headings.</summary>
internal static class PartCategories
{
    private static readonly Dictionary<string, string> Titles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Pods"] = "Command", ["FuelTank"] = "Fuel tanks", ["Engine"] = "Engines", ["Control"] = "Control", ["Structural"] = "Structural",
        ["Coupling"] = "Coupling", ["Payload"] = "Payload", ["Aero"] = "Aerodynamics", ["Ground"] = "Ground", ["Thermal"] = "Thermal",
        ["Electrical"] = "Electrical", ["Communication"] = "Communication", ["Science"] = "Science", ["Cargo"] = "Cargo",
        ["Robotics"] = "Robotics", ["Utility"] = "Utility",
    };

    public static string Title(string? category) =>
        category is null ? "Other" : Titles.GetValueOrDefault(category, category);

    /// <summary>KSP hides category "none" parts (old versions kept for craft that still use them).</summary>
    public static bool IsShown(KspPart part) => !string.Equals(part.Category, "none", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A node drawn on the tech tree.</summary>
public sealed class TechNodeItem(TechNode node, double left, double top) : ObservableObject
{
    private TechStatus _status;
    private bool _isSelected;

    public TechNode Node { get; } = node;
    public string Id => Node.Id;
    public string Title => Node.Title;
    public string CostText => Node.Cost.ToString("N0", CultureInfo.CurrentCulture);
    public double Left { get; } = left;
    public double Top { get; } = top;

    public TechStatus Status
    {
        get => _status;
        set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusText)); }
    }

    public string StatusText => Status switch
    {
        TechStatus.Researched => "Researched",
        TechStatus.Planned => "Planned",
        TechStatus.Available => "Can be researched now",
        _ => "Needs earlier nodes",
    };

    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

/// <summary>
/// The Tech tree document: the install's R&amp;D tree laid out as the game draws it, coloured by what the save has
/// researched. Clicking a node plans it (with the nodes it needs) or unplans it; the plan is kept per save.
/// </summary>
public sealed class TechTreeViewModel : ObservableObject
{
    public const double NodeWidth = 128;
    public const double NodeHeight = 46;
    private const double Scale = 0.8;
    private const double Pad = 20;

    private readonly PartAvailability _parts;
    private TechNodeItem? _selected;
    private Geometry _links = Geometry.Empty;
    private Geometry _plannedLinks = Geometry.Empty;
    private double _width;
    private double _height;

    public TechTreeViewModel(PartAvailability parts)
    {
        _parts = parts;
        ToggleCommand = new RelayCommand<TechNodeItem>(n =>
        {
            Selected = n;
            if (_parts.Research is not null) _parts.TogglePlanned(n.Id);
        });
        ClearPlanCommand = new RelayCommand(_parts.ClearPlan, () => _parts.Plan.Planned.Count > 0);
        parts.Changed += Rebuild;
        Rebuild();
    }

    public ObservableCollection<TechNodeItem> Nodes { get; } = [];

    /// <summary>Lines from each node to its parents.</summary>
    public Geometry Links { get => _links; private set => SetProperty(ref _links, value); }

    /// <summary>Lines into planned nodes from what they build on, drawn over <see cref="Links"/>.</summary>
    public Geometry PlannedLinks { get => _plannedLinks; private set => SetProperty(ref _plannedLinks, value); }

    public double Width { get => _width; private set => SetProperty(ref _width, value); }
    public double Height { get => _height; private set => SetProperty(ref _height, value); }

    public RelayCommand<TechNodeItem> ToggleCommand { get; }
    public RelayCommand ClearPlanCommand { get; }

    public TechNodeItem? Selected
    {
        get => _selected;
        set
        {
            if (_selected is not null) _selected.IsSelected = false;
            if (!SetProperty(ref _selected, value)) return;
            if (value is not null) value.IsSelected = true;
            OnPropertyChanged(nameof(SelectedParts));
            OnPropertyChanged(nameof(SelectedNeeds));
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => Selected is not null;

    /// <summary>The selected node's parts, unlocked ones first.</summary>
    public IReadOnlyList<PartItem> SelectedParts => Selected is null ? [] :
        _parts.Catalog.Parts.Values.Where(p => p.TechRequired == Selected.Id && PartCategories.IsShown(p))
            .Select(p => new PartItem(p, _parts.StatusOf(p), Selected.Title))
            .OrderBy(p => p.Status).ThenBy(p => p.Title).ToList();

    /// <summary>E.g. "Needs Basic Rocketry and Engineering 101", or "…any one of…".</summary>
    public string? SelectedNeeds
    {
        get
        {
            if (Selected is null || Selected.Node.Parents.Count == 0) return null;
            var titles = Selected.Node.Parents.Select(id => Nodes.FirstOrDefault(n => n.Id == id)?.Title ?? id).ToList();
            var list = titles.Count == 1 ? titles[0] : string.Join(", ", titles[..^1]) + (Selected.Node.AnyToUnlock ? " or " : " and ") + titles[^1];
            return (Selected.Node.AnyToUnlock && titles.Count > 1 ? "Needs any one of " : "Needs ") + list;
        }
    }

    /// <summary>Science in the bank and what the plan costs.</summary>
    public string Summary
    {
        get
        {
            var c = CultureInfo.CurrentCulture;
            if (_parts.Catalog.TechTree.Count == 0) return "No tech tree yet: set the KSP install in Settings, then Tools › Read Part Data Again.";
            if (_parts.Research is not { } research) return "Sandbox (or no save chosen): every part is unlocked, so there's nothing to plan.";
            var researched = Nodes.Count(n => n.Status == TechStatus.Researched);
            var text = $"{researched} of {Nodes.Count} nodes researched · {research.Science.ToString("N0", c)} science in the bank";
            var plan = _parts.Plan;
            if (plan.Planned.Count == 0) return text + " · click a node to plan it";
            var shortBy = plan.PlannedCost - research.Science;
            return text + $" · plan: {plan.Planned.Count} node{(plan.Planned.Count == 1 ? "" : "s")}, {plan.PlannedCost.ToString("N0", c)} science" +
                (shortBy > 0 ? $" ({shortBy.ToString("N0", c)} still to earn)" : " (affordable now)");
        }
    }

    private void Rebuild()
    {
        var tree = _parts.Catalog.TechTree;
        var selectedId = Selected?.Id;
        if (Nodes.Count != tree.Count || !Nodes.Select(n => n.Id).SequenceEqual(tree.Select(n => n.Id)))
        {
            Selected = null;
            Nodes.Clear();
            if (tree.Count > 0)
            {
                double minX = tree.Min(n => n.X), maxX = tree.Max(n => n.X), maxY = tree.Max(n => n.Y), minY = tree.Min(n => n.Y);
                foreach (var n in tree)
                    Nodes.Add(new TechNodeItem(n, (n.X - minX) * Scale + Pad, (maxY - n.Y) * Scale + Pad));
                Width = (maxX - minX) * Scale + NodeWidth + 2 * Pad;
                Height = (maxY - minY) * Scale + NodeHeight + 2 * Pad;
            }
        }

        var plan = _parts.Plan;
        foreach (var n in Nodes) n.Status = _parts.Research is null ? TechStatus.Researched : plan.Status(n.Id);

        var byId = Nodes.ToDictionary(n => n.Id);
        var links = new StreamGeometry();
        var planned = new StreamGeometry();
        using (var all = links.Open())
        using (var into = planned.Open())
            foreach (var child in Nodes)
                foreach (var parent in child.Node.Parents.Select(byId.GetValueOrDefault).OfType<TechNodeItem>())
                {
                    var onPlan = child.Status == TechStatus.Planned && parent.Status is TechStatus.Planned or TechStatus.Researched;
                    Link(onPlan ? into : all, parent, child);
                }
        links.Freeze();
        planned.Freeze();
        Links = links;
        PlannedLinks = planned;

        if (selectedId is not null && Selected is null) Selected = Nodes.FirstOrDefault(n => n.Id == selectedId);
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(SelectedParts));
    }

    /// <summary>From the parent's right edge to the child's left edge, with a step in the middle as the R&amp;D building draws it.</summary>
    private static void Link(StreamGeometryContext g, TechNodeItem parent, TechNodeItem child)
    {
        var from = new System.Windows.Point(parent.Left + NodeWidth, parent.Top + NodeHeight / 2);
        var to = new System.Windows.Point(child.Left, child.Top + NodeHeight / 2);
        g.BeginFigure(from, false, false);
        if (to.X > from.X)
        {
            var midX = (from.X + to.X) / 2;
            g.LineTo(new(midX, from.Y), true, false);
            g.LineTo(new(midX, to.Y), true, false);
        }
        g.LineTo(to, true, false);
    }
}

/// <summary>
/// The Parts pane: every part of the install by category, with what the chosen save can build. By default it lists the
/// unlocked and planned parts; "Show all parts" adds the rest, greyed out.
/// </summary>
public sealed class PartsViewModel : ObservableObject
{
    private readonly PartAvailability _parts;
    private bool _showAll;
    private string _search = "";
    private IReadOnlyList<PartItem> _items = [];

    public PartsViewModel(PartAvailability parts)
    {
        _parts = parts;
        parts.Changed += Rebuild;
        Rebuild();
    }

    public bool ShowAll { get => _showAll; set { if (SetProperty(ref _showAll, value)) Rebuild(); } }
    public string Search { get => _search; set { if (SetProperty(ref _search, value ?? "")) Rebuild(); } }

    public IReadOnlyList<PartItem> Items { get => _items; private set => SetProperty(ref _items, value); }

    public string Summary
    {
        get
        {
            var shown = _parts.Catalog.Parts.Values.Where(PartCategories.IsShown).ToList();
            if (shown.Count == 0) return "No part data yet: set the KSP install in Settings.";
            if (_parts.Research is null) return $"{shown.Count:N0} parts, all unlocked (sandbox or no save chosen).";
            var unlocked = shown.Count(p => _parts.StatusOf(p) == PartStatus.Unlocked);
            var planned = shown.Count(p => _parts.StatusOf(p) == PartStatus.Planned);
            return $"{unlocked:N0} of {shown.Count:N0} parts unlocked" + (planned > 0 ? $", {planned:N0} more planned" : "") + ".";
        }
    }

    private void Rebuild()
    {
        var nodes = _parts.Catalog.TechTree.ToDictionary(n => n.Id, n => n.Title);
        var words = _search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Items = _parts.Catalog.Parts.Values
            .Where(PartCategories.IsShown)
            .Where(p => words.All(w => p.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase) ||
                                       (p.Manufacturer?.Contains(w, StringComparison.CurrentCultureIgnoreCase) ?? false)))
            .Select(p => new PartItem(p, _parts.StatusOf(p), p.TechRequired is { } t ? nodes.GetValueOrDefault(t, t) : null))
            .Where(p => ShowAll || p.Status is PartStatus.Unlocked or PartStatus.Planned)
            .OrderBy(p => p.Category).ThenBy(p => p.Status == PartStatus.Unlocked ? 0 : 1).ThenBy(p => p.Title)
            .ToList();
        OnPropertyChanged(nameof(Summary));
    }
}
