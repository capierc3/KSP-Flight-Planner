using System.Windows.Controls;

namespace KspFlightPlanner.App.Views;

public partial class PartListView : UserControl
{
    public PartListView() => InitializeComponent();

    // Only a pick by the user brings the Part pane forward, not a refresh of the list.
    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ListBox)sender).IsKeyboardFocusWithin && e.AddedItems.Count > 0) MainCommands.ShowPane.Execute(Panes.Part, this);
    }
}
