using System.Windows;
using KspFlightPlanner.App.ViewModels;

namespace KspFlightPlanner.App;

public sealed record MainViewModel(TransferPlannerViewModel Transfer, ResonantOrbitViewModel Resonant);

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
