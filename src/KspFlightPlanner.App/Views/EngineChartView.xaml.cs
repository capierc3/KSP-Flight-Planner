using System.Windows.Controls;
using KspFlightPlanner.App.ViewModels;

namespace KspFlightPlanner.App.Views;

public partial class EngineChartView : UserControl
{
    public EngineChartView()
    {
        InitializeComponent();
        Chart.PointPicked += (payload, dv) => (DataContext as EngineChartViewModel)?.PickPoint(payload, dv);
    }
}
