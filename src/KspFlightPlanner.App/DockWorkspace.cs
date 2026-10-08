using System.IO;
using System.Windows;
using System.Windows.Controls;
using AvalonDock;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;

namespace KspFlightPlanner.App;

/// <summary>
/// The docking layout for one <see cref="WorkMode"/>: which panes are open, where, and how big. It is saved when the window
/// closes and restored at the next start; Reset puts back the default arrangement.
/// </summary>
public sealed class DockWorkspace
{
    // Bump when the default layout changes enough that old saved layouts should be dropped.
    private const int LayoutVersion = 3;

    private readonly DockingManager _dock;
    private readonly WorkMode _mode;
    private readonly MainViewModel _viewModel;
    private readonly string _file;
    private readonly Dictionary<string, FrameworkElement> _views = [];

    public DockWorkspace(DockingManager dock, WorkMode mode, MainViewModel viewModel, string folder)
    {
        _dock = dock;
        _mode = mode;
        _viewModel = viewModel;
        _file = Path.Combine(folder, $"layout-{mode.ToString().ToLowerInvariant()}.v{LayoutVersion}.xml");
    }

    /// <summary>Restores the saved layout, or sets up the default one if there is none (or it can't be read).</summary>
    public void Load()
    {
        if (File.Exists(_file))
        {
            try
            {
                var serializer = new XmlLayoutSerializer(_dock);
                serializer.LayoutSerializationCallback += (_, e) =>
                {
                    // Panes that no longer exist (or moved to the other mode) are dropped.
                    if (Panes.Find(e.Model.ContentId) is { } info && info.Modes.Contains(_mode)) e.Content = View(info);
                    else e.Cancel = true;
                };
                serializer.Deserialize(_file);
                return;
            }
            catch (Exception)
            {
                // A layout from another version or a damaged file: fall back to the default.
            }
        }
        Reset();
    }

    public void Save()
    {
        try
        {
            new XmlLayoutSerializer(_dock).Serialize(_file);
            // Layouts from older versions are never read again.
            foreach (var old in Directory.EnumerateFiles(Path.GetDirectoryName(_file)!, $"layout-{_mode.ToString().ToLowerInvariant()}.v*.xml"))
                if (!string.Equals(old, _file, StringComparison.OrdinalIgnoreCase)) File.Delete(old);
        }
        catch (IOException)
        {
            // Not worth bothering anyone about on the way out; the default layout comes back next time.
        }
    }

    /// <summary>The default arrangement: tools on the left, documents in the middle, the rocket on the right.</summary>
    public void Reset()
    {
        var panel = new LayoutPanel { Orientation = Orientation.Horizontal };
        var root = new LayoutRoot { RootPanel = panel }; // replaces the empty document pane a new root starts with

        if (_mode == WorkMode.Plan)
        {
            panel.Children.Add(SideColumn(320, [Panes.Mission, Panes.Missions], [Panes.Locations, Panes.Vessels]));
            panel.Children.Add(Documents(Panes.Legs, Panes.DvMap, Panes.Transfer, Panes.Rendezvous, Panes.Resonant));
            panel.Children.Add(SideColumn(300, [Panes.Rocket], [Panes.Notes]));
        }
        else
        {
            panel.Children.Add(SideColumn(320, [Panes.Flights], [Panes.Vessels]));
            panel.Children.Add(Documents(Panes.Flight));
        }
        _dock.Layout = root;
    }

    /// <summary>Brings a pane into view, opening it (or reopening a closed/hidden one) if needed.</summary>
    /// <param name="activate">Also move keyboard focus to it; false just makes it the visible tab.</param>
    public void Show(string id, bool activate = true)
    {
        if (Panes.Find(id) is not { } info) return;
        var content = _dock.Layout.Descendents().OfType<LayoutContent>().FirstOrDefault(c => c.ContentId == id)
                      ?? _dock.Layout.Hidden.FirstOrDefault(c => c.ContentId == id);

        if (content is LayoutAnchorable { IsHidden: true } hidden) hidden.Show();
        else if (content is null) content = Open(info);

        content.IsSelected = true;
        if (activate) content.IsActive = true;
    }

    private LayoutContent Open(PaneInfo info)
    {
        if (info.IsDocument)
        {
            var tab = Anchorable(info);
            var pane = _dock.Layout.LastFocusedDocument?.Parent as LayoutDocumentPane
                       ?? _dock.Layout.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault();
            if (pane is null)
            {
                pane = new LayoutDocumentPane();
                _dock.Layout.RootPanel.Children.Add(pane);
            }
            pane.Children.Add(tab);
            return tab;
        }

        var anchorable = Anchorable(info);
        anchorable.AddToLayout(_dock, info.OpensOnRight ? AnchorableShowStrategy.Right : AnchorableShowStrategy.Left);
        return anchorable;
    }

    private LayoutAnchorablePaneGroup SideColumn(double width, params string[][] panes)
    {
        var group = new LayoutAnchorablePaneGroup { Orientation = Orientation.Vertical, DockWidth = new GridLength(width) };
        foreach (var ids in panes)
        {
            var pane = new LayoutAnchorablePane();
            foreach (var id in ids) pane.Children.Add(Anchorable(Panes.Find(id)!));
            group.Children.Add(pane);
        }
        return group;
    }

    private LayoutDocumentPaneGroup Documents(params string[] ids)
    {
        var pane = new LayoutDocumentPane();
        foreach (var id in ids) pane.Children.Add(Anchorable(Panes.Find(id)!));
        return new LayoutDocumentPaneGroup(pane);
    }

    // Every pane is an anchorable, even the ones that start in the middle: AvalonDock only lets documents move around the
    // middle, while an anchorable can dock on any side, tab with anything, or sit in the middle as a tab. Closing one
    // hides it, so View brings it back where it was.
    private LayoutAnchorable Anchorable(PaneInfo info) =>
        new() { ContentId = info.Id, Title = info.Title, Content = View(info), CanClose = false, CanHide = true };

    // One view per pane per mode, kept for the life of the window so closing and reopening a pane keeps its scroll position etc.
    private FrameworkElement View(PaneInfo info)
    {
        if (!_views.TryGetValue(info.Id, out var view)) _views[info.Id] = view = info.Create(_viewModel);
        return view;
    }
}
