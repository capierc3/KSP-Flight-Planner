using System.Windows;
using KspFlightPlanner.App.Views;

namespace KspFlightPlanner.App;

/// <summary>What the window is set up for: planning a mission, or flying one against the live game.</summary>
public enum WorkMode { Plan, Fly }

/// <summary>
/// One dockable piece of the main window. Any pane can be docked anywhere; <paramref name="IsDocument"/> panes start as
/// tabs in the middle (Legs &amp; phases, Transfer Planner…), the rest at the side (Mission, Calculators, Rocket…).
/// <paramref name="Modes"/> lists the modes it appears in; the first is its home.
/// </summary>
public sealed record PaneInfo(string Id, string Title, bool IsDocument, WorkMode[] Modes, Func<MainViewModel, FrameworkElement> Create)
{
    /// <summary>Tool panes open on this side when they aren't in the layout yet.</summary>
    public bool OpensOnRight { get; init; }

    /// <summary>A pane to open as a tab next to, when it's in the layout (otherwise <see cref="OpensOnRight"/> decides).</summary>
    public string? OpensBeside { get; init; }
}

/// <summary>Every pane the window can show. The ids are stored in the saved window layouts, so don't rename them.</summary>
public static class Panes
{
    // Tool panes
    public const string Mission = "mission";
    public const string Missions = "missions";
    public const string Calculators = "calculators";
    public const string Notes = "notes";
    public const string Locations = "locations";
    public const string Vessels = "vessels";
    public const string Rocket = "rocket";
    public const string Flights = "flights";
    public const string FromHere = "fromhere";
    public const string Parts = "parts";
    public const string Part = "part";

    // Documents
    public const string Legs = "legs";
    public const string DvMap = "dvmap";
    public const string Location = "location";
    public const string Vessel = "vessel";
    public const string Transfer = "transfer";
    public const string Rendezvous = "rendezvous";
    public const string Resonant = "resonant";
    public const string Flight = "flight";
    public const string TechTree = "techtree";
    public const string Engineering = "engineering";
    public const string EngineChart = "enginechart";
    public const string CommNet = "commnet";
    public const string PowerBudget = "power";
    public const string Guide = "guide";

    private static readonly WorkMode[] Plan = [WorkMode.Plan];
    private static readonly WorkMode[] Fly = [WorkMode.Fly];
    private static readonly WorkMode[] Both = [WorkMode.Plan, WorkMode.Fly];

    public static readonly IReadOnlyList<PaneInfo> All =
    [
        new(Mission, "Mission", false, Plan, vm => new MissionStopsView { DataContext = vm.Mission }),
        new(Missions, "Saved missions", false, Plan, vm => new MissionListView { DataContext = vm.Mission }),
        new(Calculators, "Calculators", false, Plan, vm => new CalculatorsView { DataContext = vm }),
        new(Notes, "Notes", false, Plan, vm => new MissionNotesView { DataContext = vm.Mission }) { OpensOnRight = true },
        new(Locations, "Locations", false, Both, vm => new LocationListView { DataContext = vm.Locations }),
        new(Vessels, "Vessels", false, Both, vm => new VesselListView { DataContext = vm.Vessels }),
        new(Rocket, "Rocket", false, Plan, vm => new RocketPaneView { DataContext = vm }) { OpensOnRight = true },
        new(Parts, "Parts", false, Plan, vm => new PartListView { DataContext = vm.Parts }) { OpensOnRight = true, OpensBeside = Rocket },
        new(Part, "Part", false, Plan, vm => new PartDetailView { DataContext = vm.Parts.Detail }) { OpensOnRight = true, OpensBeside = Notes },
        new(Flights, "Flights", false, Fly, vm => new FlightListView { DataContext = vm.Flight }),
        new(FromHere, "From here", false, Fly, vm => new FromHereView { DataContext = vm.FromHere }) { OpensOnRight = true },

        new(Legs, "Legs & phases", true, Plan, vm => new MissionLegsView { DataContext = vm.Mission }),
        new(DvMap, "Δv map", true, Plan, vm => new DvMapView { DataContext = vm.Mission }),
        new(Location, "Location", true, Both, vm => new LocationsView { DataContext = vm.Locations }),
        new(Vessel, "Vessel", true, Both, vm => new VesselsView { DataContext = vm.Vessels }),
        new(Transfer, "Transfer Planner", true, Plan, vm => new TransferPlannerView { DataContext = vm.Transfer }),
        new(Rendezvous, "Rendezvous", true, Plan, vm => new RendezvousView { DataContext = vm.Rendezvous }),
        new(Resonant, "Resonant Orbits", true, Plan, vm => new ResonantOrbitView { DataContext = vm.Resonant }),
        new(TechTree, "Tech tree", true, Plan, vm => new TechTreeView { DataContext = vm.TechTree }),
        new(Engineering, "Engineering", true, Plan, vm => new EngineeringView { DataContext = vm.Engineering }),
        new(EngineChart, "Engine chart", true, Plan, vm => new EngineChartView { DataContext = vm.EngineChart }),
        new(CommNet, "CommNet", true, Plan, vm => new CommNetView { DataContext = vm.CommNet }),
        new(PowerBudget, "Power budget", true, Plan, vm => new PowerBudgetView { DataContext = vm.PowerBudget }),
        new(Flight, "Flight", true, Fly, vm => new FlightView { DataContext = vm.Flight }),
        new(Guide, "Math Guide", true, Both, _ => new GuideView()),
    ];

    public static PaneInfo? Find(string id) => All.FirstOrDefault(p => p.Id == id);
}
