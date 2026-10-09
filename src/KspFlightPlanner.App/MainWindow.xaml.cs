using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.App.ViewModels;
using KspFlightPlanner.Data;
using Microsoft.Win32;

namespace KspFlightPlanner.App;

public sealed class MainViewModel : ObservableObject
{
    private WorkMode _mode;
    private string? _activeCalculator;

    public MainViewModel(MissionBuilderViewModel mission, LocationsViewModel locations, VesselsViewModel vessels, TransferPlannerViewModel transfer,
        RendezvousViewModel rendezvous, ResonantOrbitViewModel resonant, FlightViewModel flight, SaveSync sync, KspLink link, PlannerEvents events)
    {
        Sync = sync;
        Link = link;
        Mission = mission;
        Locations = locations;
        Vessels = vessels;
        Transfer = transfer;
        Rendezvous = rendezvous;
        Resonant = resonant;
        Flight = flight;
        events.OpenFlightRequested += _ => RequestPane(Panes.Flight);

        // Leg tools: a mission leg's tool opens in its planner, locked to that leg.
        mission.TransferRequested += link =>
        {
            Transfer.EditLeg(link);
            RequestPane(Panes.Transfer);
        };
        mission.RendezvousRequested += link =>
        {
            Rendezvous.EditLeg(link);
            RequestPane(Panes.Rendezvous);
        };
        mission.DeployRequested += link =>
        {
            Resonant.EditDeploy(link);
            RequestPane(Panes.Resonant);
        };
        // Adding a leg: its planner starts where the mission is, with its inputs in the Calculators pane; once added, it
        // shows the new leg.
        Rendezvous.AttachMission(mission);
        mission.AddRendezvousRequested += () =>
        {
            Rendezvous.StartAdding();
            ShowCalculator(Panes.Rendezvous);
        };
        Rendezvous.AddedToMission += stop =>
        {
            if (mission.RendezvousLinkFor(stop) is { } link) Rendezvous.EditLeg(link);
        };
        Transfer.AttachMission(mission);
        mission.AddTransferRequested += () =>
        {
            Transfer.StartAdding();
            ShowCalculator(Panes.Transfer);
        };
        Transfer.AddedToMission += stop =>
        {
            if (mission.TransferLinkFor(stop) is { } link) Transfer.EditLeg(link);
        };
        Resonant.AttachMission(mission);
        mission.AddDeployRequested += () =>
        {
            Resonant.StartAdding();
            ShowCalculator(Panes.Resonant);
        };
        Resonant.AddedToMission += stop =>
        {
            if (mission.DeployLinkFor(stop) is { } link) Resonant.EditDeploy(link);
        };
        mission.RouteChanged += () =>
        {
            if (Transfer.Leg is { } transfer)
            {
                if (mission.TransferLinkFor(transfer.Owner) is not { } link) Transfer.StopEditingLeg();
                else if (link.Span != transfer.Span) Transfer.EditLeg(link);
            }
            if (Rendezvous.Leg is { } rendezvous)
            {
                if (mission.RendezvousLinkFor(rendezvous.Owner) is not { } link) Rendezvous.StopEditingLeg();
                else if (link.Span != rendezvous.Span) Rendezvous.EditLeg(link);
            }
            if (Resonant.Leg is { } deploy)
            {
                if (mission.DeployLinkFor(deploy.Owner) is not { } link) Resonant.StopEditingLeg();
                else if (link.Body != deploy.Body || link.AltitudeM != deploy.AltitudeM) Resonant.EditDeploy(link);
            }
        };
        events.OpenLocationRequested += id =>
        {
            Locations.SelectLocation(id);
            RequestPane(Panes.Location);
        };
    }

    public MissionBuilderViewModel Mission { get; }
    public LocationsViewModel Locations { get; }
    public VesselsViewModel Vessels { get; }
    public TransferPlannerViewModel Transfer { get; }
    public RendezvousViewModel Rendezvous { get; }
    public ResonantOrbitViewModel Resonant { get; }

    /// <summary>Flying the plans handed over from planning, against the live game.</summary>
    public FlightViewModel Flight { get; }
    public SaveSync Sync { get; }

    /// <summary>Live connection to the running game (kRPC).</summary>
    public KspLink Link { get; }

    /// <summary>Planning or flying; each has its own window layout.</summary>
    public WorkMode Mode
    {
        get => _mode;
        set
        {
            if (!SetProperty(ref _mode, value)) return;
            OnPropertyChanged(nameof(IsPlanning));
            OnPropertyChanged(nameof(IsFlying));
        }
    }

    public bool IsPlanning
    {
        get => Mode == WorkMode.Plan;
        set { if (value) Mode = WorkMode.Plan; }
    }

    public bool IsFlying
    {
        get => Mode == WorkMode.Fly;
        set { if (value) Mode = WorkMode.Fly; }
    }

    /// <summary>
    /// The calculator whose inputs the Calculators pane shows (a <see cref="Panes"/> id): the last calculator document to take
    /// focus. Other panes taking focus leave it as it is.
    /// </summary>
    public string? ActiveCalculator
    {
        get => _activeCalculator;
        private set
        {
            if (!SetProperty(ref _activeCalculator, value)) return;
            OnPropertyChanged(nameof(ShowsTransferInputs));
            OnPropertyChanged(nameof(ShowsRendezvousInputs));
            OnPropertyChanged(nameof(ShowsResonantInputs));
            OnPropertyChanged(nameof(ShowsNoCalculator));
        }
    }

    public bool ShowsTransferInputs => ActiveCalculator == Panes.Transfer;
    public bool ShowsRendezvousInputs => ActiveCalculator == Panes.Rendezvous;
    public bool ShowsResonantInputs => ActiveCalculator == Panes.Resonant;
    public bool ShowsNoCalculator => ActiveCalculator is null;

    /// <summary>Brings a calculator document and its inputs (the Calculators pane) into view.</summary>
    private void ShowCalculator(string id)
    {
        ActiveCalculator = id;
        RequestPane(id);
        RequestPane(Panes.Calculators);
    }

    /// <summary>A Plan document took focus (or is in front after loading the layout).</summary>
    public void DocumentFocused(string id)
    {
        if (id is Panes.Transfer or Panes.Rendezvous or Panes.Resonant) ActiveCalculator = id;
    }

    /// <summary>A screen asked for a pane to be brought into view (the window switches mode if it has to).</summary>
    public event Action<string>? PaneRequested;

    public void RequestPane(string id) => PaneRequested?.Invoke(id);
}

/// <summary>Menu and keyboard commands for the main window.</summary>
public static class MainCommands
{
    public static readonly RoutedUICommand ImportVessels = new("Import Vessels from Save…", nameof(ImportVessels), typeof(MainCommands),
        [new KeyGesture(Key.I, ModifierKeys.Control)]);
    public static readonly RoutedUICommand ReloadSave = new("Reload Save", nameof(ReloadSave), typeof(MainCommands), [new KeyGesture(Key.F5)]);
    public static readonly RoutedUICommand ReadPartData = new("Read Part Data Again", nameof(ReadPartData), typeof(MainCommands));
    public static readonly RoutedUICommand OpenDataFolder = new("Open Data Folder", nameof(OpenDataFolder), typeof(MainCommands));
    public static readonly RoutedUICommand ConnectKsp = new("Connect to KSP", nameof(ConnectKsp), typeof(MainCommands));
    public static readonly RoutedUICommand Settings = new("Settings…", nameof(Settings), typeof(MainCommands),
        [new KeyGesture(Key.OemComma, ModifierKeys.Control, "Ctrl+,")]);

    /// <summary>Bring a pane into view; the parameter is its id from <see cref="Panes"/>.</summary>
    public static readonly RoutedUICommand ShowPane = new("Show Pane", nameof(ShowPane), typeof(MainCommands));

    /// <summary>Switch to Plan or Fly; the parameter is the <see cref="WorkMode"/> name.</summary>
    public static readonly RoutedUICommand SetMode = new("Set Mode", nameof(SetMode), typeof(MainCommands));
    public static readonly RoutedUICommand ResetLayout = new("Reset Window Layout", nameof(ResetLayout), typeof(MainCommands));
    public static readonly RoutedUICommand About = new("About", nameof(About), typeof(MainCommands));
}

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DockWorkspace _plan;
    private readonly DockWorkspace _fly;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;

        var folder = Path.GetDirectoryName(PlannerDatabase.DefaultPath())!;
        _plan = new DockWorkspace(PlanDock, WorkMode.Plan, viewModel, folder);
        _plan.ActiveDocumentChanged += viewModel.DocumentFocused;
        _fly = new DockWorkspace(FlyDock, WorkMode.Fly, viewModel, folder);
        ApplyDockTheme();
        _plan.Load();
        _fly.Load();

        viewModel.PaneRequested += id => ShowPane(id);
        SystemEvents.UserPreferenceChanged += UserPreferenceChanged;
        viewModel.Mission.AskToSave = message => MessageBox.Show(this, message, "KSP Flight Planner",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null,
        };
        viewModel.Mission.Confirm = message => MessageBox.Show(this, message, "KSP Flight Planner",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        Closing += (_, e) =>
        {
            if (!viewModel.Mission.ConfirmDiscard())
            {
                e.Cancel = true;
                return;
            }
            _plan.Save();
            _fly.Save();
        };
        Closed += (_, _) =>
        {
            SystemEvents.UserPreferenceChanged -= UserPreferenceChanged;
            viewModel.Sync.Dispose();
            viewModel.Link.Dispose();
        };
    }

    private DockWorkspace Current => _viewModel.Mode == WorkMode.Fly ? _fly : _plan;

    /// <summary>Shows a pane, switching to Plan or Fly first if it only lives in the other mode.</summary>
    private void ShowPane(string id, bool activate = true)
    {
        if (Panes.Find(id) is not { } info) return;
        if (!info.Modes.Contains(_viewModel.Mode)) _viewModel.Mode = info.Modes[0];
        Current.Show(id, activate);
    }

    private void ApplyDockTheme()
    {
        DockTheme.Apply(PlanDock);
        DockTheme.Apply(FlyDock);
    }

    // Windows switched light/dark or the accent colour: Fluent follows by itself, the docking frame needs recolouring.
    private void UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            Dispatcher.BeginInvoke(ApplyDockTheme, DispatcherPriority.ApplicationIdle);
    }

    // CanExecute is first queried during InitializeComponent, before _viewModel is set.
    private void HasSave_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _viewModel?.Sync.HasSave == true;

    private void HasSaveChosen_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _viewModel?.Sync.PersistentPath is not null;

    private void Settings_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var settings = new SettingsViewModel(_viewModel.Sync.Settings, _viewModel.Sync.Parts);
        if (new SettingsWindow(settings) { Owner = this }.ShowDialog() != true) return;
        // The open mission belongs to the current save: offer to save it before another save's missions replace it.
        if (settings.Result.KspSaveName != _viewModel.Sync.SaveName && !_viewModel.Mission.ConfirmDiscard()) return;
        _viewModel.Sync.Apply(settings.Result);
    }

    private async void ConnectKsp_Executed(object sender, ExecutedRoutedEventArgs e) => await _viewModel.Link.ToggleAsync();

    private async void Reload_Executed(object sender, ExecutedRoutedEventArgs e) => await _viewModel.Sync.ReloadAsync();

    private async void Import_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        await _viewModel.Sync.ReloadAsync(); // pick up anything KSP saved since the last read
        if (!_viewModel.Sync.HasSave) return;
        var import = new ImportVesselsViewModel(_viewModel.Sync);
        if (new ImportVesselsWindow(import) { Owner = this }.ShowDialog() == true && import.ImportedIds.Count > 0)
        {
            _viewModel.Locations.SelectLocation(import.ImportedIds[0]);
            ShowPane(Panes.Location);
        }
    }

    private async void ReadPartData_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_viewModel.Sync.Settings.KspInstallPath is { } install) await _viewModel.Sync.Parts.ReadAsync(install);
    }

    private void OpenDataFolder_Executed(object sender, ExecutedRoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo { FileName = Path.GetDirectoryName(PlannerDatabase.DefaultPath())!, UseShellExecute = true });

    // A list picking an item only brings its detail forward; keyboard focus stays in the list.
    private void ShowPane_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is string id)
            ShowPane(id, activate: e.OriginalSource is not (Views.LocationListView or Views.VesselListView or Views.FlightListView));
    }

    private void SetMode_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (Enum.TryParse<WorkMode>(e.Parameter?.ToString(), out var mode)) _viewModel.Mode = mode;
    }

    private void ResetLayout_Executed(object sender, ExecutedRoutedEventArgs e) => Current.Reset();

    private void About_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
        MessageBox.Show(this, $"KSP Flight Planner {version}\nBrick Road Software\n\nMission planning for Kerbal Space Program. " +
                              "Every formula is explained in the Math Guide (F1).",
            "About KSP Flight Planner", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Exit_Executed(object sender, ExecutedRoutedEventArgs e) => Close();
}
