using System.Windows.Controls;

namespace KspFlightPlanner.App.Views;

public partial class FlightLogListView : UserControl
{
    public FlightLogListView() => InitializeComponent();

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ListBox)sender).IsKeyboardFocusWithin) MainCommands.ShowPane.Execute(Panes.FlightReport, this);
    }
}
