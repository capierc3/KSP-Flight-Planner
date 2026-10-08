using System.Windows;
using KspFlightPlanner.App.ViewModels;

namespace KspFlightPlanner.App;

public partial class ImportVesselsWindow : Window
{
    public ImportVesselsWindow(ImportVesselsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Close = () => DialogResult = true;
    }
}
