using System.Windows.Controls;
using System.Windows.Input;

namespace KspFlightPlanner.App.Views;

public partial class MissionStopsView : UserControl
{
    public MissionStopsView() => InitializeComponent();

    /// <summary>Enter applies a typed orbit altitude without leaving the box.</summary>
    private void AltitudeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box) box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }
}
