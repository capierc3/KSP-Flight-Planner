using System.Windows;
using System.Windows.Controls;
using KspFlightPlanner.App.ViewModels;

namespace KspFlightPlanner.App.Views;

public partial class VesselsView : UserControl
{
    public VesselsView() => InitializeComponent();

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        (DataContext as VesselsViewModel)?.Select(e.NewValue as VesselTreeItem);
}
