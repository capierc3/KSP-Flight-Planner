using System.Windows.Controls;

namespace KspFlightPlanner.App.Views;

public partial class FlightListView : UserControl
{
    public FlightListView() => InitializeComponent();

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ListBox)sender).IsKeyboardFocusWithin) MainCommands.ShowPane.Execute(Panes.Flight, this);
    }
}
