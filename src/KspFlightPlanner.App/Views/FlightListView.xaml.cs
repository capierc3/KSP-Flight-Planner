using System.Windows.Controls;
using KspFlightPlanner.App.ViewModels;

namespace KspFlightPlanner.App.Views;

public partial class FlightListView : UserControl
{
    public FlightListView() => InitializeComponent();

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ListBox)sender).IsKeyboardFocusWithin) MainCommands.ShowPane.Execute(Panes.Flight, this);
    }

    /// <summary>Missions saved in Plan mode since the list was read show up.</summary>
    private void Missions_DropDownOpened(object sender, EventArgs e) => (DataContext as FlightViewModel)?.RefreshSavedMissions();
}
