using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KspFlightPlanner.App.ViewModels;
using Microsoft.Win32;

namespace KspFlightPlanner.App.Views;

public partial class LocationsView : UserControl
{
    public LocationsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not LocationsViewModel vm) return;
            vm.Confirm = message => MessageBox.Show(Window.GetWindow(this), message, "KSP Flight Planner",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            vm.PickImages = () =>
            {
                var dialog = new OpenFileDialog
                {
                    Title = "Add images",
                    Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
                    Multiselect = true,
                };
                return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileNames : null;
            };
        };
    }

    /// <summary>Shows an image full size in its own window.</summary>
    private void Image_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LocationImageItem item
            || CustomLocationEditorViewModel.Decode(item.Data) is not { } full) return;

        new Window
        {
            Title = item.FileName,
            Owner = Window.GetWindow(this),
            Width = Math.Min(full.PixelWidth + 40, 1400),
            Height = Math.Min(full.PixelHeight + 60, 900),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.Black,
            Content = new Image { Source = full, Stretch = Stretch.Uniform },
        }.Show();
    }
}
