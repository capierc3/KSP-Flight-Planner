using System.Windows;
using KspFlightPlanner.App.ViewModels;
using Microsoft.Win32;

namespace KspFlightPlanner.App;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PickFolder = initial =>
        {
            var dialog = new OpenFolderDialog { Title = "Kerbal Space Program folder", InitialDirectory = initial ?? "" };
            return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        };
    }

    private void Categories_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Fires during InitializeComponent, before the pages exist.
        if (KspPage is null || LivePage is null) return;
        KspPage.Visibility = Categories.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        LivePage.Visibility = Categories.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
