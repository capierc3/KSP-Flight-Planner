using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.App.ViewModels;

/// <summary>A body to work out TWR on.</summary>
public sealed record TwrBody(string Name, double SurfaceGravity, double PressureAtm)
{
    public override string ToString() => Name;

    /// <summary>Firing in space: no air, and TWR measured against Kerbin's surface gravity (as KSP's readouts do).</summary>
    public static readonly TwrBody InSpace = new("In space", 9.81, 0);

    /// <summary>The stock bodies with a surface, in KSP's order, and the home world among them.</summary>
    public static (IReadOnlyList<TwrBody> Bodies, TwrBody Home) Stock(SaveSync sync)
    {
        var bodies = sync.StockBodies().Values
            // Stars and gas giants have nothing to stand on.
            .Where(b => b.BodyClass != "Star" && b.Name != "Jool")
            .OrderBy(b => Array.IndexOf(SavedOrbit.StockBodyOrder, b.Name) is var i and >= 0 ? i : int.MaxValue).ThenBy(b => b.Name)
            .Select(b => (Body: b, Twr: new TwrBody(b.Name, b.SurfaceGravity, b.Atmosphere?.SeaLevelPressureAtm ?? 0)))
            .ToList();
        var home = bodies.FirstOrDefault(b => b.Body.IsDefault).Twr ?? bodies.Select(b => b.Twr).FirstOrDefault() ?? new TwrBody("Kerbin", 9.81, 1);
        return (bodies.Count == 0 ? [home] : bodies.Select(b => b.Twr).ToList(), home);
    }
}

/// <summary>One stage of the design in the Engineering table, in firing order.</summary>
public sealed record EngineeringStageRow(StageResult Result, string Stage, string DeltaVVacuum, string DeltaVSeaLevel, string Twr,
    TwrRating Rating, string BurnTime, string Mass, string Engines, string Drops);

/// <summary>A part category's share of the design's mass and cost.</summary>
public sealed record MassShareRow(string Title, string Mass, double Fraction, string Share, string Cost, string Count);

/// <summary>
/// Parts of one kind that leave together: how many, their mass and cost, and whether the save can build them.
/// <paramref name="Group"/> is the heading they're listed under (the stage that drops them, with that drop's totals).
/// </summary>
public sealed record BuiltPartRow(string Group, string Title, string Count, string Mass, string Cost, PartStatus Status, string StatusText,
    KspPart? Part)
{
    public bool IsUnlocked => Status == PartStatus.Unlocked;
}

/// <summary>A stage's TWR on one body, for the stage detail in the Rocket pane.</summary>
public sealed record BodyTwrRow(string Body, string Twr, TwrRating Rating, string Detail);

/// <summary>The stage picked in the Engineering document, shown in the Rocket pane.</summary>
public sealed record EngineeringStageDetail(int Stage, string Title, string Subtitle, IReadOnlyList<Fact> Facts, IReadOnlyList<Fact> Engines,
    IReadOnlyList<BodyTwrRow> Twr, IReadOnlyList<Fact> Drops, string DropsTitle);

/// <summary>
/// The Engineering document: a saved design (.craft) taken apart — stage by stage, by mass and cost, which parts each
/// stage drops, what the save can't build yet, and how it fits the open mission. KSP writes the file when you save in
/// the editor, and the Ships folder is watched, so it follows the design as you work on it. The stage or part picked
/// here shows in the Rocket pane (a stage) or the Part pane (a part).
/// </summary>
public sealed class EngineeringViewModel : ObservableObject
{
    private static readonly CultureInfo C = CultureInfo.CurrentCulture;

    private readonly DesignLibrary _designs;
    private readonly SaveSync _sync;
    private readonly PartAvailability _parts;
    private DesignEntry? _selected;
    private string? _selectedPath;
    private bool _followLatest = true;
    private TwrBody? _body;
    private CraftBreakdown? _breakdown;
    private EngineeringStageRow? _selectedStage;
    private BuiltPartRow? _selectedPart;
    private EngineeringStageDetail? _detail;
    private string? _problem;

    public EngineeringViewModel(DesignLibrary designs, SaveSync sync, PartAvailability parts, MissionBuilderViewModel mission)
    {
        _designs = designs;
        _sync = sync;
        _parts = parts;
        Mission = mission;
        CheckWithMissionCommand = new RelayCommand(CheckWithMission, () => CanCheckWithMission);

        designs.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DesignLibrary.Entries)) RefreshDesigns(); };
        parts.Changed += Rebuild;
        mission.PropertyChanged += OnMissionChanged;
        RefreshBodies();
        RefreshDesigns();
    }

    public MissionBuilderViewModel Mission { get; }

    /// <summary>Saved designs that could be read, newest first.</summary>
    public IReadOnlyList<DesignEntry> Designs { get; private set; } = [];

    public DesignEntry? SelectedDesign
    {
        get => _selected;
        set
        {
            // The picker passes through null while the list is refilled.
            if (value is null || ReferenceEquals(value, _selected)) return;
            _selected = value;
            _selectedPath = value.File.Path;
            // Picking an older design by hand stops following the editor's saves.
            if (Designs.Count > 0 && value != Designs[0]) FollowLatest = false;
            OnPropertyChanged();
            Rebuild();
        }
    }

    /// <summary>Always show the design saved most recently, so saving in the VAB/SPH brings it up here.</summary>
    public bool FollowLatest
    {
        get => _followLatest;
        set
        {
            if (!SetProperty(ref _followLatest, value) || !value || Designs.Count == 0) return;
            SelectedDesign = Designs[0];
        }
    }

    public IReadOnlyList<TwrBody> Bodies { get; private set; } = [];

    /// <summary>The body the stage table's TWR is worked out on (the home world unless picked).</summary>
    public TwrBody? SelectedBody
    {
        get => _body;
        set
        {
            if (value is null || !SetProperty(ref _body, value)) return;
            Rebuild();
        }
    }

    public bool HasDesign => _breakdown is not null && _selected?.Report is not null;

    /// <summary>The design's name, for others offering to use it ("" with none).</summary>
    public string DesignTitle => HasDesign ? Title : "";

    /// <summary>
    /// The design's parts that stay to the end (what flies the long part of the mission) that make, use or store
    /// electric charge, with how many of each.
    /// </summary>
    public IReadOnlyList<(KspPart Part, int Count)> DesignPowerParts =>
        _breakdown?.Parts.Where(p => p.SeparationStage < 0 && p.Definition is { } d && PowerBudgetViewModel.IsPowerPart(d))
            .GroupBy(p => p.Name).Select(g => (g.First().Definition!, g.Count())).ToList() ?? [];

    /// <summary>The design's parts that carry an antenna (probe cores' internal ones aside), with how many of each.</summary>
    public IReadOnlyList<(KspPart Part, int Count)> DesignAntennas =>
        _breakdown?.Parts.Where(p => p.Definition is { Antennas.Count: > 0 } d && d.Antennas.Any(a => a.Type != "INTERNAL"))
            .GroupBy(p => p.Name).Select(g => (g.First().Definition!, g.Count())).ToList() ?? [];

    /// <summary>Why nothing is shown: no save, no designs, or the file couldn't be read.</summary>
    public string Empty => _problem
        ?? (_sync.Save is null && Designs.Count == 0 ? "No KSP save is linked. Choose your KSP folder and save in Tools › Settings."
            : Designs.Count == 0 ? "No saved designs yet. Save a ship in the VAB or SPH and it shows up here."
            : "");

    public string Title { get; private set; } = "";
    public string Subtitle { get; private set; } = "";
    public IReadOnlyList<Fact> Headline { get; private set; } = [];
    public IReadOnlyList<EngineeringStageRow> Stages { get; private set; } = [];
    public bool HasStages => Stages.Count > 0;
    public string TwrHeader => $"TWR {SelectedBody?.Name ?? ""}";
    public IReadOnlyList<MassShareRow> MassShares { get; private set; } = [];
    public IReadOnlyList<ResourceRow> Resources { get; private set; } = [];
    public bool HasResources => Resources.Count > 0;
    /// <summary>Every kind of part, grouped by the stage that drops it (firing order), the parts that stay last.</summary>
    public IReadOnlyList<BuiltPartRow> Parts { get; private set; } = [];
    public IReadOnlyList<string> Warnings { get; private set; } = [];
    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>
    /// Picking a stage shows it in the Rocket pane (and brings that forward) and clears a picked part. The lists pass
    /// null when they lose their selection that way; that isn't a pick.
    /// </summary>
    public EngineeringStageRow? SelectedStage
    {
        get => _selectedStage;
        set
        {
            if (value is null || !SetProperty(ref _selectedStage, value)) return;
            _selectedPart = null;
            OnPropertyChanged(nameof(SelectedPart));
            Detail = StageDetail(value);
            StagePicked?.Invoke();
        }
    }

    /// <summary>
    /// Picking a part shows it in the Part pane and clears the stage list's pick; the Rocket pane keeps showing the
    /// last stage picked until another stage is.
    /// </summary>
    public BuiltPartRow? SelectedPart
    {
        get => _selectedPart;
        set
        {
            if (value is null || !SetProperty(ref _selectedPart, value)) return;
            _selectedStage = null;
            OnPropertyChanged(nameof(SelectedStage));
            if (value.Part is { } part)
            {
                var node = part.TechRequired is { } id ? _parts.Catalog.TechTree.FirstOrDefault(n => n.Id == id)?.Title : null;
                PartPicked?.Invoke(new PartItem(part, value.Status, node));
            }
        }
    }

    /// <summary>A stage was picked: the Rocket pane should come into view.</summary>
    public event Action? StagePicked;

    /// <summary>A part was picked: the Part pane should show it.</summary>
    public event Action<PartItem>? PartPicked;

    /// <summary>The last stage picked, which the Rocket pane shows while this document is in front.</summary>
    public EngineeringStageDetail? Detail { get => _detail; private set { if (SetProperty(ref _detail, value)) OnPropertyChanged(nameof(HasDetail)); } }

    public bool HasDetail => Detail is not null;

    // ---------------- The open mission ----------------

    /// <summary>The open mission's design check is on this design (its results are the Mission's).</summary>
    public bool MissionChecksThis => _selectedPath is not null && Mission.SelectedDesign?.Design?.File.Path == _selectedPath;

    public bool CanCheckWithMission => !MissionChecksThis && _selectedPath is not null &&
                                       Mission.DesignChoices.Any(c => c.Design is not null && c.Path == _selectedPath);

    public string MissionFitHint => MissionChecksThis
        ? $"Checked against “{Mission.MissionName}”, as in the Rocket pane."
        : _selected?.Report is { Stages.Count: 0 } ? "This design has no stage with Δv, so there's nothing to check against a mission."
        : $"“{Mission.MissionName}” checks {(Mission.SelectedDesign?.Design is { } d ? $"“{d.Title}”" : "no design")}. " +
          "Check this one instead to see if it has the Δv and TWR for the route.";

    public RelayCommand CheckWithMissionCommand { get; }

    private void CheckWithMission()
    {
        if (Mission.DesignChoices.FirstOrDefault(c => c.Design is not null && c.Path == _selectedPath) is { } choice)
            Mission.SelectedDesign = choice;
    }

    private void OnMissionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MissionBuilderViewModel.SelectedDesign) or nameof(MissionBuilderViewModel.MissionName) or nameof(MissionBuilderViewModel.HasDesign))
            RaiseMissionFit();
    }

    private void RaiseMissionFit()
    {
        OnPropertyChanged(nameof(MissionChecksThis));
        OnPropertyChanged(nameof(CanCheckWithMission));
        OnPropertyChanged(nameof(MissionFitHint));
        CommandManager.InvalidateRequerySuggested();
    }

    // ---------------- Building it ----------------

    private void RefreshBodies()
    {
        (Bodies, _body) = TwrBody.Stock(_sync);
        OnPropertyChanged(nameof(Bodies));
        OnPropertyChanged(nameof(SelectedBody));
    }

    private void RefreshDesigns()
    {
        Designs = _designs.Entries.Where(d => d.Report is not null).OrderByDescending(d => d.File.LastWriteTime).ToList();
        OnPropertyChanged(nameof(Designs));
        _selected = FollowLatest ? Designs.FirstOrDefault()
            : Designs.FirstOrDefault(d => d.File.Path == _selectedPath) ?? Designs.FirstOrDefault();
        _selectedPath = _selected?.File.Path;
        OnPropertyChanged(nameof(SelectedDesign));
        Rebuild();
    }

    private void Rebuild()
    {
        var keepStage = _selectedStage?.Result.Stage;
        var shownStage = _detail?.Stage;
        var keepPart = _selectedPart?.Title;
        _breakdown = null;
        _problem = null;
        if (_selected is { Report: { } report } entry)
        {
            try
            {
                _breakdown = CraftBreakdown.Build(CraftReader.Read(entry.File.Path), _sync.Parts.Catalog, report);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                _problem = $"Couldn't read {Path.GetFileName(entry.File.Path)}: {ex.Message}";
            }
        }

        if (_breakdown is { } b && _selected is { Report: { } design } selected) Fill(b, design, selected.File);
        else
        {
            Title = Subtitle = "";
            Headline = [];
            Stages = [];
            MassShares = [];
            Resources = [];
            Parts = [];
            Warnings = [];
        }

        foreach (var name in new[] { nameof(HasDesign), nameof(DesignTitle), nameof(DesignAntennas), nameof(DesignPowerParts), nameof(Empty), nameof(Title), nameof(Subtitle), nameof(Headline), nameof(Stages),
                     nameof(HasStages), nameof(TwrHeader), nameof(MassShares), nameof(Resources), nameof(HasResources), nameof(Parts),
                     nameof(Warnings), nameof(HasWarnings) })
            OnPropertyChanged(name);
        RaiseMissionFit();

        // Keep what was picked (the same stage, or the same kind of part) when the design is saved again, without
        // pulling panes forward: nobody picked anything.
        _selectedStage = keepStage is { } s ? Stages.FirstOrDefault(r => r.Result.Stage == s) : null;
        _selectedPart = _selectedStage is null && keepPart is { } p ? Parts.FirstOrDefault(r => r.Title == p) : null;
        Detail = Stages.FirstOrDefault(r => r.Result.Stage == shownStage) is { } shown ? StageDetail(shown) : null;
        OnPropertyChanged(nameof(SelectedStage));
        OnPropertyChanged(nameof(SelectedPart));
    }

    private void Fill(CraftBreakdown b, DesignReport design, CraftFileInfo file)
    {
        var body = SelectedBody ?? new TwrBody("Kerbin", 9.81, 1);
        Title = design.Name;
        Subtitle = $"{(file.Facility == "SPH" ? "Spaceplane Hangar" : "Vehicle Assembly Building")} · " +
                   (file.IsAutoSave ? $"open in the editor (auto-saved {file.LastWriteTime:g})" : $"saved {file.LastWriteTime:g}") +
                   $" · {Path.GetFileName(file.Path)}";

        var headline = new List<Fact>
        {
            new("Δv (vacuum)", design.Stages.Count > 0 ? $"{Units.Dv(design.TotalDeltaVVacuum)} in {Plural(design.Stages.Count, "stage")}" : "—"),
            new("Launch mass", Units.Tonnes(design.LaunchMassT)),
            new("Cost", $"{Funds(b.Cost)}  ({Funds(b.KeptCost)} stays to the end)"),
            new("Parts", design.PartCount.ToString("N0", C)),
        };
        if (design.Stages.Count > 0)
        {
            headline.Insert(1, new("Launch TWR", $"{design.LaunchTwr:0.00} at sea level"));
            headline.Add(new("Left at the end", $"{Units.Tonnes(b.FinalMassT)}  ({b.FinalMassT / design.LaunchMassT:0.0%} of launch mass)"));
        }
        if (b.CrewCapacity > 0) headline.Add(new("Crew seats", b.CrewCapacity.ToString(C)));
        if (b.WeakestKeptPart is { Definition: { } weak })
            headline.Add(new("Weakest part at the end", $"breaks above {weak.CrashToleranceMs:0.#} m/s  ({weak.Title})"));
        Headline = headline;

        Stages = design.Stages.Select(s =>
        {
            var twr = CraftBreakdown.TwrAt(s, body.SurfaceGravity, body.PressureAtm);
            var leaves = NextDrop(s.Stage);
            return new EngineeringStageRow(s, s.Stage.ToString(C), Units.Dv(s.DeltaVVacuum), Units.Dv(s.DeltaVSeaLevel), $"{twr:0.00}",
                Rate(twr), Units.BurnTime(s.BurnTimeS), $"{Units.Tonnes(s.StartMassT)} → {Units.Tonnes(s.EndMassT)}",
                string.Join(", ", s.Engines.Select(e => e.Count == 1 ? e.Title : $"{e.Count} × {e.Title}")),
                leaves is null ? "" : $"{Plural(leaves.Parts.Count, "part")}, {Units.Tonnes(leaves.Parts.Sum(p => p.DryMassT))} dry");
        }).ToList();

        var total = b.DryMassT + b.ResourceMassT;
        MassShares = b.Categories
            .Select(c => new MassShareRow(PartCategories.Title(c.Category), Units.Tonnes(c.DryMassT), total > 0 ? c.DryMassT / total : 0,
                total > 0 ? (c.DryMassT / total).ToString("0%", C) : "", Funds(c.Cost), $"× {c.Count}"))
            .Prepend(new MassShareRow("Fuel & resources", Units.Tonnes(b.ResourceMassT), total > 0 ? b.ResourceMassT / total : 0,
                total > 0 ? (b.ResourceMassT / total).ToString("0%", C) : "", Funds(design.Resources.Sum(r => r.Amount *
                    (_sync.Parts.Catalog.Resources.GetValueOrDefault(r.Name)?.UnitCost ?? 0))), ""))
            .Where(r => r.Fraction > 0 || r.Title != "Fuel & resources")
            .ToList();

        Resources = design.Resources
            .Select(r => new ResourceRow(r.DisplayName, $"{r.Amount:#,0.##} / {r.MaxAmount:#,0.##}", r.Fraction, $"{r.Fraction:0%}",
                r.MassT > 0 ? Units.Tonnes(r.MassT) : "—"))
            .ToList();

        Parts = b.Drops.SelectMany(d =>
        {
            var group = (d.StaysToTheEnd ? "Stays to the end" : $"Dropped when stage {d.Stage} fires") +
                        $"  ·  {Plural(d.Parts.Count, "part")}, {Units.Tonnes(d.MassT)}, {Funds(d.Cost)}";
            return d.Parts.GroupBy(p => p.Title).Select(g =>
            {
                var part = g.First().Definition;
                var status = part is null ? PartStatus.Locked : _parts.StatusOf(part);
                var statusText = part is null ? "not in the part data" : status switch
                {
                    PartStatus.Unlocked => "",
                    PartStatus.Planned => "planned",
                    PartStatus.NotBought => "not bought",
                    _ => "locked",
                };
                return new BuiltPartRow(group, g.Key, $"× {g.Count()}", Units.Tonnes(g.Sum(p => p.MassT)), Funds(g.Sum(p => p.Cost)), status,
                    statusText, part);
            }).OrderByDescending(r => r.Part?.MassT ?? 0).ThenBy(r => r.Title);
        }).ToList();

        var locked = Parts.Where(p => !p.IsUnlocked && p.Part is not null).Select(p => p.Title).Distinct().ToList();
        var warnings = new List<string>(b.Warnings);
        if (locked.Count > 0)
            warnings.Insert(0, $"This save can't build it yet: {string.Join(", ", locked.Take(5))}{(locked.Count > 5 ? $" and {locked.Count - 5} more" : "")} " +
                               $"{(locked.Count == 1 ? "isn't" : "aren't")} unlocked (see the tech tree).");
        warnings.AddRange(b.Notes);
        warnings.AddRange(design.Notes);
        Warnings = warnings;
    }

    private EngineeringStageDetail StageDetail(EngineeringStageRow row)
    {
        var s = row.Result;
        var order = Stages.ToList().IndexOf(row) + 1;
        var facts = new List<Fact>
        {
            new("Δv vacuum", Units.Dv(s.DeltaVVacuum)),
            new("Δv sea level", Units.Dv(s.DeltaVSeaLevel)),
            new("Burn time", Units.BurnTime(s.BurnTimeS)),
            new("Thrust vacuum", $"{s.ThrustVacuumKn.ToString("N1", C)} kN"),
            new("Thrust sea level", $"{s.ThrustSeaLevelKn.ToString("N1", C)} kN"),
            new("Mass at start", Units.Tonnes(s.StartMassT)),
            new("Mass at end", Units.Tonnes(s.EndMassT)),
            new("Propellant", Units.Tonnes(s.StartMassT - s.EndMassT)),
        };
        if (s.ThrustVacuumKn > 0 && s.StartMassT > s.EndMassT)
            facts.Add(new("Isp vacuum", $"{s.DeltaVVacuum / (VesselAnalysis.G0 * Math.Log(s.StartMassT / s.EndMassT)):0} s"));

        var twr = Bodies.Select(body =>
        {
            var value = CraftBreakdown.TwrAt(s, body.SurfaceGravity, body.PressureAtm);
            var where = $"{body.SurfaceGravity / VesselAnalysis.G0:0.00} g" + (body.PressureAtm > 0 ? $", {body.PressureAtm:0.###} atm" : "");
            return new BodyTwrRow(body.Name, $"{value:0.00}", Rate(value), where);
        }).ToList();

        // What the next firing drops once this stage is spent; after the last stage, what's left.
        var next = NextDrop(s.Stage);
        var (dropsTitle, dropped) = next is null
            ? ("Left at the end", _breakdown?.Drops.Where(d => d.StaysToTheEnd).SelectMany(d => d.Parts) ?? [])
            : ($"Dropped next (stage {next.Stage} fires)", next.Parts);
        return new EngineeringStageDetail(s.Stage, $"Stage {s.Stage}", $"Fires {Ordinal(order)} of {Stages.Count} · TWR with its tanks full",
            facts, s.Engines.Select(e => new Fact(e.Title, $"× {e.Count}")).ToList(), twr,
            dropped.GroupBy(p => p.Title).Select(g => new Fact(g.Key, $"× {g.Count()}")).ToList(), dropsTitle);
    }

    /// <summary>
    /// The parts the next firing after a stage drops. A stage's own decouplers fire as it starts, so what they drop is
    /// gone before it burns; what it leaves behind goes with the next firing.
    /// </summary>
    private PartDrop? NextDrop(int stage) =>
        _breakdown?.Drops.Where(d => !d.StaysToTheEnd && d.Stage < stage).OrderByDescending(d => d.Stage).FirstOrDefault();

    private static TwrRating Rate(double twr) => twr < 1 ? TwrRating.TooLow : twr < SurfaceTwr.LowTwr ? TwrRating.Low : TwrRating.Good;

    private static string Funds(double cost) => "√" + cost.ToString("N0", C);

    private static string Plural(int count, string noun) => $"{count.ToString("N0", C)} {noun}{(count == 1 ? "" : "s")}";

    private static string Ordinal(int n) => n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });
}
