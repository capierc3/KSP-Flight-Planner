using System.Windows;
using KspFlightPlanner.App.ViewModels;

namespace KspFlightPlanner.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
