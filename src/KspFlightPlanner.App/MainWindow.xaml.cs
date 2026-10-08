using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.App.ViewModels;
using KspFlightPlanner.Data;

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

/// <summary>Menu and keyboard commands for the main window.</summary>
public static class MainCommands
{
    public static readonly RoutedUICommand ImportVessels = new("Import Vessels from Save…", nameof(ImportVessels), typeof(MainCommands),
        [new KeyGesture(Key.I, ModifierKeys.Control)]);
    public static readonly RoutedUICommand ReloadSave = new("Reload Save", nameof(ReloadSave), typeof(MainCommands), [new KeyGesture(Key.F5)]);
    public static readonly RoutedUICommand OpenDataFolder = new("Open Data Folder", nameof(OpenDataFolder), typeof(MainCommands));
    public static readonly RoutedUICommand Settings = new("Settings…", nameof(Settings), typeof(MainCommands),
        [new KeyGesture(Key.OemComma, ModifierKeys.Control, "Ctrl+,")]);
    public static readonly RoutedUICommand ShowTab = new("Show Tab", nameof(ShowTab), typeof(MainCommands));
    public static readonly RoutedUICommand About = new("About", nameof(About), typeof(MainCommands));
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

    // CanExecute is first queried during InitializeComponent, before _viewModel is set.
    private void HasSave_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _viewModel?.Sync.HasSave == true;

    private void HasSaveChosen_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _viewModel?.Sync.PersistentPath is not null;

    private void Settings_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var settings = new SettingsViewModel(_viewModel.Sync.Settings);
        if (new SettingsWindow(settings) { Owner = this }.ShowDialog() == true) _viewModel.Sync.Apply(settings.Result);
    }

    private async void Reload_Executed(object sender, ExecutedRoutedEventArgs e) => await _viewModel.Sync.ReloadAsync();

    private async void Import_Executed(object sender, ExecutedRoutedEventArgs e)
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

    private void OpenDataFolder_Executed(object sender, ExecutedRoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo { FileName = Path.GetDirectoryName(PlannerDatabase.DefaultPath())!, UseShellExecute = true });

    private void ShowTab_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (int.TryParse(e.Parameter?.ToString(), out var tab)) _viewModel.SelectedTab = tab;
    }

    private void About_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
        MessageBox.Show(this, $"KSP Flight Planner {version}\nBrick Road Software\n\nMission planning for Kerbal Space Program. " +
                              "Every formula is explained in the Math Guide (F1).",
            "About KSP Flight Planner", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Exit_Executed(object sender, ExecutedRoutedEventArgs e) => Close();
}
