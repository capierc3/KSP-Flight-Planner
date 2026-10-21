using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using KspFlightPlanner.App.ViewModels;

namespace KspFlightPlanner.App.Views;

public partial class FlightLogListView : UserControl
{
    public FlightLogListView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is FlightLogViewModel old) old.PropertyChanged -= ViewModel_PropertyChanged;
            if (e.NewValue is FlightLogViewModel vm) vm.PropertyChanged += ViewModel_PropertyChanged;
            UpdateGrouping();
        };
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FlightLogViewModel.GroupByRocket)) UpdateGrouping();
    }

    /// <summary>Groups the list by rocket while it's sorted by rocket; other orders are one flat list.</summary>
    private void UpdateGrouping()
    {
        var groups = ((CollectionViewSource)Resources["LogsView"]).GroupDescriptions;
        var group = DataContext is FlightLogViewModel { GroupByRocket: true };
        if (group == (groups.Count > 0)) return;
        groups.Clear();
        if (group) groups.Add(new PropertyGroupDescription(nameof(FlightLogItem.Rocket)));
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not FlightLogViewModel vm || vm.EditSelected() is not { } edit) return;
        new FlightLogEditWindow(edit) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not FlightLogViewModel vm) return;
        vm.CompareSelected();
        MainCommands.ShowPane.Execute(Panes.FlightCompare, this);
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ListBox)sender).IsKeyboardFocusWithin) MainCommands.ShowPane.Execute(Panes.FlightReport, this);
    }
}
