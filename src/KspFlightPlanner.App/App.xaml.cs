using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
            var viewModel = new MainViewModel(
                new MissionBuilderViewModel(new DvMapRepository(database), new MissionRepository(database),
                    new CustomLocationRepository(database)),
                new TransferPlannerViewModel(bodies),
                new ResonantOrbitViewModel(bodies, new ConstellationRepository(database)));
            new MainWindow(viewModel).Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"KSP Flight Planner failed to start:\n\n{ex.Message}", "Startup error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// Clicking or tabbing into any text box selects its contents, so typing replaces the value
    /// (most inputs here are numbers you overwrite, e.g. a Δv of 580 → 700).
    /// </summary>
    private static void SelectAllOnFocus()
    {
        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler((s, _) => ((TextBox)s).SelectAll()));

        // A mouse click would otherwise place the caret and clear the selection: focus first, swallow the click.
        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler((s, e) =>
            {
                var box = (TextBox)s;
                if (box.IsKeyboardFocusWithin) return;
                box.Focus();
                e.Handled = true;
            }));
    }
}
