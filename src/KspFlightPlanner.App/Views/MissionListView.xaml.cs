using System.Windows.Controls;
using System.Windows.Input;
using KspFlightPlanner.App.ViewModels;

namespace KspFlightPlanner.App.Views;

public partial class MissionListView : UserControl
{
    public MissionListView() => InitializeComponent();

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OpenSelected();
        e.Handled = true;
    }

    private void OpenSelected()
    {
        if (DataContext is MissionBuilderViewModel vm && vm.OpenMissionCommand.CanExecute(vm.SelectedSavedMission))
            vm.OpenMissionCommand.Execute(vm.SelectedSavedMission);
    }
}
