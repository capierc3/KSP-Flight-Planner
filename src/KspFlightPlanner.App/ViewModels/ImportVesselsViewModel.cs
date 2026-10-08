using KspFlightPlanner.App.Services;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.App.ViewModels;

/// <summary>One vessel in the import list.</summary>
public sealed class VesselRow : ObservableObject
{
    private readonly Action _changed;
    private bool _isChecked;

    public VesselRow(VesselImport import, string? linkedTo, Action changed)
    {
        Import = import;
        LinkedTo = linkedTo;
        _changed = changed;
        _isChecked = CanCheck && SaveImport.IsUsuallyWanted(import.Vessel);
    }

    public VesselImport Import { get; }
    public string? LinkedTo { get; }
    public bool CanCheck => Import.CanImport && LinkedTo is null;

    public bool IsChecked
    {
        get => _isChecked;
        set { if (CanCheck && SetProperty(ref _isChecked, value)) _changed(); }
    }

    public string Name => Import.Vessel.Name;
    public string Type => SaveImport.TypeText(Import.Vessel.Type);
    public string Orbiting => Import.Location?.BodyName ?? Import.Vessel.Orbit?.BodyName ?? "—";
    public string Orbit => Import.Location is { } l ? CustomLocations.AltitudeText(l) + (l.InclinationDeg >= 0.05 ? $", {l.InclinationDeg:0.#}°" : "") : "";
    public string Status => LinkedTo is { } name ? $"Linked to \"{name}\" — follows the save"
        : Import.Problem ?? (SaveImport.IsUsuallyWanted(Import.Vessel) ? "New" : "New (not ticked by default)");
    public bool IsMuted => !CanCheck;
}

/// <summary>The Import vessels window: pick vessels from the save to add as linked custom locations.</summary>
public sealed class ImportVesselsViewModel : ObservableObject
{
    private readonly SaveSync _sync;

    public ImportVesselsViewModel(SaveSync sync)
    {
        _sync = sync;
        var save = sync.Save!;
        Header = $"{sync.SaveName} — {save.Title}";
        Subheader = $"Game time {GameCalendar.Kerbin.FormatDate(save.Ut)} · {save.Vessels.Count} objects in the save";
        Rows = sync.Candidates()
            .Select(c => new VesselRow(c, sync.LinkedLocation(c.Vessel)?.Name, () => OnPropertyChanged(nameof(ImportText))))
            .OrderByDescending(r => r.CanCheck)
            .ThenByDescending(r => SaveImport.IsUsuallyWanted(r.Import.Vessel))
            .ThenByDescending(r => r.LinkedTo is not null)
            .ThenBy(r => r.Orbiting)
            .ThenBy(r => r.Name)
            .ToList();
        ImportCommand = new RelayCommand(DoImport, () => Rows.Any(r => r.IsChecked));
        SelectAllCommand = new RelayCommand(() => { foreach (var r in Rows) r.IsChecked = true; });
        SelectNoneCommand = new RelayCommand(() => { foreach (var r in Rows) r.IsChecked = false; });
    }

    public string Header { get; }
    public string Subheader { get; }
    public IReadOnlyList<VesselRow> Rows { get; }
    public RelayCommand ImportCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }

    public string ImportText => Rows.Count(r => r.IsChecked) is var n && n == 1 ? "Import 1 vessel" : $"Import {n} vessels";

    /// <summary>Ids of the locations created, once imported.</summary>
    public IReadOnlyList<long> ImportedIds { get; private set; } = [];

    /// <summary>Closes the window; set by the view.</summary>
    public Action Close { get; set; } = () => { };

    private void DoImport()
    {
        ImportedIds = _sync.Import(Rows.Where(r => r.IsChecked).Select(r => r.Import));
        Close();
    }
}
