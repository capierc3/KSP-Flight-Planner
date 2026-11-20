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

/// <summary>A pick in the map's situation box.</summary>
public sealed record ScienceSituationChoice(ScienceSituation Situation, string Title)
{
    public override string ToString() => Title;
}

/// <summary>A pick in the "count as" box: recovered, sent home, or both.</summary>
public sealed record ScienceCountingChoice(ScienceCounting Counting, string Title)
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

/// <summary>One upcoming pass of the picked vessel: when, over where, and what a run pays then.</summary>
public sealed record SciencePassView(string When, string Value, string Where, string Experiments, bool IsNow);

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
    private readonly HashSet<string> _mapFetchTried = [];
    private ScienceGapTable? _table;
    private ScienceBiomeMap? _map;
    private ScienceSituationChoice? _mapSituation;
    private IReadOnlyDictionary<string, double> _mapValues = new Dictionary<string, double>();
    private System.Windows.Media.ImageSource? _mapImage;
    private string _mapLegend = "";
    private string _hoverText = "";
    private ScienceCountingChoice? _counting;
    private IReadOnlyList<SciencePassView> _passes = [];
    private string _passesNote = "";

    public ScienceViewModel(ScienceRepository repository, SaveSync sync, KspLink link)
    {
        _repository = repository;
        _sync = sync;
        _link = link;
        FetchCommand = new RelayCommand(() => _ = FetchAsync(), () => !_working);
        FetchMapCommand = new RelayCommand(() => _ = FetchMapAsync(), () => !_working);
        sync.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SaveSync.Save)) Reload(); };
        link.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(KspLink.State)) return;
            OnPropertyChanged(nameof(NeedsCatalog));
            // Fetch on its own the first time the game is there and the catalog isn't.
            if (_link.IsConnected && NeedsCatalog && !_autoFetchTried)
            {
                _autoFetchTried = true;
                _ = FetchAsync();
            }
            else AutoFetchMap();
        };
        Reload();
    }

    public ObservableCollection<ScienceVesselChoice> Vessels { get; } = [];
    public ObservableCollection<string> Bodies { get; } = [];
    public ObservableCollection<ScienceExperimentChoice> Experiments { get; } = [];

    public IReadOnlyList<ScienceCountingChoice> Countings { get; } =
    [
        new(ScienceCounting.Transmitted, "Sent home"),
        new(ScienceCounting.Recovered, "Recovered"),
        new(ScienceCounting.Both, "Both (recovered / sent)"),
    ];

    /// <summary>
    /// How results get home. A probe that never comes back only ever sends its data, which earns a share of the value
    /// (the part's transmit fraction), so picking a vessel sets this: sent for an uncrewed one, both for a crewed one.
    /// </summary>
    public ScienceCountingChoice? Counting
    {
        get => _counting;
        set
        {
            if (_refreshingChoices || !SetProperty(ref _counting, value)) return;
            Refresh();
        }
    }

    private ScienceCounting CountingMode => _counting?.Counting ?? ScienceCounting.Both;

    /// <summary>The counting a vessel starts with: uncrewed probes only send; crewed craft may come home.</summary>
    private void DefaultCounting()
    {
        var mode = _vessel?.Vessel is { } v && !v.Crew.Any() ? ScienceCounting.Transmitted : ScienceCounting.Both;
        _counting = Countings.First(c => c.Counting == mode);
        OnPropertyChanged(nameof(Counting));
    }

    public ScienceVesselChoice? Vessel
    {
        get => _vessel;
        set
        {
            if (_refreshingChoices || !SetProperty(ref _vessel, value)) return;
            _vesselId = value?.Vessel?.Id;
            DefaultCounting();
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

    /// <summary>The game's experiment data hasn't been fetched for this save yet (or was fetched before transmit fractions were).</summary>
    public bool NeedsCatalog => _catalog is null || _catalog.PartTransmit.Count == 0;

    public bool HasCatalog => _catalog is not null;

    public bool IsSandbox => _sync.Save?.Mode == "SANDBOX";

    public RelayCommand FetchCommand { get; }

    /// <summary>Situations the map can show (the table's columns).</summary>
    public ObservableCollection<ScienceSituationChoice> MapSituations { get; } = [];

    public ScienceSituationChoice? MapSituation
    {
        get => _mapSituation;
        set
        {
            if (_refreshingChoices || !SetProperty(ref _mapSituation, value)) return;
            DrawMap();
        }
    }

    /// <summary>The body flattened, north up and 180° W on the left: each biome coloured by what it pays in the picked situation.</summary>
    public System.Windows.Media.ImageSource? MapImage
    {
        get => _mapImage;
        private set { if (SetProperty(ref _mapImage, value)) OnPropertyChanged(nameof(HasMap)); }
    }

    public bool HasMap => MapImage is not null;

    /// <summary>"Red: Lowlands, 80 · grey: done".</summary>
    public string MapLegend { get => _mapLegend; private set => SetProperty(ref _mapLegend, value); }

    /// <summary>The biome under the mouse and what it pays.</summary>
    public string HoverText { get => _hoverText; private set => SetProperty(ref _hoverText, value); }

    /// <summary>The body has biomes but its map hasn't been fetched from the game yet.</summary>
    public bool NeedsBiomeMap => _catalog?.Body(_body ?? "") is { Biomes.Count: > 0 } && _map is null;

    public RelayCommand FetchMapCommand { get; }

    /// <summary>The picked vessel's next few orbits: the stretches over somewhere that still pays, in time order.</summary>
    public IReadOnlyList<SciencePassView> Passes
    {
        get => _passes;
        private set { if (SetProperty(ref _passes, value)) OnPropertyChanged(nameof(HasPasses)); }
    }

    public bool HasPasses => Passes.Count > 0;

    /// <summary>Why there are no passes, or what they cover.</summary>
    public string PassesNote { get => _passesNote; private set => SetProperty(ref _passesNote, value); }

    /// <summary>The mouse is over the map at a point (0–1 across, 0–1 down).</summary>
    public void HoverAt(double x, double y)
    {
        if (_map is null || x is < 0 or > 1 || y is < 0 or > 1) { HoverText = ""; return; }
        var lat = 90 - y * 180;
        var lon = -180 + x * 360;
        var biome = _map.BiomeAt(lat, lon);
        var cell = biome is null ? null : _table?.Rows.FirstOrDefault(r => r.Biome == biome)?.Cell(_mapSituation?.Situation ?? ScienceSituation.None);
        HoverText = biome is null ? "" : _mapValues.TryGetValue(biome, out var v) && cell is not null
            ? $"{biome} ({lat:0}°, {lon:0}°): {(v >= PaysAtLeast ? Show(cell.Recovered, cell.Sent) : "done")}"
            : $"{biome}: nothing to run in this situation";
    }

    public void HoverEnd() => HoverText = "";

    private async Task FetchMapAsync()
    {
        if (_working || _body is not { } body) return;
        if (!_link.IsConnected)
        {
            Status = "Connect to KSP first (KSP › Connect to KSP); the biome map comes from the game once per body.";
            return;
        }
        SetWorking(true);
        Status = $"Fetching {body}'s biome map from the game…";
        try
        {
            var map = await _link.FetchScienceBiomeMapAsync(body);
            _repository.SaveBiomeMap(map);
            Status = $"{body}'s biome map is in.";
            if (_body == body) { _map = map; OnPropertyChanged(nameof(NeedsBiomeMap)); DrawMap(); if (_catalog?.Body(body) is { } b) RefreshPasses(b); }
        }
        catch (Exception ex) when (ex is InvalidOperationException or KRPC.Client.RPCException or ArgumentException)
        {
            Status = $"Couldn't fetch {body}'s biome map: {ex.Message}";
        }
        finally { SetWorking(false); }
    }

    /// <summary>Fetches the picked body's biome map on its own, once per body, while the game is there.</summary>
    private void AutoFetchMap()
    {
        if (_link.IsConnected && NeedsBiomeMap && !_working && _body is { } body && _mapFetchTried.Add(body))
            _ = FetchMapAsync();
    }

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
        if (_counting is null) DefaultCounting();
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
            _table = null;
            _map = null;
            OnPropertyChanged(nameof(NeedsBiomeMap));
            DrawMap();
            Passes = [];
            PassesNote = "";
            Summary = save is null ? "No KSP save is being followed (KSP › Follow a save)." : "";
            return;
        }

        var experiments = _experiment?.Experiment is { } one ? [one] : CurrentExperiments();
        var table = ScienceGaps.Table(body, experiments, save.ScienceSubjects, ScienceGaps.Carried(save),
            ScienceGaps.TransmitFractions(_vessel?.Vessel, catalog), CountingMode);
        Columns = table.Situations.Select(ScienceGaps.Label).ToList();
        Rows = table.Rows.Select(r => new ScienceRowView(
            r.IsMiniBiome ? $"{r.Biome} (KSC)" : r.Biome,
            Show(r.Recovered, r.Sent),
            table.Situations.Select(s => CellView(r.Cell(s))).ToList(),
            r.Biome == ScienceGapTable.AnywhereRow)).ToList();

        Best = table.Rows
            .SelectMany(r => r.Cells.Where(c => c.NextRun >= PaysAtLeast).Select(c => (Row: r, Cell: c)))
            .OrderByDescending(x => x.Cell.NextRun).Take(8)
            .Select(x => new SciencePlaceView(Show(x.Cell.Recovered, x.Cell.Sent),
                x.Row.Biome == ScienceGapTable.AnywhereRow ? $"{ScienceGaps.Label(x.Cell.Situation)}, anywhere" : $"{x.Row.Biome}, {ScienceGaps.Label(x.Cell.Situation).ToLowerInvariant()}",
                string.Join(", ", x.Cell.Entries.Where(e => e.NextRun >= 0.05).OrderByDescending(e => e.NextRun).Select(e => $"{e.Title} {Show(e.Recovered, e.Sent)}"))))
            .ToList();

        var who = _vessel?.Vessel is { } v ? v.Name : "One run of every experiment";
        Summary = experiments.Count == 0
            ? "Nothing to show: no experiments."
            : $"{who} could still earn {Show(table.Recovered, table.Sent)} at {body.Name}" +
              (_experiment?.Experiment is { } picked ? $" with the {picked.Title.ToLowerInvariant()}" : "") +
              CountingMode switch
              {
                  ScienceCounting.Transmitted => " (one run in each place, sent home).",
                  ScienceCounting.Recovered => " (one run in each place, recovered).",
                  _ => " (one run in each place: recovered / sent home).",
              };

        _table = table;
        if (_map?.BodyName != body.Name) _map = _repository.GetBiomeMap(body.Name);
        OnPropertyChanged(nameof(NeedsBiomeMap));
        var keep = _mapSituation?.Situation;
        _refreshingChoices = true;
        try
        {
            MapSituations.Clear();
            foreach (var s in table.Situations) MapSituations.Add(new ScienceSituationChoice(s, ScienceGaps.Label(s)));
        }
        finally { _refreshingChoices = false; }
        var start = keep is { } k && table.Situations.Contains(k) ? k : ScienceMapping.BestSituation(table);
        _mapSituation = MapSituations.FirstOrDefault(c => c.Situation == start);
        OnPropertyChanged(nameof(MapSituation));
        DrawMap();
        RefreshPasses(body);
        AutoFetchMap();
    }

    /// <summary>How many turns of its orbit the passes look ahead.</summary>
    private const int PassOrbits = 3;

    /// <summary>
    /// The picked vessel's upcoming passes over the picked body (docs/SCIENCE.md "Upcoming passes"): its orbit from the save,
    /// the biome map's rotation and the body's altitude lines.
    /// </summary>
    private void RefreshPasses(ScienceBody body)
    {
        Passes = [];
        if (_vessel?.Vessel is not { } vessel) { PassesNote = "Pick a vessel to see when it passes over somewhere that still pays."; return; }
        if (!vessel.IsOrbiting || vessel.Orbit is not { IsClosed: true } orbit)
        {
            PassesNote = $"{vessel.Name} isn't in orbit, so there are no passes to show.";
            return;
        }
        if (ScienceName(orbit.BodyName) != body.Name) { PassesNote = $"{vessel.Name} isn't orbiting {body.Name}."; return; }
        if (body.Biomes.Count > 0 && _map is null) { PassesNote = "Passes need the biome map (above)."; return; }
        var stock = _sync.StockBodies().GetValueOrDefault(orbit.BodyName ?? "");
        if (stock is null || _table is not { } table) { PassesNote = $"The planner doesn't know {body.Name}'s gravity."; return; }
        if (body.HasAtmosphere && body.AtmosphereHeightM <= 0) body = body with { AtmosphereHeightM = stock.AtmosphereHeightM };
        var map = _map ?? new ScienceBiomeMap(body.Name, 1, stock.EquatorialRadiusM, 0, 0, [], [], DateTime.UtcNow);

        var now = _sync.Now ?? _sync.Save?.Ut ?? 0;
        var passes = SciencePasses.Upcoming(orbit.ToElements(), stock.Mu, body, map, table, now, PassOrbits, PaysAtLeast);
        Passes = passes.Select(p => new SciencePassView(
            p.StartUt <= now + 1 ? $"now, for {Span(p.EndUt - now)}" : $"in {Span(p.StartUt - now)}, for {Span(p.EndUt - p.StartUt)}",
            Show(p.Entries.Sum(e => e.Recovered), p.Entries.Sum(e => e.Sent)),
            $"{p.Biome ?? "anywhere"}, {ScienceGaps.Label(p.Situation).ToLowerInvariant()}",
            string.Join(", ", p.Entries.Where(e => e.NextRun >= 0.05).OrderByDescending(e => e.NextRun).Select(e => $"{e.Title} {Show(e.Recovered, e.Sent)}")),
            p.StartUt <= now + 1)).ToList();
        PassesNote = passes.Count == 0
            ? $"Nothing aboard {vessel.Name} pays {PaysAtLeast:0} or more anywhere on its next {PassOrbits} orbits."
            : $"Next {PassOrbits} orbits of {vessel.Name}, from {(_sync.IsLive ? "the live game" : "the save")}. The game only runs experiments on the vessel you're flying, so switch to it in time.";
    }

    /// <summary>"45s", "12m", "1h 05m", "2d 3h" (Kerbin days).</summary>
    private static string Span(double seconds)
    {
        seconds = Math.Max(0, seconds);
        if (seconds < 60) return $"{seconds:0}s";
        if (seconds < 3600) return $"{Math.Floor(seconds / 60):0}m";
        var day = KspFlightPlanner.Core.GameCalendar.Kerbin.SecondsPerDay;
        if (seconds < day) return $"{Math.Floor(seconds / 3600):0}h {Math.Floor(seconds % 3600 / 60):00}m";
        return $"{Math.Floor(seconds / day):0}d {Math.Floor(seconds % day / 3600):0}h";
    }

    /// <summary>Colours each biome by what it pays in the picked situation: dark where nothing runs, grey where it's done.</summary>
    private void DrawMap()
    {
        if (_map is not { } map || _table is not { } table || _mapSituation is not { } pick)
        {
            MapImage = null;
            MapLegend = "";
            _mapValues = new Dictionary<string, double>();
            return;
        }
        _mapValues = ScienceMapping.BiomeValues(table, pick.Situation);
        var top = _mapValues.Values.DefaultIfEmpty(0).Max();
        int width = map.Columns, height = map.Rows;
        var pixels = new uint[width * height];
        for (var i = 0; i < map.Count; i++)
        {
            var row = height - 1 - i / width; // north at the top
            var color = MapColors.None;
            if (map.BiomeOf(i) is { } biome && _mapValues.TryGetValue(biome, out var value))
                color = value < PaysAtLeast ? 0xFF4A4F57 : MapColors.Heat(value / top);
            pixels[row * width + i % width] = color;
        }
        var bitmap = new System.Windows.Media.Imaging.WriteableBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        bitmap.Freeze();
        MapImage = bitmap;

        var best = _mapValues.Where(kv => kv.Value >= PaysAtLeast).OrderByDescending(kv => kv.Value).FirstOrDefault();
        var done = _mapValues.Count(kv => kv.Value < PaysAtLeast);
        var anywhereCell = table.Rows.FirstOrDefault(r => r.Biome == ScienceGapTable.AnywhereRow)?.Cell(pick.Situation);
        var bestCell = best.Key is null ? null : table.Rows.First(r => r.Biome == best.Key).Cell(pick.Situation);
        MapLegend = (bestCell is null ? "Nothing left to earn in any biome here." : $"Red pays most: {best.Key}, {Show(bestCell.Recovered, bestCell.Sent)}. Blue pays least.") +
                    (done > 0 ? $" Grey: done ({done} biome{(done == 1 ? "" : "s")})." : "") +
                    (anywhereCell is { NextRun: >= PaysAtLeast } ? $" Plus {Show(anywhereCell.Recovered, anywhereCell.Sent)} anywhere in this situation." : "");
    }

    private ScienceCellView CellView(ScienceGapCell? cell)
    {
        if (cell is null || cell.Entries.Count == 0) return new ScienceCellView("", "", false, true);
        var detail = string.Join("\n", cell.Entries.OrderByDescending(e => e.NextRun).Select(e =>
            $"{e.Title}: {Points(e.Recovered)} recovered, {Points(e.Sent)} sent ({(e.Recovered > 0 ? e.Sent / e.Recovered : 0):0%}) · {Points(e.Left)} of {Points(e.Cap)} left" +
            (e.CarriedCopies > 0 ? $" · {e.CarriedCopies} cop{(e.CarriedCopies == 1 ? "y" : "ies")} aboard already" : "")));
        var pays = cell.NextRun >= PaysAtLeast;
        return new ScienceCellView(pays ? Show(cell.Recovered, cell.Sent) : "✓", detail, pays, false);
    }

    /// <summary>A value the way the "count as" box says: recovered, sent, or "recovered / sent".</summary>
    private string Show(double recovered, double sent) => CountingMode switch
    {
        ScienceCounting.Recovered => Points(recovered),
        ScienceCounting.Transmitted => Points(sent),
        _ => $"{Points(recovered)} / {Points(sent)}",
    };

    private static string Points(double value) => value >= 10 ? value.ToString("N0") : value.ToString("0.#");
}
