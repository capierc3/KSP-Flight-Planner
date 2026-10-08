using System.Windows;
using KspFlightPlanner.App.ViewModels;

namespace KspFlightPlanner.App;

public sealed class MainViewModel : ObservableObject
{
    public const int LocationsTab = 1;
    private int _selectedTab;

    public MainViewModel(MissionBuilderViewModel mission, LocationsViewModel locations, TransferPlannerViewModel transfer,
        ResonantOrbitViewModel resonant, PlannerEvents events)
    {
        Mission = mission;
        Locations = locations;
        Transfer = transfer;
        Resonant = resonant;
        events.OpenLocationRequested += id =>
        {
            SelectedTab = LocationsTab;
            Locations.SelectLocation(id);
        };
    }

    public MissionBuilderViewModel Mission { get; }
    public LocationsViewModel Locations { get; }
    public TransferPlannerViewModel Transfer { get; }
    public ResonantOrbitViewModel Resonant { get; }

    public int SelectedTab
    {
        get => _selectedTab;
        set => SetProperty(ref _selectedTab, value);
    }
}

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
