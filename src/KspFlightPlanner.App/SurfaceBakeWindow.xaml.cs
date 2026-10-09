using System.Windows;
using KspFlightPlanner.App.ViewModels;

namespace KspFlightPlanner.App;

/// <summary>Tools › Bake Surface Data. Not modal, so the rest of the planner stays usable while it runs.</summary>
public partial class SurfaceBakeWindow : Window
{
    public SurfaceBakeWindow(SurfaceBakeViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closing += (_, _) => viewModel.Closing();
    }
}
