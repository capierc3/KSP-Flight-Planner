using System.Windows;
using System.Windows.Controls;
using KspFlightPlanner.App.ViewModels;

namespace KspFlightPlanner.App.Views;

public partial class LocationListView : UserControl
{
    public LocationListView() => InitializeComponent();

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        (DataContext as LocationsViewModel)?.Select(e.NewValue as LocationTreeItem);
        // Only a pick by the user brings the detail forward, not a refresh of the tree.
        if (((TreeView)sender).IsKeyboardFocusWithin) MainCommands.ShowPane.Execute(Panes.Location, this);
    }
}
