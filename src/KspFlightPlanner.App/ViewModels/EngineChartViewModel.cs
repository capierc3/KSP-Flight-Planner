using System.Globalization;
using System.Windows.Media;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.App.ViewModels;

/// <summary>Which parts the engine chart may build with.</summary>
public enum ChartParts { Unlocked, UnlockedAndPlanned, All }

/// <summary>An option in one of the chart's pickers: its text and value.</summary>
public sealed record Choice<T>(string Text, T Value)
{
    public override string ToString() => Text;
}

/// <summary>An engine that wins somewhere on the chart, with its colour.</summary>
public sealed record ChartLegendEntry(string Title, Brush Color, string Share);

/// <summary>
/// The chart's grid: for each cell (payload across on a log scale, Δv up), which engine wins (an index into
/// <see cref="Legend"/>, −1 for none) and how many of it.
/// </summary>
public sealed class EngineChartPlot(int columns, int rows, double payloadMinT, double payloadMaxT, double deltaVMax,
    int[] winners, int[] counts, IReadOnlyList<ChartLegendEntry> legend, IReadOnlyList<Color> colors)
{
    public int Columns { get; } = columns;
    public int Rows { get; } = rows;
    public double PayloadMinT { get; } = payloadMinT;
    public double PayloadMaxT { get; } = payloadMaxT;
    public double DeltaVMax { get; } = deltaVMax;
    public IReadOnlyList<ChartLegendEntry> Legend { get; } = legend;
    public IReadOnlyList<Color> Colors { get; } = colors;

    /// <summary>Winner of cell (column, row), row 0 at the bottom; −1 when nothing can do it.</summary>
    public int Winner(int column, int row) => winners[row * Columns + column];

    public int Count(int column, int row) => counts[row * Columns + column];

    public double PayloadAt(double fraction) => PayloadMinT * Math.Pow(PayloadMaxT / PayloadMinT, fraction);

    public double FractionOf(double payloadT) => Math.Log(payloadT / PayloadMinT) / Math.Log(PayloadMaxT / PayloadMinT);
}

/// <summary>One engine sized for the stage in the inputs, as a row of the table.</summary>
public sealed record EngineOptionRow(EngineOption Option, string Engine, string Count, string Status, bool IsUnlocked, string StageMass,
    string TotalMass, string Propellant, string Twr, TwrRating? Rating, string BurnTime, string Cost, string Note);

/// <summary>
/// The engine chart: which engine makes the lightest (or cheapest) stage for a payload and the Δv it needs, across a
/// range of payloads and Δv, using the parts the save can build. Inputs are in the Calculators pane; picking an engine
/// in the table shows it in the Part pane.
/// </summary>
public sealed class EngineChartViewModel : ObservableObject
{
    private static readonly CultureInfo C = CultureInfo.CurrentCulture;

    // A categorical palette that reads on both themes; engines beyond it reuse colours.
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x4E, 0x79, 0xA7), Color.FromRgb(0xF2, 0x8E, 0x2B), Color.FromRgb(0xE1, 0x57, 0x59), Color.FromRgb(0x76, 0xB7, 0xB2),
        Color.FromRgb(0x59, 0xA1, 0x4F), Color.FromRgb(0xED, 0xC9, 0x48), Color.FromRgb(0xB0, 0x7A, 0xA1), Color.FromRgb(0xFF, 0x9D, 0xA7),
        Color.FromRgb(0x9C, 0x75, 0x5F), Color.FromRgb(0xBA, 0xB0, 0xAC), Color.FromRgb(0x86, 0xBC, 0xB6), Color.FromRgb(0xD3, 0x72, 0x95),
    ];

    private const int Columns = 120;
    private const int Rows = 90;
    private const double PayloadMinT = 0.1;

    private readonly SaveSync _sync;
    private readonly PartAvailability _parts;
    private EngineChart? _chart;
    private string _payloadText = "5";
    private string _deltaVText = "2000";
    private string _minTwrText = "0.5";
    private TwrBody _body = TwrBody.InSpace;
    private Choice<int> _maxEngines;
    private Choice<EngineChartGoal> _goal;
    private Choice<ChartParts> _partsChoice;
    private Choice<double> _payloadRange;
    private Choice<double> _deltaVRange;
    private EngineChartPlot? _plot;
    private EngineOptionRow? _selected;
    private int _version;

    public EngineChartViewModel(SaveSync sync, PartAvailability parts)
    {
        _sync = sync;
        _parts = parts;
        MaxEnginesChoices = Enumerable.Range(1, 9).Select(n => new Choice<int>(n == 1 ? "1 engine" : $"Up to {n}", n)).ToList();
        _maxEngines = MaxEnginesChoices[3];
        Goals = [new("Lightest stage", EngineChartGoal.LightestStage), new("Cheapest stage", EngineChartGoal.CheapestStage)];
        _goal = Goals[0];
        PartsChoices =
        [
            new("Unlocked parts", ChartParts.Unlocked), new("Unlocked and planned", ChartParts.UnlockedAndPlanned),
            new("Every part", ChartParts.All),
        ];
        _partsChoice = PartsChoices[0];
        PayloadRanges = [new("Up to 10 t", 10), new("Up to 100 t", 100), new("Up to 1,000 t", 1000)];
        _payloadRange = PayloadRanges[1];
        DeltaVRanges = [new("Up to 2,000 m/s", 2000), new("Up to 4,000 m/s", 4000), new("Up to 6,000 m/s", 6000), new("Up to 8,000 m/s", 8000)];
        _deltaVRange = DeltaVRanges[2];

        Bodies = [TwrBody.InSpace, .. TwrBody.Stock(sync).Bodies];
        parts.Changed += Rebuild;
        Rebuild();
    }

    // ---------------- Inputs (Calculators pane) ----------------

    public string PayloadText { get => _payloadText; set { if (SetProperty(ref _payloadText, value)) Recalculate(chart: false); } }
    public string DeltaVText { get => _deltaVText; set { if (SetProperty(ref _deltaVText, value)) Recalculate(chart: false); } }
    public string MinTwrText { get => _minTwrText; set { if (SetProperty(ref _minTwrText, value)) Recalculate(); } }

    public IReadOnlyList<TwrBody> Bodies { get; }

    /// <summary>Where the stage fires: its gravity sets the TWR, its air the thrust.</summary>
    public TwrBody Body { get => _body; set { if (value is not null && SetProperty(ref _body, value)) Recalculate(); } }

    public IReadOnlyList<Choice<int>> MaxEnginesChoices { get; }
    public Choice<int> MaxEngines { get => _maxEngines; set { if (value is not null && SetProperty(ref _maxEngines, value)) Recalculate(); } }

    public IReadOnlyList<Choice<EngineChartGoal>> Goals { get; }
    public Choice<EngineChartGoal> Goal { get => _goal; set { if (value is not null && SetProperty(ref _goal, value)) Recalculate(); } }

    public IReadOnlyList<Choice<ChartParts>> PartsChoices { get; }
    public Choice<ChartParts> PartsChoice { get => _partsChoice; set { if (value is not null && SetProperty(ref _partsChoice, value)) Rebuild(); } }

    public IReadOnlyList<Choice<double>> PayloadRanges { get; }
    public Choice<double> PayloadRange { get => _payloadRange; set { if (value is not null && SetProperty(ref _payloadRange, value)) Recalculate(); } }

    public IReadOnlyList<Choice<double>> DeltaVRanges { get; }
    public Choice<double> DeltaVRange { get => _deltaVRange; set { if (value is not null && SetProperty(ref _deltaVRange, value)) Recalculate(); } }

    /// <summary>Why the inputs can't be used, or null.</summary>
    public string? InputProblem { get; private set; }

    // ---------------- Results (the document) ----------------

    public EngineChartPlot? Plot { get => _plot; private set => SetProperty(ref _plot, value); }

    /// <summary>The payload and Δv typed in, marked on the chart (null while they don't parse).</summary>
    public (double PayloadT, double DeltaV)? Marker { get; private set; }

    public IReadOnlyList<EngineOptionRow> Options { get; private set; } = [];
    public bool HasOptions => Options.Count > 0;

    /// <summary>"Best for 5 t and 2,000 m/s in space, TWR 0.5 or more".</summary>
    public string OptionsTitle { get; private set; } = "";

    public string OptionsEmpty { get; private set; } = "";

    /// <summary>What the chart compares, e.g. "23 engines and 4 kinds of tank, unlocked in this save".</summary>
    public string Summary { get; private set; } = "";

    /// <summary>Picking an engine shows it in the Part pane.</summary>
    public EngineOptionRow? Selected
    {
        get => _selected;
        set
        {
            if (value is null || !SetProperty(ref _selected, value)) return;
            var part = value.Option.Engine;
            var node = part.TechRequired is { } id ? _parts.Catalog.TechTree.FirstOrDefault(n => n.Id == id)?.Title : null;
            PartPicked?.Invoke(new PartItem(part, _parts.StatusOf(part), node));
        }
    }

    /// <summary>An engine was picked in the table: the Part pane should show it.</summary>
    public event Action<PartItem>? PartPicked;

    /// <summary>A point picked on the chart becomes the payload and Δv.</summary>
    public void PickPoint(double payloadT, double deltaV)
    {
        _payloadText = payloadT.ToString(payloadT < 10 ? "0.##" : "0.#", C);
        _deltaVText = (Math.Round(deltaV / 10) * 10).ToString("0", C);
        OnPropertyChanged(nameof(PayloadText));
        OnPropertyChanged(nameof(DeltaVText));
        Recalculate(chart: false);
    }

    // ---------------- Working it out ----------------

    private bool Allowed(KspPart part) => PartsChoice.Value switch
    {
        ChartParts.All => true,
        ChartParts.UnlockedAndPlanned => _parts.StatusOf(part) is PartStatus.Unlocked or PartStatus.Planned or PartStatus.NotBought,
        _ => _parts.StatusOf(part) == PartStatus.Unlocked,
    };

    /// <summary>The parts changed (or the filter did): build the engine and tank lists again.</summary>
    private void Rebuild()
    {
        var catalog = _parts.Catalog;
        var shown = catalog.Parts.Values.Where(p => PartCategories.IsShown(p) && Allowed(p)).ToList();
        // Rocket engines only (launch escape towers and tanks with a built-in motor aren't engines to build with).
        var engines = shown.Where(p => p.Category == "Engine" && p.Engines.Any(e => !e.BreathesAir)).ToList();
        var tanks = shown.Where(p => p.Category is "FuelTank" or "Propulsion" && p.Engines.Count == 0).ToList();
        _chart = new EngineChart(engines, tanks, catalog);
        var kinds = _chart.Engines.Count;
        Summary = catalog.IsEmpty ? "No part data yet: choose your KSP folder in Tools › Settings."
            : $"{kinds} rocket engine{(kinds == 1 ? "" : "s")} " + PartsChoice.Value switch
            {
                ChartParts.All => "(every part, locked or not).",
                ChartParts.UnlockedAndPlanned => "unlocked in this save or in your research plan.",
                _ => _parts.Research is null ? "(sandbox: every part is unlocked)." : "unlocked in this save.",
            };
        OnPropertyChanged(nameof(Summary));
        Recalculate();
    }

    private void Recalculate(bool chart = true)
    {
        if (_chart is not { } engineChart) return;
        var minTwr = Parse(MinTwrText);
        var payload = Parse(PayloadText);
        var dv = Parse(DeltaVText);
        InputProblem = minTwr is not >= 0 ? "Enter the least TWR the stage may start with (0 for any)."
            : payload is not > 0 ? "Enter the payload's mass in tonnes."
            : dv is not > 0 ? "Enter the Δv the stage must give."
            : null;
        OnPropertyChanged(nameof(InputProblem));

        var template = new StageNeed(0, 0, minTwr ?? 0, Body.SurfaceGravity, Body.PressureAtm, MaxEngines.Value);
        var goal = Goal.Value;
        if (chart && minTwr is >= 0) _ = BuildPlotAsync(engineChart, template, goal);

        Marker = payload is > 0 && dv is > 0 ? (payload.Value, dv.Value) : null;
        OnPropertyChanged(nameof(Marker));
        if (InputProblem is null)
        {
            var need = template with { PayloadT = payload!.Value, DeltaV = dv!.Value };
            Options = engineChart.Options(need, goal).Select(o => Row(o, need)).ToList();
            var where = Body == TwrBody.InSpace ? "in space" : $"on {Body.Name}";
            OptionsTitle = $"Engines for {Units.Tonnes(need.PayloadT)} and {Units.Dv(need.DeltaV)} {where}, TWR {need.MinTwr.ToString("0.##", C)} or more";
            OptionsEmpty = Options.Count > 0 ? "" : "No engine here can do that: lower the Δv or the TWR, allow more engines, or include more parts.";
        }
        else
        {
            Options = [];
            OptionsTitle = "Engines";
            OptionsEmpty = InputProblem;
        }
        _selected = null;
        foreach (var name in new[] { nameof(Options), nameof(HasOptions), nameof(OptionsTitle), nameof(OptionsEmpty), nameof(Selected) })
            OnPropertyChanged(name);
    }

    /// <summary>Works out every cell's winner off the UI thread; only the latest request is shown.</summary>
    private async Task BuildPlotAsync(EngineChart chart, StageNeed template, EngineChartGoal goal)
    {
        var version = ++_version;
        var payloadMax = PayloadRange.Value;
        var dvMax = DeltaVRange.Value;
        var plot = await Task.Run(() =>
        {
            var winners = new int[Columns * Rows];
            var counts = new int[Columns * Rows];
            var names = new List<KspPart>();
            var area = new List<int>();
            for (var row = 0; row < Rows; row++)
            for (var column = 0; column < Columns; column++)
            {
                if (version != Volatile.Read(ref _version)) return null;
                var payload = PayloadMinT * Math.Pow(payloadMax / PayloadMinT, (column + 0.5) / Columns);
                var dv = dvMax * (row + 0.5) / Rows;
                var best = chart.Best(template with { PayloadT = payload, DeltaV = dv }, goal);
                var index = -1;
                if (best is not null)
                {
                    index = names.FindIndex(p => p.Name == best.Engine.Name);
                    if (index < 0)
                    {
                        index = names.Count;
                        names.Add(best.Engine);
                        area.Add(0);
                    }
                    area[index]++;
                    counts[row * Columns + column] = best.Count;
                }
                winners[row * Columns + column] = index;
            }

            // Colours by how much of the chart each engine wins, so the big regions get the clearest colours.
            var order = Enumerable.Range(0, names.Count).OrderByDescending(i => area[i]).ToList();
            var rank = new int[names.Count];
            for (var i = 0; i < order.Count; i++) rank[order[i]] = i;
            for (var i = 0; i < winners.Length; i++) if (winners[i] >= 0) winners[i] = rank[winners[i]];
            var colors = order.Select((_, i) => Palette[i % Palette.Length]).ToList();
            var legend = order.Select((engine, i) =>
            {
                var brush = new SolidColorBrush(colors[i]);
                brush.Freeze();
                var share = area[engine] / (double)winners.Length;
                return new ChartLegendEntry(names[engine].Title, brush, share < 0.005 ? "<1%" : share.ToString("0%", C));
            }).ToList();
            return new EngineChartPlot(Columns, Rows, PayloadMinT, payloadMax, dvMax, winners, counts, legend, colors);
        });
        if (plot is not null && version == _version) Plot = plot;
    }

    private EngineOptionRow Row(EngineOption o, StageNeed need)
    {
        var status = _parts.StatusOf(o.Engine);
        var statusText = status switch { PartStatus.Unlocked => "", PartStatus.Planned => "planned", PartStatus.NotBought => "not bought", _ => "locked" };
        var twr = o.Twr;
        var flow = o.Count * o.Mode.MaxThrustKn / (o.Mode.IspVacuumS * VesselAnalysis.G0); // t/s at full thrust
        var note = o.IsSolid ? $"solid: gives {Units.Dv(o.MaxDeltaV)}, burns it all"
            : o.TankDryT > 0 ? $"+ {Units.Tonnes(o.TankDryT)} of tanks" : "its own tank is enough";
        // Lifting off or landing, a TWR under 1 can't hold the stage up; in space any TWR over the least set will do.
        TwrRating? rating = Body == TwrBody.InSpace ? null : twr < 1 ? TwrRating.TooLow : twr < SurfaceTwr.LowTwr ? TwrRating.Low : TwrRating.Good;
        return new EngineOptionRow(o, o.Engine.Title, $"× {o.Count}", statusText, status == PartStatus.Unlocked,
            Units.Tonnes(o.StageMassT(need.PayloadT)), Units.Tonnes(o.TotalMassT), Units.Tonnes(o.PropellantT),
            twr.ToString("0.00", C), rating,
            flow > 0 ? Units.BurnTime(o.PropellantT / flow) : "—", "√" + o.Cost.ToString("N0", C), note);
    }

    private static double? Parse(string? text) =>
        double.TryParse((text ?? "").Trim().TrimEnd('t', 'T', 's', '/', 'm').Trim(), NumberStyles.Float | NumberStyles.AllowThousands, C, out var value)
        && double.IsFinite(value) ? value : null;
}
