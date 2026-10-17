using System.Windows.Controls;

namespace KspFlightPlanner.App.Views;

public partial class FlightLogListView : UserControl
{
    public FlightLogListView() => InitializeComponent();

    private void Compare_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.FlightLogViewModel vm) return;
        vm.CompareSelected();
        MainCommands.ShowPane.Execute(Panes.FlightCompare, this);
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ListBox)sender).IsKeyboardFocusWithin) MainCommands.ShowPane.Execute(Panes.FlightReport, this);
    }
}
