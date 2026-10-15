using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.App.ViewModels;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        SelectAllOnFocus();
        HideClearButtons();
        try
        {
            var database = new PlannerDatabase(PlannerDatabase.DefaultPath());
            database.Initialize();

            var bodies = new BodyRepository(database);
            var maps = new DvMapRepository(database);
            var locations = new CustomLocationRepository(database);
            var events = new PlannerEvents();
            var settings = new SettingsRepository(database);
            var link = new KspLink(() => settings.Load());
            var sync = new SaveSync(database, settings, bodies, locations, new PartData(new PartCatalogRepository(database)), events, link);
            var designs = new DesignLibrary(sync);
            var availability = new PartAvailability(sync, new TechPlanRepository(database), events);
            var flights = new FlightRepository(database);
            var bakes = new SurfaceBakeRepository(database);
            var rotation = new BodyRotation(bakes, link);
            var stockBodies = bodies.GetSystems().FirstOrDefault(s => s.Name == BodyRepository.StockSystemName) is { } stock
                ? bodies.GetBodies(stock.Id).ToDictionary(b => b.Name)
                : [];
            var mission = new MissionBuilderViewModel(maps, locations, events, designs, bodies, new MissionRepository(database), settings, sync, bakes, flights);
            var engineering = new EngineeringViewModel(designs, sync, availability, mission);
            var viewModel = new MainViewModel(
                mission,
                new LocationsViewModel(bodies, maps, locations, events, bakes),
                new VesselsViewModel(sync, designs),
                new TransferPlannerViewModel(bodies, locations, events, sync, flights),
                new RendezvousViewModel(sync, locations, events, designs, flights, maps, bakes),
                new ResonantOrbitViewModel(bodies, new ConstellationRepository(database), sync, flights, events),
                new FlightViewModel(flights, sync, events, rotation),
                new FromHereViewModel(sync, flights, events, rotation),
                sync,
                link,
                events)
            {
                TechTree = new TechTreeViewModel(availability),
                Parts = new PartsViewModel(availability),
                Engineering = engineering,
                EngineChart = new EngineChartViewModel(sync, availability),
                CommNet = new CommNetViewModel(sync, availability, engineering),
                NewSurfaceBake = () => new SurfaceBakeViewModel(link, bakes, events, stockBodies),
            };
            // The tech tree follows the picked part, so an open tree shows where it comes from. Only a different part
            // moves it: the Part pane also refreshes when the plan changes, and that mustn't undo a pick in the tree.
            string? shownPart = null;
            viewModel.Parts.Detail.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(PartDetailViewModel.Item) || viewModel.Parts.Detail.Item?.Part is not { } part) return;
                if (part.Name == shownPart) return;
                shownPart = part.Name;
                if (part.TechRequired is { } node) viewModel.TechTree.SelectNode(node);
            };
            void OpenNode(string id)
            {
                viewModel.TechTree.SelectNode(id);
                viewModel.RequestPane(Panes.TechTree);
            }
            viewModel.Parts.Detail.OpenNodeRequested += OpenNode;
            // Picking in the Engineering document brings the Rocket pane (a stage) or the Part pane (a part) into view,
            // leaving keyboard focus in the document's list.
            viewModel.Engineering.StagePicked += () => viewModel.RequestPane(Panes.Rocket, activate: false);
            void ShowPart(PartItem item)
            {
                viewModel.Parts.Detail.Item = item;
                viewModel.RequestPane(Panes.Part, activate: false);
            }
            viewModel.Engineering.PartPicked += ShowPart;
            viewModel.EngineChart.PartPicked += ShowPart;
            new MainWindow(viewModel).Show();
            if (sync.Settings.KrpcConnectOnStartup) _ = link.ConnectAsync();
        }
        catch (Exception ex)
        {
            // The whole exception goes to a file next to the database: a one-line message rarely says where it failed.
            var log = Path.Combine(Path.GetDirectoryName(PlannerDatabase.DefaultPath())!, "startup-error.log");
            try { File.AppendAllText(log, $"{DateTime.Now:O}\n{ex}\n\n"); }
            catch (IOException) { log = ""; }
            MessageBox.Show($"KSP Flight Planner failed to start:\n\n{ex.Message}" + (log.Length > 0 ? $"\n\nDetails: {log}" : ""),
                "Startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// Clicking or tabbing into any text box selects its contents, so typing replaces the value
    /// (most inputs here are numbers you overwrite, e.g. a Δv of 580 → 700). Multi-line text (notes) is left alone:
    /// there a click should put the caret where you clicked.
    /// </summary>
    private static void SelectAllOnFocus()
    {
        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler((s, _) =>
            {
                if (s is TextBox { AcceptsReturn: false } box) box.SelectAll();
            }));

        // A mouse click would otherwise place the caret and clear the selection: focus first, swallow the click.
        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler((s, e) =>
            {
                var box = (TextBox)s;
                if (box.IsKeyboardFocusWithin || box.AcceptsReturn) return;
                box.Focus();
                e.Handled = true;
            }));
    }

    /// <summary>
    /// Fluent text boxes show a ✕ to clear them while focused. It covers short number boxes (the inclination's "180"),
    /// and select-all on focus already lets you type over or delete the value, so it's hidden everywhere.
    /// </summary>
    private static void HideClearButtons() =>
        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler((s, _) =>
            {
                // The theme's visual states show it on focus (animations beat a local Visibility), so give it no room instead.
                foreach (var clear in Descendants((TextBox)s).OfType<Button>().Where(b => b.Name == "DeleteButton"))
                {
                    clear.MinWidth = clear.Width = clear.MaxWidth = 0; // the button style's MinWidth would win over MaxWidth
                    clear.Margin = new Thickness(0);
                    clear.Focusable = false;
                }
            }));

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var below in Descendants(child)) yield return below;
        }
    }
}
