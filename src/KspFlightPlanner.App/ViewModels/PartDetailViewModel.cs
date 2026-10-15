using System.Globalization;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.App.ViewModels;

/// <summary>
/// The Part pane: everything the part data knows about the part picked in the Parts pane — mass full and empty, what it
/// holds, its engine modes and antennas, and the tech node that unlocks it.
/// </summary>
public sealed class PartDetailViewModel : ObservableObject
{
    private static readonly Dictionary<string, string> Sizes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["size0"] = "0.625 m", ["size1"] = "1.25 m", ["size1p5"] = "1.875 m", ["size2"] = "2.5 m", ["size3"] = "3.75 m",
        ["size4"] = "5 m", ["srf"] = "radial", ["mk2"] = "Mk2", ["mk3"] = "Mk3",
    };

    private readonly PartAvailability _parts;
    private PartItem? _item;

    public PartDetailViewModel(PartAvailability parts)
    {
        _parts = parts;
        OpenNodeCommand = new RelayCommand(() => { if (_item?.Part.TechRequired is { } id) OpenNodeRequested?.Invoke(id); },
            () => _item?.Part.TechRequired is { } id && _parts.Catalog.TechTree.Any(n => n.Id == id));
    }

    /// <summary>The tech tree should show this node.</summary>
    public event Action<string>? OpenNodeRequested;

    public RelayCommand OpenNodeCommand { get; }

    public PartItem? Item
    {
        get => _item;
        set
        {
            if (!SetProperty(ref _item, value)) return;
            OnPropertyChanged(nameof(HasPart));
            OnPropertyChanged(nameof(Subtitle));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(Groups));
        }
    }

    public bool HasPart => Item is not null;

    /// <summary>E.g. "Engines · Rockomax Conglomerate".</summary>
    public string Subtitle => Item is null ? "" : string.Join(" · ", new[] { Item.Category, Item.Part.Manufacturer }.Where(s => !string.IsNullOrEmpty(s)));

    public string StatusText => Item is null ? "" : Item.Status switch
    {
        PartStatus.Unlocked => _parts.Research is null ? "Unlocked (sandbox)" : "Unlocked",
        PartStatus.Planned => $"Planned: comes with {Item.NodeTitle}",
        PartStatus.NotBought => $"{Item.NodeTitle} is researched; buy the part in the R&D building",
        _ => Item.NodeTitle is null ? "Locked" : $"Locked: needs {Item.NodeTitle}",
    };

    public IReadOnlyList<FactGroup> Groups => Item is null ? [] : Describe(Item.Part, _parts.Catalog);

    private static IReadOnlyList<FactGroup> Describe(KspPart p, PartCatalog catalog)
    {
        var c = CultureInfo.CurrentCulture;
        string Tonnes(double t) => t.ToString("0.####", c) + " t";
        var groups = new List<FactGroup>();

        var resourceMass = p.Resources.Sum(r => r.MaxAmount * catalog.Density(r.Name));
        var resourceCost = p.Resources.Sum(r => r.MaxAmount * (catalog.Resources.GetValueOrDefault(r.Name)?.UnitCost ?? 0));
        var part = new List<Fact> { new("Mass", resourceMass > 0 ? $"{Tonnes(p.MassT)} empty, {Tonnes(p.MassT + resourceMass)} full" : Tonnes(p.MassT)) };
        // KSP's part cost includes full tanks.
        part.Add(new("Cost", resourceCost > 0
            ? $"√{p.Cost.ToString("N0", c)} full, √{(p.Cost - resourceCost).ToString("N0", c)} empty"
            : $"√{p.Cost.ToString("N0", c)}"));
        if (p.BulkheadProfiles is { Count: > 0 } sizes)
            part.Add(new("Attaches", string.Join(", ", sizes.Select(s => Sizes.GetValueOrDefault(s, s)))));
        if (p.CrewCapacity > 0) part.Add(new("Crew", p.CrewCapacity.ToString(c)));
        if (p.CrashToleranceMs > 0) part.Add(new("Crash tolerance", $"{p.CrashToleranceMs.ToString("0.#", c)} m/s"));
        if (p.MaxTempK > 0) part.Add(new("Max temperature", $"{p.MaxTempK.ToString("N0", c)} K"));
        if (!p.FuelCrossfeed) part.Add(new("Fuel crossfeed", "No (fuel doesn't pass through)"));
        groups.Add(new("Part", part));

        if (p.Resources.Count > 0)
            groups.Add(new("Holds", p.Resources.Select(r =>
            {
                var mass = r.MaxAmount * catalog.Density(r.Name);
                return new Fact(catalog.DisplayName(r.Name), r.MaxAmount.ToString("N1", c) + (mass > 0 ? $" ({Tonnes(mass)})" : ""));
            }).ToList()));

        for (var i = 0; i < p.Engines.Count; i++)
        {
            var e = p.Engines[i];
            var facts = new List<Fact>();
            if (e.BreathesAir)
            {
                facts.Add(new("Thrust", $"{e.MaxThrustKn.ToString("N1", c)} kN (at best speed and altitude)"));
                facts.Add(new("Needs", "Intake air: no thrust in a vacuum"));
            }
            else
            {
                var seaLevel = e.IspVacuumS > 0 ? e.MaxThrustKn * e.IspSeaLevelS / e.IspVacuumS : 0;
                facts.Add(new("Thrust", $"{e.MaxThrustKn.ToString("N1", c)} kN vacuum, {seaLevel.ToString("N1", c)} kN sea level"));
                facts.Add(new("Isp", $"{e.IspVacuumS:0} s vacuum, {e.IspSeaLevelS:0} s sea level"));
                if (e.IspVacuumS > 0)
                    facts.Add(new("Fuel use", $"{(e.MaxThrustKn / (e.IspVacuumS * VesselAnalysis.G0) * 1000).ToString("N1", c)} kg/s at full thrust"));
            }
            var burns = e.Propellants.Where(pr => pr.Name != "IntakeAir").ToList();
            if (burns.Count > 0)
                facts.Add(new("Burns", string.Join(" : ", burns.Select(pr => $"{catalog.DisplayName(pr.Name)} {pr.Ratio.ToString("0.##", c)}"))));
            groups.Add(new(p.Engines.Count == 1 ? "Engine" : $"Engine mode {i + 1}" + (e.EngineId is { } id ? $" ({id})" : ""), facts));
        }

        foreach (var a in p.Antennas)
        {
            var type = a.Type switch { "RELAY" => "Relay (passes signal on)", "INTERNAL" => "Internal (probe core)", _ => "Direct" };
            groups.Add(new("Antenna", [
                new("Type", type),
                new("Power", Range(a.Power, c)),
                new("Combinable", a.Combinable ? $"Yes (exponent {a.CombinableExponent.ToString("0.##", c)})" : "No"),
            ]));
        }
        return groups;
    }

    /// <summary>An antenna's power rating as a distance (5 Gm, 500 km).</summary>
    private static string Range(double metres, CultureInfo c) => metres switch
    {
        >= 1e9 => (metres / 1e9).ToString("0.##", c) + " Gm",
        >= 1e6 => (metres / 1e6).ToString("0.##", c) + " Mm",
        >= 1e3 => (metres / 1e3).ToString("0.##", c) + " km",
        _ => metres.ToString("N0", c) + " m",
    };
}
