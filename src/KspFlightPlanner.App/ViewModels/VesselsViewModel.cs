using System.Collections.ObjectModel;
using System.ComponentModel;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.App.ViewModels;

/// <summary>A row in the vessels tree: a body (group) or a vessel.</summary>
public sealed class VesselTreeItem(string title, string subtitle, string glyph, SavedVessel? vessel = null) : ObservableObject
{
    private bool _isSelected;
    private bool _isExpanded = true;

    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public string Glyph { get; } = glyph;
    public SavedVessel? Vessel { get; } = vessel;
    public ObservableCollection<VesselTreeItem> Children { get; } = [];

    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
}

/// <summary>
/// The Vessels tab: every vessel in the linked KSP save, grouped by the body it's at, with its remaining Δv and fuel.
/// Reads straight from the save, so it's always as current as KSP's last write.
/// </summary>
public sealed class VesselsViewModel : ObservableObject
{
    private readonly SaveSync _sync;
    private VesselDetailViewModel? _detail;
    private string? _selectedId;
    private bool _showAll;

    public VesselsViewModel(SaveSync sync)
    {
        _sync = sync;
        sync.PropertyChanged += OnSourceChanged;
        sync.Parts.PropertyChanged += OnSourceChanged;
        Rebuild();
    }

    public ObservableCollection<VesselTreeItem> Tree { get; } = [];

    public VesselDetailViewModel? Detail { get => _detail; private set => SetProperty(ref _detail, value); }

    /// <summary>Also list debris, asteroids, flags and surface experiments.</summary>
    public bool ShowAll { get => _showAll; set { if (SetProperty(ref _showAll, value)) Rebuild(); } }

    public string Empty => _sync.Save is null
        ? "No KSP save is linked. Choose your KSP folder and save in Tools › Settings."
        : "No vessels here yet." + (ShowAll ? "" : " Tick \"Show debris, asteroids & flags\" to see everything in the save.");

    public bool IsEmpty => Tree.Count == 0;

    public string PartDataSummary => _sync.Parts.Summary;

    /// <summary>Called by the view when the tree selection changes.</summary>
    public void Select(VesselTreeItem? item)
    {
        if (item?.Vessel is not { } vessel) return;
        _selectedId = vessel.Id;
        var radius = vessel.Orbit?.BodyName is { } body && _sync.StockBodies().TryGetValue(body, out var b) ? b.EquatorialRadiusM : (double?)null;
        Detail = new VesselDetailViewModel(vessel, _sync.Save!.Ut, _sync.Parts.Catalog, radius);
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SaveSync.Save) or nameof(PartData.Catalog)) Rebuild();
        if (e.PropertyName is nameof(PartData.Summary)) OnPropertyChanged(nameof(PartDataSummary));
    }

    private void Rebuild()
    {
        Tree.Clear();
        var save = _sync.Save;
        if (save is not null)
        {
            var catalog = _sync.Parts.Catalog;
            var shown = save.Vessels.Where(v => ShowAll || SaveImport.IsUsuallyWanted(v));
            foreach (var group in shown.GroupBy(v => v.Orbit?.BodyName ?? "Unknown").OrderBy(g => BodyOrder(g.Key)))
            {
                var body = new VesselTreeItem(group.Key, group.Count() == 1 ? "1 vessel" : $"{group.Count()} vessels", "●");
                foreach (var v in group.OrderBy(v => v.Type).ThenBy(v => v.Name))
                {
                    var report = VesselAnalysis.Analyze(v, catalog);
                    var dv = report.Engines.Any(e => e.CountedForDeltaV) ? $" · {Units.Dv(report.DeltaVVacuum)}" : "";
                    body.Children.Add(new VesselTreeItem(v.Name, $"{SaveImport.TypeText(v.Type)} · {Situation(v.Situation)}{dv}", "◇", v));
                }
                Tree.Add(body);
            }
        }

        var selected = Tree.SelectMany(b => b.Children).FirstOrDefault(i => i.Vessel!.Id == _selectedId)
                       ?? Tree.SelectMany(b => b.Children).FirstOrDefault();
        if (selected is not null)
        {
            selected.IsSelected = true;
            Select(selected);
        }
        else Detail = null;
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Empty));
        OnPropertyChanged(nameof(PartDataSummary));
    }

    /// <summary>Stock bodies in KSP's order (Kerbol, Kerbin, Mun, …); anything else after.</summary>
    private static int BodyOrder(string body) =>
        Array.IndexOf(SavedOrbit.StockBodyOrder, body) is var i and >= 0 ? i : int.MaxValue;

    public static string Situation(string situation) => situation switch
    {
        "ORBITING" => "orbiting",
        "SUB_ORBITAL" => "sub-orbital",
        "PRELAUNCH" => "on the pad",
        _ => situation.ToLowerInvariant(),
    };
}

/// <summary>One fuel/resource row: amount, fill level and mass.</summary>
public sealed record ResourceRow(string Name, string Amount, double Fraction, string Percent, string Mass);

/// <summary>One engine row: count × part, thrust, limiter, Isp, what it burns.</summary>
public sealed record EngineRow(string Engine, string Thrust, string Limit, string Isp, string Propellants, bool Counted);

/// <summary>Everything about one vessel: status, Δv, fuel, engines, parts.</summary>
public sealed class VesselDetailViewModel
{
    /// <param name="bodyRadiusM">Radius of the body it orbits, to show altitudes; null if unknown.</param>
    public VesselDetailViewModel(SavedVessel vessel, double saveUt, PartCatalog catalog, double? bodyRadiusM)
    {
        var report = VesselAnalysis.Analyze(vessel, catalog);
        var calendar = GameCalendar.Kerbin;
        var body = vessel.Orbit?.BodyName ?? "?";

        Title = vessel.Name;
        Subtitle = $"{Capitalize(SaveImport.TypeText(vessel.Type))} · {VesselsViewModel.Situation(vessel.Situation)} {body}";
        HasEngines = report.Engines.Any(e => e.CountedForDeltaV);
        DeltaV = HasEngines ? Units.Dv(report.DeltaVVacuum) : "—";
        DeltaVNote = HasEngines
            ? $"in vacuum, all engines together at Isp {report.EffectiveIspS:0} s"
            : report.Engines.Count > 0 ? "no engine that works in a vacuum" : "no engines";

        var propulsion = new List<Fact>
        {
            new("Mass now (wet)", Units.Tonnes(report.WetMassT)),
            new("Dry mass", Units.Tonnes(report.DryMassT)),
            new("Propellant", Units.Tonnes(report.PropellantMassT)),
        };
        if (HasEngines)
        {
            var limited = report.ThrustKn < report.FullThrustKn - 1e-9;
            propulsion.Add(new("Thrust (vacuum)", limited
                ? $"{report.ThrustKn:0.###} kN with limiters  ({report.FullThrustKn:0.###} kN full)"
                : $"{report.FullThrustKn:0.###} kN"));
            propulsion.Add(new("Acceleration at start", $"{report.Acceleration:0.00} m/s²  ({report.Acceleration / VesselAnalysis.G0:0.000} g)"));
            propulsion.Add(new("Burn time for all of it", limited
                ? $"{Units.Duration(report.BurnTimeS)} with limiters  ({Units.Duration(report.BurnTimeS * report.ThrustKn / report.FullThrustKn)} full)"
                : Units.Duration(report.BurnTimeS)));
        }
        Propulsion = propulsion;

        Resources = report.Resources
            .Select(r => new ResourceRow(r.DisplayName, $"{r.Amount:#,0.##} / {r.MaxAmount:#,0.##}", r.Fraction, $"{r.Fraction:0%}",
                r.MassT > 0 ? Units.Tonnes(r.MassT) : "—"))
            .ToList();
        Engines = report.Engines
            .Select(e => new EngineRow(
                $"{e.Count} × {e.Title}",
                $"{e.MaxThrustKn * e.Count:0.###} kN",
                e.ThrustLimitPercent < 100 ? $"{e.ThrustLimitPercent:0.#} %" : "—",
                e.IspSeaLevelS > 0 ? $"{e.IspVacuumS:0} / {e.IspSeaLevelS:0} s" : $"{e.IspVacuumS:0} s",
                string.Join(" + ", e.Propellants.Select(p => catalog.DisplayName(p.Name))),
                e.CountedForDeltaV))
            .ToList();
        Notes = report.Notes;

        var status = new List<Fact> { new("Situation", $"{Capitalize(VesselsViewModel.Situation(vessel.Situation))} {body}") };
        if (vessel.IsOrbiting && vessel.Orbit is { } o && bodyRadiusM is { } radius)
        {
            status.Add(new("Periapsis × apoapsis", $"{Units.Km(o.SemiMajorAxisM * (1 - o.Eccentricity) - radius)} × " +
                                                   Units.Km(o.SemiMajorAxisM * (1 + o.Eccentricity) - radius)));
            status.Add(new("Inclination", Units.Degrees(Math.Round(o.InclinationDeg, 2))));
        }
        if (vessel.LaunchUt > 0) status.Add(new("Launched", calendar.FormatDate(vessel.LaunchUt)));
        if (vessel.MissionTimeS > 0) status.Add(new("Mission time", calendar.FormatDuration(vessel.MissionTimeS)));
        var crew = vessel.Crew.ToList();
        status.Add(new("Crew", crew.Count == 0 ? "None (uncrewed)" : string.Join(", ", crew)));
        status.Add(new("Save read at", calendar.FormatDate(saveUt)));
        Status = status;

        Parts = report.Parts.Select(p => new Fact(p.Title, $"× {p.Count}")).ToList();
        PartsTitle = $"Parts ({vessel.Parts.Count})";
    }

    public string Title { get; }
    public string Subtitle { get; }
    public bool HasEngines { get; }
    public string DeltaV { get; }
    public string DeltaVNote { get; }
    public IReadOnlyList<Fact> Propulsion { get; }
    public IReadOnlyList<ResourceRow> Resources { get; }
    public bool HasResources => Resources.Count > 0;
    public IReadOnlyList<EngineRow> Engines { get; }
    public bool HasEngineRows => Engines.Count > 0;
    public IReadOnlyList<string> Notes { get; }
    public bool HasNotes => Notes.Count > 0;
    public IReadOnlyList<Fact> Status { get; }
    public IReadOnlyList<Fact> Parts { get; }
    public string PartsTitle { get; }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
