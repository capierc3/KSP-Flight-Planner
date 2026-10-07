using System.Windows.Controls;
using KspFlightPlanner.App.ViewModels;

namespace KspFlightPlanner.App.Views;

public partial class TransferPlannerView : UserControl
{
    public TransferPlannerView()
    {
        InitializeComponent();
        Porkchop.PointPicked += (_, p) => (DataContext as TransferPlannerViewModel)?.SelectPoint(p.DepartureUt, p.FlightTimeS);
    }
}
