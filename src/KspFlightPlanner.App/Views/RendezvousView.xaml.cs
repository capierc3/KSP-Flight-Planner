using System.Windows;
using System.Windows.Controls;
using KspFlightPlanner.App.ViewModels;

namespace KspFlightPlanner.App.Views;

public partial class RendezvousView : UserControl
{
    public RendezvousView()
    {
        InitializeComponent();
        Porkchop.PointPicked += (_, p) => (DataContext as RendezvousViewModel)?.SelectPoint(p.DepartureUt, p.FlightTimeS);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is RendezvousViewModel vm)
                vm.Confirm = message => MessageBox.Show(Window.GetWindow(this)!, message, "Send to KSP",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        };
    }
}
