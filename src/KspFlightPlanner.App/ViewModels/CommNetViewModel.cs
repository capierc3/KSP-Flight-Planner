using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.App.ViewModels;

/// <summary>An antenna part to pick, e.g. "Communotron 16 · 500 km direct".</summary>
public sealed record AntennaChoice(KspPart Part, string Text)
{
    public override string ToString() => Text;
}

/// <summary>One line of an antenna list: which antenna and how many.</summary>
public sealed class AntennaRow : ObservableObject
{
    private readonly Action _changed;
    private AntennaChoice? _antenna;
    private string _countText;

    public AntennaRow(AntennaChoice? antenna, int count, Action changed, Action<AntennaRow> remove)
    {
        _antenna = antenna;
        _countText = count.ToString(CultureInfo.CurrentCulture);
        _changed = changed;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public AntennaChoice? Antenna { get => _antenna; set { if (value is not null && SetProperty(ref _antenna, value)) _changed(); } }
    public string CountText { get => _countText; set { if (SetProperty(ref _countText, value)) _changed(); } }
    public int Count => int.TryParse(CountText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var n) && n > 0 ? n : 0;
    public RelayCommand RemoveCommand { get; }

    public IEnumerable<PartAntenna> Antennas => Antenna is null ? [] : Enumerable.Repeat(Antenna.Part.Antennas, Count).SelectMany(a => a);
}

/// <summary>A link's range and what it means, as a card in the results.</summary>
public sealed record RangeCard(string Title, string Range, string Detail);

/// <summary>
/// A body and how the craft and relay fare at its farthest from home: the signal left there, and a log-scale bar
/// of the distance with the two ranges marked.
/// </summary>
public sealed record BodyLinkRow(string Body, string Farthest, double DistanceFraction, double? CraftRangeFraction, double? RelayRangeFraction,
    string CraftSignal, bool CraftReaches, string RelaySignal, bool RelayReaches, bool HasRelay);

/// <summary>
/// The CommNet planner: how far a craft's antennas reach (to the tracking station, a relay, each other) and whether
/// it can still talk home from each body at its farthest. Antennas and settings are in the Calculators pane; the
/// game's settings come from the save.
/// </summary>
public sealed class CommNetViewModel : ObservableObject
{
    private static readonly CultureInfo C = CultureInfo.CurrentCulture;

    // The bars' log scale: 1 Mm to 200 Gm (Eeloo at its farthest is about 127 Gm from Kerbin).
    private const double ScaleMinM = 1e6, ScaleMaxM = 2e11;

    private readonly SaveSync _sync;
    private readonly PartAvailability _parts;
    private readonly EngineeringViewModel _engineering;
    private Choice<ChartParts> _partsChoice;
    private Choice<int> _level;
    private string _rangeModifierText = "1";
    private string _dsnModifierText = "1";
    private SavedCommNet? _fromSave;

    public CommNetViewModel(SaveSync sync, PartAvailability parts, EngineeringViewModel engineering)
    {
        _sync = sync;
        _parts = parts;
        _engineering = engineering;
        PartsChoices =
        [
            new("Unlocked antennas", ChartParts.Unlocked), new("Unlocked and planned", ChartParts.UnlockedAndPlanned),
            new("Every antenna", ChartParts.All),
        ];
        _partsChoice = PartsChoices[0];
        Levels = [new("Level 1 (2 Gm)", 1), new("Level 2 (50 Gm)", 2), new("Level 3 (250 Gm)", 3)];
        _level = Levels[2];

        AddCraftAntennaCommand = new RelayCommand(() => Add(CraftAntennas));
        AddRelayAntennaCommand = new RelayCommand(() => Add(RelayAntennas, relay: true));
        UseDesignCommand = new RelayCommand(UseDesign, () => _engineering.DesignAntennas.Count > 0);

        parts.Changed += RefreshChoices;
        sync.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SaveSync.Save)) ReadSaveSettings(); };
        engineering.PropertyChanged += OnEngineeringChanged;
        RefreshChoices();
        ReadSaveSettings();
        UseDesign();
    }

    // ---------------- Inputs (Calculators pane) ----------------

    public ObservableCollection<AntennaRow> CraftAntennas { get; } = [];
    public ObservableCollection<AntennaRow> RelayAntennas { get; } = [];
    public IReadOnlyList<AntennaChoice> AntennaChoices { get; private set; } = [];

    public RelayCommand AddCraftAntennaCommand { get; }
    public RelayCommand AddRelayAntennaCommand { get; }

    /// <summary>Fills the craft's antennas from the design open in the Engineering document.</summary>
    public RelayCommand UseDesignCommand { get; }

    public string UseDesignText => _engineering.DesignTitle is { Length: > 0 } title ? $"Use “{title}”" : "Use the Engineering design";

    public IReadOnlyList<Choice<ChartParts>> PartsChoices { get; }
    public Choice<ChartParts> PartsChoice { get => _partsChoice; set { if (value is not null && SetProperty(ref _partsChoice, value)) RefreshChoices(); } }

    public IReadOnlyList<Choice<int>> Levels { get; }
    public Choice<int> Level { get => _level; set { if (value is not null && SetProperty(ref _level, value)) Recalculate(); } }

    public string RangeModifierText { get => _rangeModifierText; set { if (SetProperty(ref _rangeModifierText, value)) Recalculate(); } }
    public string DsnModifierText { get => _dsnModifierText; set { if (SetProperty(ref _dsnModifierText, value)) Recalculate(); } }

    /// <summary>"From the save: tracking station level 3, range ×1, DSN ×1."</summary>
    public string SaveSettingsText { get; private set; } = "";

    // ---------------- Results (the document) ----------------

    public IReadOnlyList<RangeCard> Cards { get; private set; } = [];
    public IReadOnlyList<BodyLinkRow> Bodies { get; private set; } = [];
    public bool HasRelay { get; private set; }
    public string Summary { get; private set; } = "";

    /// <summary>Where 1 Mm, 10 Mm … 100 Gm fall on the bars (0 to 1), for the scale under them.</summary>
    public IReadOnlyList<(string Label, double Fraction)> ScaleTicks { get; } =
        new[] { 1e6, 1e7, 1e8, 1e9, 1e10, 1e11 }.Select(m => (Units.Scaled(m), Fraction(m))).ToList();

    // ---------------- Working it out ----------------

    /// <param name="relay">Start with the weakest relay antenna (a relay's list only counts those).</param>
    private void Add(ObservableCollection<AntennaRow> list, bool relay = false)
    {
        var first = relay ? AntennaChoices.FirstOrDefault(c => c.Part.Antennas.Any(a => a.Type == "RELAY")) ?? AntennaChoices.FirstOrDefault()
            : AntennaChoices.FirstOrDefault();
        list.Add(new AntennaRow(first, 1, Recalculate, r => { list.Remove(r); Recalculate(); }));
        Recalculate();
    }

    private void UseDesign()
    {
        var design = _engineering.DesignAntennas;
        if (design.Count == 0) return;
        CraftAntennas.Clear();
        foreach (var (part, count) in design)
            CraftAntennas.Add(new AntennaRow(Choice(part), count, Recalculate, r => { CraftAntennas.Remove(r); Recalculate(); }));
        Recalculate();
    }

    private void OnEngineeringChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(EngineeringViewModel.DesignTitle)) return;
        OnPropertyChanged(nameof(UseDesignText));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        // Nothing entered yet: start from the design.
        if (CraftAntennas.Count == 0) UseDesign();
    }

    private void ReadSaveSettings()
    {
        var settings = _sync.Save?.CommNet;
        if (settings is null || settings == _fromSave) return;
        _fromSave = settings;
        _level = Levels.FirstOrDefault(l => l.Value == settings.TrackingStationLevel) ?? Levels[^1];
        _rangeModifierText = settings.RangeModifier.ToString("0.##", C);
        _dsnModifierText = settings.DsnModifier.ToString("0.##", C);
        SaveSettingsText = (settings.Enabled ? "" : "CommNet is off in this save, so none of this applies there. ") +
                           $"From the save: tracking station level {settings.TrackingStationLevel}, range ×{_rangeModifierText}, " +
                           $"tracking station power ×{_dsnModifierText}.";
        foreach (var name in new[] { nameof(Level), nameof(RangeModifierText), nameof(DsnModifierText), nameof(SaveSettingsText) })
            OnPropertyChanged(name);
        Recalculate();
    }

    private bool Allowed(KspPart part) => PartsChoice.Value switch
    {
        ChartParts.All => true,
        ChartParts.UnlockedAndPlanned => _parts.StatusOf(part) is PartStatus.Unlocked or PartStatus.Planned or PartStatus.NotBought,
        _ => _parts.StatusOf(part) == PartStatus.Unlocked,
    };

    private void RefreshChoices()
    {
        AntennaChoices = _parts.Catalog.Parts.Values
            .Where(p => PartCategories.IsShown(p) && p.Antennas.Any(a => a.Type != "INTERNAL") && Allowed(p))
            .OrderBy(p => p.Antennas.Max(a => a.Power)).ThenBy(p => p.Title)
            .Select(Describe).ToList();
        OnPropertyChanged(nameof(AntennaChoices));
        // Rows keep their antenna (a locked one stays listed for them until changed).
        foreach (var row in CraftAntennas.Concat(RelayAntennas))
            if (row.Antenna is { } a && AntennaChoices.FirstOrDefault(c => c.Part.Name == a.Part.Name) is { } same) row.Antenna = same;
        Recalculate();
    }

    private AntennaChoice Choice(KspPart part) => AntennaChoices.FirstOrDefault(c => c.Part.Name == part.Name) ?? Describe(part);

    private static AntennaChoice Describe(KspPart part)
    {
        var antenna = part.Antennas.Where(a => a.Type != "INTERNAL").MaxBy(a => a.Power)!;
        var type = antenna.Type == "RELAY" ? "relay" : "direct";
        return new AntennaChoice(part, $"{part.Title} · {Units.Scaled(antenna.Power)} {type}{(antenna.Combinable ? "" : ", doesn't combine")}");
    }

    private void Recalculate()
    {
        var range = Parse(RangeModifierText) ?? 1;
        var dsn = Parse(DsnModifierText) ?? 1;
        var station = CommNet.TrackingStation(Level.Value, dsn);
        var craftAntennas = CraftAntennas.SelectMany(r => r.Antennas).ToList();
        var relayAntennas = RelayAntennas.SelectMany(r => r.Antennas).ToList();
        var craft = CommNet.CombinedPower(craftAntennas, range);
        var relay = CommNet.RelayPower(relayAntennas, range);
        HasRelay = relay > 0;

        var craftHome = CommNet.Range(craft, station);
        var relayHome = CommNet.Range(relay, station);
        var cards = new List<RangeCard>
        {
            new("Craft ↔ tracking station", craft > 0 ? Units.Scaled(craftHome) : "—",
                craft > 0 ? $"craft {Units.Scaled(craft)}, station {Units.Scaled(station)}" : "Add the craft's antennas in the Calculators pane."),
        };
        if (HasRelay)
        {
            cards.Add(new("Relay ↔ tracking station", Units.Scaled(relayHome), $"relay {Units.Scaled(relay)}"));
            cards.Add(new("Craft ↔ relay", craft > 0 ? Units.Scaled(CommNet.Range(craft, relay)) : "—", "how far the craft can be from the relay"));
            cards.Add(new("Relay ↔ relay", Units.Scaled(CommNet.Range(relay, relay)), "two of these relays, for a chain or a constellation"));
        }
        else if (relayAntennas.Count > 0)
            cards.Add(new("Relay", "—", "No relay antenna in the relay's list: direct antennas don't pass signals on."));
        Cards = cards;

        var system = _sync.StockBodies().Values.ToList();
        var home = system.FirstOrDefault(b => b.IsDefault)?.Name ?? "home";
        Bodies = system
            .Where(b => b.BodyClass != "Star" && !b.IsDefault)
            .Select(b => (Body: b, Far: CommNet.FarthestFromHome(b, system)))
            .Where(x => x.Far is not null)
            .OrderBy(x => Array.IndexOf(SavedOrbit.StockBodyOrder, x.Body.Name) is var i and >= 0 ? i : int.MaxValue)
            .Select(x =>
            {
                var d = x.Far!.Value;
                var craftStrength = CommNet.Strength(d, craftHome);
                var relayStrength = CommNet.Strength(d, relayHome);
                return new BodyLinkRow(x.Body.Name, Units.Scaled(d), Fraction(d), craft > 0 ? Fraction(craftHome) : null,
                    HasRelay ? Fraction(relayHome) : null, craft > 0 ? Signal(craftStrength) : "—", craftStrength > 0,
                    HasRelay ? Signal(relayStrength) : "", relayStrength > 0, HasRelay);
            }).ToList();
        Summary = $"Signal at each body's farthest from {home} (both on the far side of their orbits). A relay there, " +
                  "with a craft close by, is how to reach past the craft's own range.";
        foreach (var name in new[] { nameof(Cards), nameof(Bodies), nameof(HasRelay), nameof(Summary) }) OnPropertyChanged(name);
    }

    private static string Signal(double strength) => strength <= 0 ? "out of range" : strength.ToString("0%", C);

    private static double Fraction(double metres) =>
        Math.Clamp(Math.Log(Math.Max(metres, ScaleMinM) / ScaleMinM) / Math.Log(ScaleMaxM / ScaleMinM), 0, 1);

    private static double? Parse(string? text) =>
        double.TryParse((text ?? "").Trim().TrimStart('×', 'x'), NumberStyles.Float, C, out var v) && v > 0 && double.IsFinite(v) ? v : null;
}
