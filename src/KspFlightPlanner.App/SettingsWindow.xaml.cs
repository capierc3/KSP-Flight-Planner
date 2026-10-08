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

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
