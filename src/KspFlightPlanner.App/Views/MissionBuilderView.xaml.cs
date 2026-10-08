using System.Windows.Controls;
using KspFlightPlanner.App.ViewModels;

namespace KspFlightPlanner.App.Views;

public partial class MissionBuilderView : UserControl
{
    public MissionBuilderView()
    {
        InitializeComponent();
        MapView.NodeClicked += (_, node) => ViewModel?.AddStopFromMap(node);
        MapView.EdgeEdited += (_, edit) => ViewModel?.EditEdge(edit.Key, edit.DeltaV);
    }

    private MissionBuilderViewModel? ViewModel => DataContext as MissionBuilderViewModel;
}
