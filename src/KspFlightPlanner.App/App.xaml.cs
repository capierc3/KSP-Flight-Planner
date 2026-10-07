using System.Windows;
using KspFlightPlanner.App.ViewModels;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        try
        {
            var database = new PlannerDatabase(PlannerDatabase.DefaultPath());
            database.Initialize();

            var bodies = new BodyRepository(database);
            var viewModel = new MainViewModel(
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
}
