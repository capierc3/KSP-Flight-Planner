using System.Windows;
using System.Windows.Controls;
using KspFlightPlanner.App.ViewModels;

namespace KspFlightPlanner.App.Views;

public partial class FlightView : UserControl
{
    public FlightView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is FlightViewModel vm)
                vm.Confirm = message => MessageBox.Show(Window.GetWindow(this)!, message, "Flight",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        };
    }
}
