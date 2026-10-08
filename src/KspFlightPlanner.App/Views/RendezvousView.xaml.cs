using System.Windows.Controls;
using KspFlightPlanner.App.ViewModels;

namespace KspFlightPlanner.App.Views;

public partial class RendezvousView : UserControl
{
    public RendezvousView()
    {
        InitializeComponent();
        Porkchop.PointPicked += (_, p) => (DataContext as RendezvousViewModel)?.SelectPoint(p.DepartureUt, p.FlightTimeS);
    }
}
