using System.Windows.Controls;
using System.Windows.Input;
using KspFlightPlanner.App.ViewModels;

namespace KspFlightPlanner.App.Views;

public partial class TransferWindowsView : UserControl
{
    public TransferWindowsView() => InitializeComponent();

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => PlanSelected();

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        PlanSelected();
        e.Handled = true;
    }

    private void PlanSelected()
    {
        if (DataContext is TransferWindowsViewModel vm && List.SelectedItem is WindowRow row) vm.PlanCommand.Execute(row);
    }
}
