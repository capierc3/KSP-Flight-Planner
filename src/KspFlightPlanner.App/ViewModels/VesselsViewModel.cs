using System.Collections.ObjectModel;
using System.ComponentModel;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.App.ViewModels;

/// <summary>A row in the vessels tree: a group (In flight, a body, Saved designs, VAB…), a vessel or a saved design.</summary>
public sealed class VesselTreeItem(string title, string subtitle, string glyph, SavedVessel? vessel = null, CraftFileInfo? craft = null)
    : ObservableObject
{
    private bool _isSelected;
    private bool _isExpanded = true;

    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public string Glyph { get; } = glyph;
    public SavedVessel? Vessel { get; } = vessel;
    public CraftFileInfo? Craft { get; } = craft;

    /// <summary>What stays the same when the tree is rebuilt: the vessel's id or the design's file.</summary>
    public string? Key => Vessel?.Id ?? Craft?.Path;
    public ObservableCollection<VesselTreeItem> Children { get; } = [];

    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
}

/// <summary>
/// The Vessels pane: every vessel in the linked KSP save, grouped by the body it's at, with its remaining Δv and fuel;
/// and every saved design (.craft) with its stage-by-stage Δv. Reads straight from KSP's files, so it's always as
/// current as KSP's last write.
/// </summary>
public sealed class VesselsViewModel : ObservableObject
{
    private readonly SaveSync _sync;
    private readonly DesignLibrary _designs;
    private object? _detail;
    private string? _selectedId;
    private bool _showAll;

    public VesselsViewModel(SaveSync sync, DesignLibrary designs)
    {
        _sync = sync;
        _designs = designs;
        sync.PropertyChanged += OnSourceChanged;
        sync.Parts.PropertyChanged += OnSourceChanged;
        designs.PropertyChanged += OnSourceChanged;
        Rebuild();
    }

    public ObservableCollection<VesselTreeItem> Tree { get; } = [];

    /// <summary>The right-hand panel: a <see cref="VesselDetailViewModel"/> or a <see cref="DesignDetailViewModel"/>.</summary>
    public object? Detail { get => _detail; private set => SetProperty(ref _detail, value); }

    /// <summary>Also list debris, asteroids, flags and surface experiments.</summary>
    public bool ShowAll { get => _showAll; set { if (SetProperty(ref _showAll, value)) Rebuild(); } }

    public string Empty => _sync.Save is null
        ? "No KSP save is linked. Choose your KSP folder and save in File › Settings."
        : "No vessels here yet." + (ShowAll ? "" : " Tick \"Show debris, asteroids & flags\" to see everything in the save.");

    public bool IsEmpty => Tree.Count == 0;

    public string PartDataSummary => _sync.Parts.Summary;

    /// <summary>A vessel's logged flights, newest first (set by the app; none without it).</summary>
    public Func<SavedVessel, IReadOnlyList<FlightLogSummary>> FlightsOf { get; set; } = _ => [];

    /// <summary>The Flight log should open this flight (picked on a vessel's page).</summary>
    public event Action<long>? OpenLogRequested;

    /// <summary>Picks a vessel by its save pid, showing debris and the like if that's what it is. False if it isn't in the save.</summary>
    public bool SelectVessel(string id)
    {
        if (Find(Tree, id) is null && !ShowAll && _sync.Save?.Vessels.Any(v => v.Id == id) == true) ShowAll = true;
        if (Find(Tree, id) is not { } path) return false;
        foreach (var parent in path[..^1]) parent.IsExpanded = true;
        _selectedId = id;
        path[^1].IsSelected = true;
        Select(path[^1]);
        return true;
    }

    /// <summary>Shows the picked vessel again, e.g. once its flights have changed.</summary>
    public void RefreshDetail()
    {
        if (Detail is VesselDetailViewModel && Leaves(Tree).FirstOrDefault(i => i.Key == _selectedId) is { } item) Select(item);
    }

    /// <summary>The way down the tree to a vessel: its groups, then the vessel; null if it isn't shown.</summary>
    private static VesselTreeItem[]? Find(IEnumerable<VesselTreeItem> items, string id)
    {
        foreach (var item in items)
        {
            if (item.Vessel?.Id == id) return [item];
            if (Find(item.Children, id) is { } below) return [item, .. below];
        }
        return null;
    }

    /// <summary>Called by the view when the tree selection changes.</summary>
    public void Select(VesselTreeItem? item)
    {
        if (item?.Craft is { } craft)
        {
            _selectedId = craft.Path;
            Detail = _designs.Entries.FirstOrDefault(d => d.File.Path == craft.Path) is { Report: { } report }
                ? new DesignDetailViewModel(report, craft) : null;
            return;
        }
        if (item?.Vessel is not { } vessel) return;
        _selectedId = vessel.Id;
        var radius = vessel.Orbit?.BodyName is { } body && _sync.StockBodies().TryGetValue(body, out var b) ? b.EquatorialRadiusM : (double?)null;
        var stack = vessel.DockedInId is { } stackId ? _sync.FindVessel(stackId) : null;
        var piece = stack is null ? null : _sync.DockedIn(stack).FirstOrDefault(p => p.Craft.Id == vessel.Id);
        var docked = piece is null ? [] : _sync.DockedIn(vessel);
        Detail = new VesselDetailViewModel(vessel, _sync.Save!.Ut, _sync.Parts.Catalog, radius, FlightsOf(vessel))
        {
            OpenLogCommand = new RelayCommand<VesselFlightRow>(row => OpenLogRequested?.Invoke(row.Id)),
            DockedNote = piece is not null
                ? $"Docked at {stack!.Name}. Its parts, fuel and engines are shown on their own, as they'll be once it undocks."
                : _sync.DockedIn(vessel) is { Count: > 0 } pieces
                    ? $"Docked here: {string.Join(", ", pieces.Select(p => p.Name))}. Shown as the whole stack, as KSP flies it."
                    : null,
            CountsAsChoices = piece is null ? [] : CountsAs(piece, stack!),
            CountsAs = piece is null ? null : CountsAs(piece, stack!).First(c => c.Choice == _sync.DockedChoice(piece.Name)),
            // After the pick is done: storing it rebuilds this page.
            SetCountsAs = piece is null ? null : choice => System.Windows.Application.Current.Dispatcher.BeginInvoke(() => _sync.SetDockedChoice(piece.Name, choice)),
        };
    }

    /// <summary>What a docked vessel can count as: the rule's pick, a craft of its own, or part of the stack.</summary>
    private static IReadOnlyList<CountsAsOption> CountsAs(DockedPiece piece, SavedVessel stack) =>
    [
        new($"Automatic ({(piece.IsCraftByDefault ? "its own craft" : $"part of {stack.Name}")})", null),
        new("Its own craft", true),
        new($"Part of {stack.Name}", false),
    ];

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Designs are rebuilt when the library has re-read them (it follows Crafts and the part catalog itself).
        if (e.PropertyName is nameof(SaveSync.Save) or nameof(PartData.Catalog) or nameof(DesignLibrary.Entries)) Rebuild();
        if (e.PropertyName is nameof(PartData.Summary)) OnPropertyChanged(nameof(PartDataSummary));
    }

    private void Rebuild()
    {
        Tree.Clear();
        var save = _sync.Save;
        var catalog = _sync.Parts.Catalog;
        if (save is not null)
        {
            var inFlight = new VesselTreeItem("In flight", "", "");
            var shown = save.Vessels.Where(v => ShowAll || SaveImport.IsUsuallyWanted(v));
            foreach (var group in shown.GroupBy(v => v.Orbit?.BodyName ?? "Unknown").OrderBy(g => BodyOrder(g.Key)))
            {
                var body = new VesselTreeItem(group.Key, group.Count() == 1 ? "1 vessel" : $"{group.Count()} vessels", "●");
                foreach (var v in group.OrderBy(v => v.Type).ThenBy(v => v.Name))
                {
                    var report = VesselAnalysis.Analyze(v, catalog);
                    var dv = report.Engines.Any(e => e.CountedForDeltaV) ? $" · {Units.Dv(report.DeltaVVacuum)}" : "";
                    var item = new VesselTreeItem(v.Name, $"{SaveImport.TypeText(v.Type)} · {Situation(v.Situation)}{dv}", "◇", v);
                    // The vessels docked into it, each on its own: a craft of its own, or a part of the stack.
                    foreach (var piece in _sync.DockedIn(v))
                    {
                        var own = VesselAnalysis.Analyze(piece.Craft, catalog);
                        var pieceDv = own.Engines.Any(e => e.CountedForDeltaV) ? $" · {Units.Dv(own.DeltaVVacuum)}" : "";
                        var role = piece.IsCraft(_sync.DockedChoice(piece.Name)) ? "docked craft" : "station part";
                        item.Children.Add(new VesselTreeItem(piece.Name, $"{SaveImport.TypeText(piece.Type)} · {role}{pieceDv}", "⧉", piece.Craft));
                    }
                    body.Children.Add(item);
                }
                inFlight.Children.Add(body);
            }
            if (inFlight.Children.Count > 0) Tree.Add(inFlight);
        }

        if (_designs.Entries.Count > 0)
        {
            var designs = new VesselTreeItem("Saved designs", "Ships in the VAB and SPH", "");
            foreach (var facility in _designs.Entries.GroupBy(d => d.File.Facility))
            {
                var building = new VesselTreeItem(facility.Key, facility.Key == "VAB" ? "Vehicle Assembly Building" : "Spaceplane Hangar", "●");
                foreach (var design in facility)
                {
                    if (design.Report is not { } report) continue;
                    var dv = report.Stages.Count > 0 ? $"{Units.Dv(report.TotalDeltaVVacuum)} · {StageCount(report)} · " : "";
                    building.Children.Add(new VesselTreeItem(design.Title, $"{dv}{Units.Tonnes(report.LaunchMassT)}", "◇", craft: design.File));
                }
                if (building.Children.Count > 0) designs.Children.Add(building);
            }
            if (designs.Children.Count > 0) Tree.Add(designs);
        }

        var leaves = Leaves(Tree).ToList();
        var selected = leaves.FirstOrDefault(i => i.Key == _selectedId) ?? leaves.FirstOrDefault();
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

    private static string StageCount(DesignReport report) => report.Stages.Count == 1 ? "1 stage" : $"{report.Stages.Count} stages";

    private static IEnumerable<VesselTreeItem> Leaves(IEnumerable<VesselTreeItem> items) =>
        items.SelectMany(i => (i.Key is null ? [] : new[] { i }).Concat(Leaves(i.Children)));

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

/// <summary>One of a vessel's logged flights, on its page: how it went, when, and the figures.</summary>
public sealed record VesselFlightRow(long Id, string Heading, string Day, string Figures, string Launched);

/// <summary>One thing a docked vessel can count as; <paramref name="Choice"/> null for the rule's pick.</summary>
public sealed record CountsAsOption(string Name, bool? Choice)
{
    public override string ToString() => Name;
}

/// <summary>Everything about one vessel: status, Δv, fuel, engines, parts, and its logged flights.</summary>
public sealed class VesselDetailViewModel
{
    private CountsAsOption? _countsAs;

    /// <summary>What's docked: where this vessel is docked, or what's docked into it. Null when nothing is.</summary>
    public string? DockedNote { get; init; }

    public bool HasDockedNote => DockedNote is not null;

    /// <summary>For a vessel docked into another: what it can count as (empty otherwise).</summary>
    public IReadOnlyList<CountsAsOption> CountsAsChoices { get; init; } = [];

    public bool IsDockedPiece => CountsAsChoices.Count > 0;

    /// <summary>What it counts as now; picking another stores it.</summary>
    public CountsAsOption? CountsAs
    {
        get => _countsAs;
        set
        {
            if (value is null || value == _countsAs) return;
            var first = _countsAs is null;
            _countsAs = value;
            if (!first) SetCountsAs?.Invoke(value.Choice);
        }
    }

    public Action<bool?>? SetCountsAs { get; init; }

    /// <param name="bodyRadiusM">Radius of the body it orbits, to show altitudes; null if unknown.</param>
    /// <param name="flights">Its flights in the flight log, newest first.</param>
    public VesselDetailViewModel(SavedVessel vessel, double saveUt, PartCatalog catalog, double? bodyRadiusM,
        IReadOnlyList<FlightLogSummary>? flights = null)
    {
        Flights = (flights ?? []).Select(l => new FlightLogItem(l))
            .Select(i => new VesselFlightRow(i.Id, i.Rocket == vessel.Name ? i.Outcome : $"{i.Outcome} · as {i.Rocket}", i.LaunchDay, i.Figures, i.Launched))
            .ToList();

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

    public IReadOnlyList<VesselFlightRow> Flights { get; }
    public bool HasFlights => Flights.Count > 0;
    public string FlightsTitle => Flights.Count == 0 ? "Flight log" : $"Flight log ({Flights.Count})";

    /// <summary>Opens a flight in the Flight log and Flight report (set by the Vessels pane).</summary>
    public RelayCommand<VesselFlightRow>? OpenLogCommand { get; init; }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}

/// <summary>One stage row of a design, in firing order.</summary>
public sealed record StageRow(string Stage, string DeltaVVacuum, string DeltaVSeaLevel, string Twr, string BurnTime, string Mass, string Engines);

/// <summary>A saved design: total Δv, launch mass and TWR, stage-by-stage table, what it carries and is built from.</summary>
public sealed class DesignDetailViewModel
{
    public DesignDetailViewModel(DesignReport report, CraftFileInfo file)
    {
        Title = report.Name;
        Subtitle = $"{(file.Facility == "SPH" ? "Spaceplane Hangar" : "Vehicle Assembly Building")} design · " +
                   (file.IsAutoSave ? $"open in the editor now (auto-saved {file.LastWriteTime:g})" : $"saved {file.LastWriteTime:g}");
        HasStages = report.Stages.Count > 0;
        DeltaV = HasStages ? Units.Dv(report.TotalDeltaVVacuum) : "—";
        DeltaVNote = HasStages
            ? $"in vacuum, over {report.Stages.Count} stage{(report.Stages.Count == 1 ? "" : "s")} · launch TWR {report.LaunchTwr:0.00} at sea level"
            : "no stage with an engine and fuel it can reach";

        Summary =
        [
            new("Mass at launch", Units.Tonnes(report.LaunchMassT)),
            new("Dry mass (all parts)", Units.Tonnes(report.DryMassT)),
            new("Parts", report.PartCount.ToString("N0")),
            new("File", System.IO.Path.GetFileName(file.Path)),
        ];
        Stages = report.Stages.Select(s => new StageRow(
            s.Stage.ToString(),
            Units.Dv(s.DeltaVVacuum),
            Units.Dv(s.DeltaVSeaLevel),
            $"{s.TwrSeaLevel:0.00} / {s.TwrVacuum:0.00}",
            Units.BurnTime(s.BurnTimeS),
            $"{Units.Tonnes(s.StartMassT)} → {Units.Tonnes(s.EndMassT)}",
            string.Join(", ", s.Engines.Select(e => e.Count == 1 ? e.Title : $"{e.Count} × {e.Title}")))).ToList();
        Resources = report.Resources
            .Select(r => new ResourceRow(r.DisplayName, $"{r.Amount:#,0.##} / {r.MaxAmount:#,0.##}", r.Fraction, $"{r.Fraction:0%}",
                r.MassT > 0 ? Units.Tonnes(r.MassT) : "—"))
            .ToList();
        Notes = report.Notes;
        Parts = report.Parts.Select(p => new Fact(p.Title, $"× {p.Count}")).ToList();
        PartsTitle = $"Parts ({report.PartCount})";
    }

    public string Title { get; }
    public string Subtitle { get; }
    public bool HasStages { get; }
    public string DeltaV { get; }
    public string DeltaVNote { get; }
    public IReadOnlyList<Fact> Summary { get; }
    public IReadOnlyList<StageRow> Stages { get; }
    public IReadOnlyList<ResourceRow> Resources { get; }
    public bool HasResources => Resources.Count > 0;
    public IReadOnlyList<string> Notes { get; }
    public bool HasNotes => Notes.Count > 0;
    public IReadOnlyList<Fact> Parts { get; }
    public string PartsTitle { get; }
}
