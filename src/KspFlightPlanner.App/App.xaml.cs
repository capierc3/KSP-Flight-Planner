using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.App.ViewModels;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        SelectAllOnFocus();
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
            var sync = new SaveSync(settings, bodies, locations, new PartData(new PartCatalogRepository(database)), events, link);
            var designs = new DesignLibrary(sync);
            var flights = new FlightRepository(database);
            var viewModel = new MainViewModel(
                new MissionBuilderViewModel(maps, locations, events, designs, bodies, new MissionRepository(database), settings),
                new LocationsViewModel(bodies, maps, locations, events),
                new VesselsViewModel(sync, designs),
                new TransferPlannerViewModel(bodies, locations, events, sync),
                new RendezvousViewModel(sync, locations, events, designs, flights, maps),
                new ResonantOrbitViewModel(bodies, new ConstellationRepository(database), sync, flights, events),
                new FlightViewModel(flights, sync, events),
                sync,
                link,
                events);
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
}
