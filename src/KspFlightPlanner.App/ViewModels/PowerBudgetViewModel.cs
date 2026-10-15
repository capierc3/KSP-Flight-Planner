using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.App.ViewModels;

/// <summary>A part to pick for the power budget, e.g. "OX-4W 3x2 Photovoltaic Panels · makes 1.64 EC/s".</summary>
public sealed record PowerPartChoice(KspPart Part, string Text)
{
    public override string ToString() => Text;
}

/// <summary>One line of the power budget's part list: which part and how many.</summary>
public sealed class PowerPartRow : ObservableObject
{
    private readonly Action _changed;
    private PowerPartChoice? _part;
    private string _countText;

    public PowerPartRow(PowerPartChoice? part, int count, Action changed, Action<PowerPartRow> remove)
    {
        _part = part;
        _countText = count.ToString(CultureInfo.CurrentCulture);
        _changed = changed;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public PowerPartChoice? Part { get => _part; set { if (value is not null && SetProperty(ref _part, value)) _changed(); } }
    public string CountText { get => _countText; set { if (SetProperty(ref _countText, value)) _changed(); } }
    public int Count => int.TryParse(CountText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var n) && n > 0 ? n : 0;
    public RelayCommand RemoveCommand { get; }
}

/// <summary>A switch for a kind of load that only draws while it's in use.</summary>
public sealed class LoadSwitch(string key, string title, string hint, bool isOn, Action changed) : ObservableObject
{
    private bool _isOn = isOn;

    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Hint { get; } = hint;
    public bool IsOn { get => _isOn; set { if (SetProperty(ref _isOn, value)) changed(); } }
}

/// <summary>A row of the budget: what makes or uses charge, how much, and whether it's counted.</summary>
public sealed record PowerLineRow(string Part, string Kind, string Count, string Rate, bool Makes, bool IsOn);

/// <summary>A headline of the budget, coloured when it's a pass or a fail.</summary>
public sealed record PowerCard(string Title, string Value, string Detail, bool? Good);

/// <summary>
/// The power budget: what a craft's solar panels, generators and fuel cells make where it's going, what its parts use,
/// and whether its batteries see it through the longest night there. Parts and the place are in the Calculators pane.
/// </summary>
public sealed class PowerBudgetViewModel : ObservableObject
{
    private static readonly CultureInfo C = CultureInfo.CurrentCulture;

    private readonly SaveSync _sync;
    private readonly PartAvailability _parts;
    private readonly EngineeringViewModel _engineering;
    private Choice<ChartParts> _partsChoice;
    private CelestialBody? _body;
    private bool _onSurface;
    private string _altitudeText = "100";
    private bool _fuelCells;

    public PowerBudgetViewModel(SaveSync sync, PartAvailability parts, EngineeringViewModel engineering)
    {
        _sync = sync;
        _parts = parts;
        _engineering = engineering;
        PartsChoices = [new("Unlocked parts", ChartParts.Unlocked), new("Unlocked and planned", ChartParts.UnlockedAndPlanned), new("Every part", ChartParts.All)];
        _partsChoice = PartsChoices[0];
        Loads =
        [
            new(nameof(PowerKind.Command), "Probe cores and pods", "Keeping control of the craft", true, Recalculate),
            new(nameof(PowerKind.ReactionWheel), "Reaction wheels (turning)", "At full torque, while the craft turns", false, Recalculate),
            new(nameof(PowerKind.Antenna), "Transmitting", "Sending science through the hungriest antenna aboard", false, Recalculate),
            new(nameof(PowerKind.Light), "Lights", "", false, Recalculate),
            new(nameof(PowerKind.Lab), "Science lab", "While researching", false, Recalculate),
            new(nameof(PowerKind.Converter), "ISRU converters", "One mode running on each", false, Recalculate),
            new(nameof(PowerKind.Drill), "Drills", "", false, Recalculate),
            new("Engine", "Ion engines", "At full thrust", false, Recalculate),
        ];
        AddPartCommand = new RelayCommand(() =>
        {
            Rows.Add(NewRow(PartChoices.FirstOrDefault(), 1));
            Recalculate();
        });
        UseDesignCommand = new RelayCommand(UseDesign, () => _engineering.DesignPowerParts.Count > 0);

        Bodies = StockBodies().Where(b => b.BodyClass != "Star").ToList();
        _body = Bodies.FirstOrDefault(b => b.IsDefault) ?? Bodies.FirstOrDefault();

        parts.Changed += RefreshChoices;
        engineering.PropertyChanged += OnEngineeringChanged;
        RefreshChoices();
        UseDesign();
    }

    // ---------------- Inputs (Calculators pane) ----------------

    public ObservableCollection<PowerPartRow> Rows { get; } = [];
    public IReadOnlyList<PowerPartChoice> PartChoices { get; private set; } = [];
    public RelayCommand AddPartCommand { get; }
    public RelayCommand UseDesignCommand { get; }
    public string UseDesignText => _engineering.DesignTitle is { Length: > 0 } title ? $"Use “{title}”" : "Use the Engineering design";

    public IReadOnlyList<Choice<ChartParts>> PartsChoices { get; }
    public Choice<ChartParts> PartsChoice { get => _partsChoice; set { if (value is not null && SetProperty(ref _partsChoice, value)) RefreshChoices(); } }

    public IReadOnlyList<CelestialBody> Bodies { get; }
    public CelestialBody? Body { get => _body; set { if (value is not null && SetProperty(ref _body, value)) Recalculate(); } }

    /// <summary>On the surface (false: in orbit at <see cref="AltitudeText"/>).</summary>
    public bool OnSurface
    {
        get => _onSurface;
        set
        {
            if (!SetProperty(ref _onSurface, value)) return;
            OnPropertyChanged(nameof(InOrbit));
            Recalculate();
        }
    }

    public bool InOrbit { get => !OnSurface; set => OnSurface = !value; }
    public string AltitudeText { get => _altitudeText; set { if (SetProperty(ref _altitudeText, value)) Recalculate(); } }

    public IReadOnlyList<LoadSwitch> Loads { get; }

    /// <summary>Run the fuel cells when the panels and generators can't keep up.</summary>
    public bool FuelCells { get => _fuelCells; set { if (SetProperty(ref _fuelCells, value)) Recalculate(); } }

    // ---------------- Results (the document) ----------------

    public string Where { get; private set; } = "";
    public IReadOnlyList<PowerCard> Cards { get; private set; } = [];
    public IReadOnlyList<PowerLineRow> Lines { get; private set; } = [];
    public bool HasLines => Lines.Count > 0;
    public string Verdict { get; private set; } = "";
    public bool? VerdictGood { get; private set; }

    // ---------------- Working it out ----------------

    /// <summary>Parts that make, use or store electric charge (ion engines included).</summary>
    public static bool IsPowerPart(KspPart part) =>
        part.PowerModules.Count > 0 || part.Resources.Any(r => r.Name == "ElectricCharge" && r.MaxAmount > 0)
        || part.Engines.Any(e => e.Propellants.Any(p => p.Name == "ElectricCharge"));

    private PowerPartRow NewRow(PowerPartChoice? part, int count) => new(part, count, Recalculate, r =>
    {
        Rows.Remove(r);
        Recalculate();
    });

    private void UseDesign()
    {
        var design = _engineering.DesignPowerParts;
        if (design.Count == 0) return;
        Rows.Clear();
        foreach (var (part, count) in design) Rows.Add(NewRow(Choice(part), count));
        Recalculate();
    }

    private void OnEngineeringChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(EngineeringViewModel.DesignTitle)) return;
        OnPropertyChanged(nameof(UseDesignText));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        if (Rows.Count == 0) UseDesign();
    }

    private bool Allowed(KspPart part) => PartsChoice.Value switch
    {
        ChartParts.All => true,
        ChartParts.UnlockedAndPlanned => _parts.StatusOf(part) is PartStatus.Unlocked or PartStatus.Planned or PartStatus.NotBought,
        _ => _parts.StatusOf(part) == PartStatus.Unlocked,
    };

    private void RefreshChoices()
    {
        PartChoices = _parts.Catalog.Parts.Values
            .Where(p => PartCategories.IsShown(p) && IsPowerPart(p) && Allowed(p))
            .Select(Describe)
            .OrderBy(c => c.Text).ToList();
        OnPropertyChanged(nameof(PartChoices));
        foreach (var row in Rows)
            if (row.Part is { } p && PartChoices.FirstOrDefault(c => c.Part.Name == p.Part.Name) is { } same) row.Part = same;
        Recalculate();
    }

    private PowerPartChoice Choice(KspPart part) => PartChoices.FirstOrDefault(c => c.Part.Name == part.Name) ?? Describe(part);

    private PowerPartChoice Describe(KspPart part)
    {
        var made = part.PowerModules.Where(m => m.Makes).Sum(m => m.EcPerSecond);
        var battery = part.Resources.Where(r => r.Name == "ElectricCharge").Sum(r => r.MaxAmount);
        var used = part.PowerModules.Where(m => !m.Makes && m.Kind != PowerKind.Antenna).Sum(m => m.EcPerSecond)
                   + part.Engines.Select(e => PowerBudget.EngineEcPerSecond(e, _parts.Catalog)).DefaultIfEmpty(0).Max();
        var facts = new List<string>();
        if (made > 0) facts.Add($"makes {Rate(made)}");
        if (used > 0) facts.Add($"uses {Rate(used)}");
        if (battery > 0) facts.Add($"holds {battery.ToString("N0", C)} EC");
        return new PowerPartChoice(part, facts.Count > 0 ? $"{part.Title} · {string.Join(", ", facts)}" : part.Title);
    }

    private IReadOnlyCollection<CelestialBody> StockBodies() => _sync.StockBodies().Values
        .OrderBy(b => Array.IndexOf(SavedOrbit.StockBodyOrder, b.Name) is var i and >= 0 ? i : int.MaxValue).ThenBy(b => b.Name).ToList();

    private void Recalculate()
    {
        var system = StockBodies();
        var home = system.FirstOrDefault(b => b.IsDefault);
        if (Body is not { } body || home is null || Rows is null)
        {
            Clear("Link a KSP save in Tools › Settings so the planner knows the bodies.");
            return;
        }
        var altitude = (double.TryParse(AltitudeText, NumberStyles.Float, C, out var km) && km >= 0 ? km : 100) * 1000;
        var situation = OnSurface ? PowerBudget.OnSurface(body, system) : PowerBudget.InOrbit(body, altitude, system);
        var parts = Rows.Where(r => r.Part is not null).GroupBy(r => r.Part!.Part.Name)
            .Select(g => (g.First().Part!.Part, g.Sum(r => r.Count))).ToList();
        var on = Loads.Where(l => l.IsOn).Select(l => l.Key).ToHashSet();
        var r = PowerBudget.Work(parts, _parts.Catalog, situation, PowerBudget.SunDistance(home, system), on, FuelCells);

        Where = $"{situation.Where} · {Math.Round(100 * PowerBudget.SolarScale(situation.SunDistanceM, PowerBudget.SunDistance(home, system)))}% sunlight of {home.Name}'s";
        Lines = r.Lines.OrderByDescending(l => l.Makes).ThenByDescending(l => l.EcPerSecond)
            .Select(l => new PowerLineRow(l.Title, KindText(l.Kind), $"× {l.Count}", (l.Makes ? "+" : "−") + Rate(l.EcPerSecond), l.Makes, l.IsOn))
            .ToList();

        var cards = new List<PowerCard>
        {
            new("In the sun", Signed(r.NetInSunEcS), $"makes {Rate(r.MadeInSunEcS)}, uses {Rate(r.UsedEcS)}", r.NetInSunEcS >= 0),
            new("In the dark", Signed(r.NetInDarkEcS), $"makes {Rate(r.MadeInDarkEcS)}, uses {Rate(r.UsedEcS)}", r.NetInDarkEcS >= 0 ? true : null),
            new("Longest night", Units.BurnTime(situation.NightS), $"then {Units.BurnTime(situation.DayS)} of sunlight", null),
            new("Batteries", $"{r.BatteryEc.ToString("N0", C)} EC",
                r.BatteryNeededEc <= 0 ? "the night doesn't drain them" : $"the night needs {r.BatteryNeededEc.ToString("N0", C)} EC",
                r.LastsTheNight),
        };
        if (r.BatteryLastsS is { } lasts) cards.Add(new("Full batteries last", Units.BurnTime(lasts), "in the dark", lasts >= situation.NightS));
        if (r.BatteryNeededEc > 0)
            cards.Add(new("Recharge", r.RechargeS is { } t ? Units.BurnTime(t) : "never", r.RechargeS is null
                ? "the sun doesn't make enough to refill them" : $"of the {Units.BurnTime(situation.DayS)} day", r.RechargesInTheDay));
        if (r.FuelPerNight.Count > 0)
            cards.Add(new("Fuel cells burn", string.Join(" + ", r.FuelPerNight.Select(f => $"{f.Ratio.ToString("N0", C)} {_parts.Catalog.DisplayName(f.Name)}")),
                "per night", null));
        Cards = cards;

        (Verdict, VerdictGood) = parts.Count == 0 ? ("Add the craft's parts in the Calculators pane, or use the Engineering design.", (bool?)null)
            : r.UsedEcS <= 0 ? ("Nothing switched on uses charge.", true)
            : !r.LastsTheNight ? ($"Runs out in the dark: the night needs {r.BatteryNeededEc.ToString("N0", C)} EC, the batteries hold {r.BatteryEc.ToString("N0", C)}.", false)
            : !r.RechargesInTheDay ? ("Lasts the night, but the day doesn't make enough to refill the batteries.", false)
            : r.BatteryNeededEc <= 0 ? ("Enough: it makes more than it uses, even in the dark.", true)
            : ($"Enough: the night uses {r.BatteryNeededEc / Math.Max(r.BatteryEc, 1e-9):0%} of the batteries and the day refills them.", true);

        foreach (var name in new[] { nameof(Where), nameof(Lines), nameof(HasLines), nameof(Cards), nameof(Verdict), nameof(VerdictGood) })
            OnPropertyChanged(name);
    }

    private void Clear(string verdict)
    {
        Where = "";
        Lines = [];
        Cards = [];
        (Verdict, VerdictGood) = (verdict, null);
        foreach (var name in new[] { nameof(Where), nameof(Lines), nameof(HasLines), nameof(Cards), nameof(Verdict), nameof(VerdictGood) })
            OnPropertyChanged(name);
    }

    private static string KindText(string kind) => kind switch
    {
        nameof(PowerKind.Solar) => "solar panel",
        nameof(PowerKind.Generator) => "generator",
        nameof(PowerKind.FuelCell) => "fuel cell",
        nameof(PowerKind.Command) => "control",
        nameof(PowerKind.ReactionWheel) => "reaction wheel",
        nameof(PowerKind.Antenna) => "transmitting",
        nameof(PowerKind.Light) => "light",
        nameof(PowerKind.Lab) => "research",
        nameof(PowerKind.Converter) => "converter",
        nameof(PowerKind.Drill) => "drill",
        "Engine" => "ion engine",
        _ => kind,
    };

    private static string Rate(double ecPerSecond) => ecPerSecond.ToString(ecPerSecond < 1 ? "0.###" : "0.##", C) + " EC/s";

    private static string Signed(double ecPerSecond) => (ecPerSecond >= 0 ? "+" : "−") + Rate(Math.Abs(ecPerSecond));
}
