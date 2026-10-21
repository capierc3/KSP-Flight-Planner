using System.Windows;
using KspFlightPlanner.App.ViewModels;

namespace KspFlightPlanner.App;

public partial class FlightLogEditWindow : Window
{
    public FlightLogEditWindow(FlightLogEditViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Close = () => DialogResult = true;
    }
}
