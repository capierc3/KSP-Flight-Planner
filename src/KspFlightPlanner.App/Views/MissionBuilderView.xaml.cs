using System.Windows;
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
        DataContextChanged += (_, _) =>
        {
            if (ViewModel is { } vm)
                vm.Confirm = message => MessageBox.Show(Window.GetWindow(this), message, "KSP Flight Planner",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        };
    }

    private MissionBuilderViewModel? ViewModel => DataContext as MissionBuilderViewModel;
}
