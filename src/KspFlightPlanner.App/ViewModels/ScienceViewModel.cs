using System.Collections.ObjectModel;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App.ViewModels;

/// <summary>A pick in the vessel box: a vessel from the save, or every experiment the game has.</summary>
public sealed record ScienceVesselChoice(SavedVessel? Vessel, string Title, string? BodyName)
{
    public override string ToString() => Title;
}

/// <summary>A pick in the experiment box: one experiment, or all of them (null).</summary>
public sealed record ScienceExperimentChoice(ScienceExperimentDef? Experiment, string Title)
{
    public override string ToString() => Title;
}

/// <summary>One cell as the pane shows it: the science one run of each experiment would earn, with the breakdown as a tooltip.</summary>
public sealed record ScienceCellView(string Text, string Detail, bool Pays, bool Empty);

/// <summary>One row of the table.</summary>
public sealed record ScienceRowView(string Biome, string Total, IReadOnlyList<ScienceCellView> Cells, bool IsAnywhere);

/// <summary>One of the best places to scan.</summary>
public sealed record SciencePlaceView(string Value, string Where, string Experiments);

/// <summary>
/// Tools › Science (docs/SCIENCE.md): what's still left to collect at a body, per biome and situation, for the experiments
/// a vessel carries. Read from the save's R&amp;D records and the data vessels carry, with the game's experiment
/// definitions fetched once through the planner's add-on.
/// </summary>
public sealed class ScienceViewModel : ObservableObject
{
    /// <summary>A cell pays when one run there earns at least this much.</summary>
    public const double PaysAtLeast = 1;

    private readonly ScienceRepository _repository;
    private readonly SaveSync _sync;
    private readonly KspLink _link;
    private ScienceCatalog? _catalog;
    private ScienceVesselChoice? _vessel;
    private string? _vesselId;
    private string? _body;
    private ScienceExperimentChoice? _experiment;
    private bool _refreshingChoices;
    private IReadOnlyList<string> _columns = [];
    private IReadOnlyList<ScienceRowView> _rows = [];
    private IReadOnlyList<SciencePlaceView> _best = [];
    private string _summary = "";
    private string _status = "";
    private bool _working;
    private bool _autoFetchTried;

    public ScienceViewModel(ScienceRepository repository, SaveSync sync, KspLink link)
    {
        _repository = repository;
        _sync = sync;
        _link = link;
        FetchCommand = new RelayCommand(() => _ = FetchAsync(), () => !_working);
        sync.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SaveSync.Save)) Reload(); };
        link.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(KspLink.State)) return;
            OnPropertyChanged(nameof(NeedsCatalog));
            // Fetch on its own the first time the game is there and the catalog isn't.
            if (_link.IsConnected && _catalog is null && !_autoFetchTried)
            {
                _autoFetchTried = true;
                _ = FetchAsync();
            }
        };
        Reload();
    }

    public ObservableCollection<ScienceVesselChoice> Vessels { get; } = [];
    public ObservableCollection<string> Bodies { get; } = [];
    public ObservableCollection<ScienceExperimentChoice> Experiments { get; } = [];

    public ScienceVesselChoice? Vessel
    {
        get => _vessel;
        set
        {
            if (_refreshingChoices || !SetProperty(ref _vessel, value)) return;
            _vesselId = value?.Vessel?.Id;
            // Following a vessel moves the body to where it is.
            if (value?.BodyName is { } body && Bodies.Contains(body)) Body = body;
            RefreshExperiments();
            Refresh();
        }
    }

    public string? Body
    {
        get => _body;
        set
        {
            if (_refreshingChoices || !SetProperty(ref _body, value)) return;
            Refresh();
        }
    }

    public ScienceExperimentChoice? Experiment
    {
        get => _experiment;
        set
        {
            if (_refreshingChoices || !SetProperty(ref _experiment, value)) return;
            Refresh();
        }
    }

    /// <summary>Situation headings, after the biome column.</summary>
    public IReadOnlyList<string> Columns { get => _columns; private set => SetProperty(ref _columns, value); }

    public IReadOnlyList<ScienceRowView> Rows { get => _rows; private set => SetProperty(ref _rows, value); }

    /// <summary>The cells that pay most, best first.</summary>
    public IReadOnlyList<SciencePlaceView> Best
    {
        get => _best;
        private set { if (SetProperty(ref _best, value)) OnPropertyChanged(nameof(HasBest)); }
    }

    public bool HasBest => Best.Count > 0;

    /// <summary>"Gravity Probe could still earn 412 at Minmus".</summary>
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>The game's experiment data hasn't been fetched for this save yet.</summary>
    public bool NeedsCatalog => _catalog is null;

    public bool HasCatalog => _catalog is not null;

    public bool IsSandbox => _sync.Save?.Mode == "SANDBOX";

    public RelayCommand FetchCommand { get; }

    private async Task FetchAsync()
    {
        if (_working) return;
        if (!_link.IsConnected)
        {
            Status = "Connect to KSP first (KSP › Connect to KSP); the experiment data comes from the game once per save.";
            return;
        }
        SetWorking(true);
        Status = "Fetching the game's experiments and bodies…";
        try
        {
            if (await _link.ScienceAddOnVersionAsync() is null)
            {
                Status = "The planner's add-on in KSP is older than this feature: build and install mod/KspFlightPlanner.AddOns again, then restart the game.";
                return;
            }
            var catalog = await _link.FetchScienceCatalogAsync();
            _repository.SaveCatalog(catalog);
            Status = $"Read {catalog.Experiments.Count} experiments and {catalog.Bodies.Count} bodies from the game.";
            Reload();
        }
        catch (Exception ex) when (ex is InvalidOperationException or KRPC.Client.RPCException or ArgumentException)
        {
            Status = $"Couldn't fetch the science data: {ex.Message}";
        }
        finally { SetWorking(false); }
    }

    private void SetWorking(bool working)
    {
        _working = working;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>KSP calls the star "Sun"; the save reader calls it Kerbol.</summary>
    private static string? ScienceName(string? body) => body == "Kerbol" ? "Sun" : body;

    /// <summary>The catalog and the save changed: rebuild the boxes, keeping the picks where they still exist.</summary>
    private void Reload()
    {
        _catalog = _repository.GetCatalog();
        OnPropertyChanged(nameof(NeedsCatalog));
        OnPropertyChanged(nameof(HasCatalog));
        OnPropertyChanged(nameof(IsSandbox));
        var save = _sync.Save;
        _refreshingChoices = true;
        try
        {
            Bodies.Clear();
            foreach (var b in _catalog?.Bodies ?? []) Bodies.Add(b.Name);

            Vessels.Clear();
            Vessels.Add(new ScienceVesselChoice(null, "Any experiment", null));
            if (_catalog is { } catalog && save is not null)
                foreach (var v in save.Vessels
                             .Where(v => v.Type is not ("Debris" or "Flag" or "SpaceObject" or "DeployedScienceController" or "DeployedSciencePart"))
                             .Where(v => ScienceGaps.Aboard(v, catalog).Count > 0)
                             .OrderBy(v => v.Name, StringComparer.CurrentCulture))
                    Vessels.Add(new ScienceVesselChoice(v, $"{v.Name} · {ScienceName(v.Orbit?.BodyName)}", ScienceName(v.Orbit?.BodyName)));
        }
        finally { _refreshingChoices = false; }

        _vessel = Vessels.FirstOrDefault(v => v.Vessel?.Id == _vesselId && _vesselId is not null) ?? Vessels.FirstOrDefault();
        OnPropertyChanged(nameof(Vessel));
        if (_body is null || !Bodies.Contains(_body))
            _body = _vessel?.BodyName is { } at && Bodies.Contains(at) ? at : Bodies.Contains("Kerbin") ? "Kerbin" : Bodies.FirstOrDefault();
        OnPropertyChanged(nameof(Body));
        RefreshExperiments();
        Refresh();
    }

    /// <summary>The experiments the picked vessel carries (or every experiment), keeping the pick if it's still there.</summary>
    private void RefreshExperiments()
    {
        var keep = _experiment?.Experiment?.Id;
        _refreshingChoices = true;
        try
        {
            Experiments.Clear();
            Experiments.Add(new ScienceExperimentChoice(null, _vessel?.Vessel is null ? "All experiments" : "Everything aboard"));
            foreach (var e in CurrentExperiments()) Experiments.Add(new ScienceExperimentChoice(e, e.Title));
        }
        finally { _refreshingChoices = false; }
        _experiment = Experiments.FirstOrDefault(e => e.Experiment?.Id == keep && keep is not null) ?? Experiments[0];
        OnPropertyChanged(nameof(Experiment));
    }

    private IReadOnlyList<ScienceExperimentDef> CurrentExperiments() =>
        _catalog is not { } catalog ? []
        : _vessel?.Vessel is { } v ? ScienceGaps.Aboard(v, catalog)
        : catalog.Experiments.Where(e => !e.IsPerObject).OrderBy(e => e.Title, StringComparer.CurrentCulture).ToList();

    private void Refresh()
    {
        var save = _sync.Save;
        if (_catalog is not { } catalog || save is null || _body is null || catalog.Body(_body) is not { } body)
        {
            Columns = [];
            Rows = [];
            Best = [];
            Summary = save is null ? "No KSP save is being followed (KSP › Follow a save)." : "";
            return;
        }

        var experiments = _experiment?.Experiment is { } one ? [one] : CurrentExperiments();
        var table = ScienceGaps.Table(body, experiments, save.ScienceSubjects, ScienceGaps.Carried(save));
        Columns = table.Situations.Select(ScienceGaps.Label).ToList();
        Rows = table.Rows.Select(r => new ScienceRowView(
            r.IsMiniBiome ? $"{r.Biome} (KSC)" : r.Biome,
            Points(r.NextRun),
            table.Situations.Select(s => CellView(r.Cell(s))).ToList(),
            r.Biome == ScienceGapTable.AnywhereRow)).ToList();

        Best = table.Rows
            .SelectMany(r => r.Cells.Where(c => c.NextRun >= PaysAtLeast).Select(c => (Row: r, Cell: c)))
            .OrderByDescending(x => x.Cell.NextRun).Take(8)
            .Select(x => new SciencePlaceView(Points(x.Cell.NextRun),
                x.Row.Biome == ScienceGapTable.AnywhereRow ? $"{ScienceGaps.Label(x.Cell.Situation)}, anywhere" : $"{x.Row.Biome}, {ScienceGaps.Label(x.Cell.Situation).ToLowerInvariant()}",
                string.Join(", ", x.Cell.Entries.Where(e => e.NextRun >= 0.05).OrderByDescending(e => e.NextRun).Select(e => $"{e.Title} {Points(e.NextRun)}"))))
            .ToList();

        var who = _vessel?.Vessel is { } v ? v.Name : "One run of every experiment";
        Summary = experiments.Count == 0
            ? "Nothing to show: no experiments."
            : $"{who} could still earn {Points(table.NextRun)} at {body.Name}" +
              (_experiment?.Experiment is { } picked ? $" with the {picked.Title.ToLowerInvariant()}" : "") +
              " (one run in each place, recovered).";
    }

    private static ScienceCellView CellView(ScienceGapCell? cell)
    {
        if (cell is null || cell.Entries.Count == 0) return new ScienceCellView("", "", false, true);
        var detail = string.Join("\n", cell.Entries.OrderByDescending(e => e.NextRun).Select(e =>
            $"{e.Title}: {Points(e.NextRun)} a run · {Points(e.Left)} of {Points(e.Cap)} left" +
            (e.CarriedCopies > 0 ? $" · {e.CarriedCopies} cop{(e.CarriedCopies == 1 ? "y" : "ies")} aboard already" : "")));
        var pays = cell.NextRun >= PaysAtLeast;
        return new ScienceCellView(pays ? Points(cell.NextRun) : "✓", detail, pays, false);
    }

    private static string Points(double value) => value >= 10 ? value.ToString("N0") : value.ToString("0.#");
}
