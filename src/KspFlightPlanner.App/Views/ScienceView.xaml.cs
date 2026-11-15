using System.Windows.Controls;
using System.Windows.Input;
using KspFlightPlanner.App.ViewModels;

namespace KspFlightPlanner.App.Views;

public partial class ScienceView : UserControl
{
    public ScienceView() => InitializeComponent();

    private void MapImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (DataContext is not ScienceViewModel vm || MapImage.ActualWidth <= 0 || MapImage.ActualHeight <= 0) return;
        var p = e.GetPosition(MapImage);
        vm.HoverAt(p.X / MapImage.ActualWidth, p.Y / MapImage.ActualHeight);
    }

    private void MapImage_MouseLeave(object sender, MouseEventArgs e) => (DataContext as ScienceViewModel)?.HoverEnd();
}
