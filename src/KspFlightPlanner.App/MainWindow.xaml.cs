using System.Windows;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.App.ViewModels;

namespace KspFlightPlanner.App;

public sealed class MainViewModel : ObservableObject
{
    public const int LocationsTab = 1;
    private int _selectedTab;

    public MainViewModel(MissionBuilderViewModel mission, LocationsViewModel locations, TransferPlannerViewModel transfer,
        ResonantOrbitViewModel resonant, SaveSync sync, PlannerEvents events)
    {
        Sync = sync;
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
    public SaveSync Sync { get; }

    public int SelectedTab
    {
        get => _selectedTab;
        set => SetProperty(ref _selectedTab, value);
    }
}

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        Closed += (_, _) => viewModel.Sync.Dispose();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsViewModel(_viewModel.Sync.Settings);
        if (new SettingsWindow(settings) { Owner = this }.ShowDialog() == true) _viewModel.Sync.Apply(settings.Result);
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await _viewModel.Sync.ReloadAsync();

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.Sync.ReloadAsync(); // pick up anything KSP saved since the last read
        if (!_viewModel.Sync.HasSave) return;
        var import = new ImportVesselsViewModel(_viewModel.Sync);
        if (new ImportVesselsWindow(import) { Owner = this }.ShowDialog() == true && import.ImportedIds.Count > 0)
        {
            _viewModel.SelectedTab = MainViewModel.LocationsTab;
            _viewModel.Locations.SelectLocation(import.ImportedIds[0]);
        }
    }
}
