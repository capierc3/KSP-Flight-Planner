using System.Collections.ObjectModel;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App.ViewModels;

/// <summary>One recorded flight in the Flight log list.</summary>
/// <param name="grouped">Whether the list is grouped by rocket, so the row needn't name it.</param>
/// <param name="vessel">The save's vessel the flight belongs to now (FlightLogs.VesselOf), if it's still there.</param>
public sealed class FlightLogItem(FlightLogSummary log, bool grouped = true, SavedVessel? vessel = null)
{
    public FlightLogSummary Log { get; } = log;
    public long Id => Log.Id;

    /// <summary>The rocket: the vessel's name at launch, or what the player filed it under (the list groups by it).</summary>
    public string Rocket => Log.VesselName;

    /// <summary>The save's vessel this flight is now, if it's still in the save.</summary>
    public SavedVessel? Vessel { get; } = vessel;

    /// <summary>The row's bold text: how the flight went, or the vessel when the list isn't grouped by rocket.</summary>
    public string Heading => grouped ? Outcome : Vessel?.Name ?? Rocket;

    /// <summary>The vessel's name now when it has been renamed since launch (or the launch name when that heads the row), else null.</summary>
    public string? VesselLine => Vessel is not { } v || v.Name == Rocket ? null
        : grouped ? $"Now {v.Name}" : $"Launched as {Rocket}";

    public bool HasVesselLine => VesselLine is not null;

    /// <summary>Whether the flight was tagged to a vessel by hand, rather than found by launch time.</summary>
    public bool IsTagged => Log.VesselId is { Length: > 0 };

    /// <summary>The launch day, for the row; the full date and time is in <see cref="Launched"/>.</summary>
    public string LaunchDay => GameCalendar.Kerbin.FormatDay(Log.LaunchUt);

    public string Launched => (Log.FromLaunch ? "Launched " : "Joined in flight · launched ") + GameCalendar.Kerbin.FormatDate(Log.LaunchUt);

    public FlightLogOutcome OutcomeKind => Log.Outcome;

    public string Outcome => Log.Outcome switch
    {
        FlightLogOutcome.InFlight => Log.LastSituation switch
        {
            "LANDED" => $"Landed on {Log.LastBody}",
            "SPLASHED" => $"Splashed down on {Log.LastBody}",
            "ORBITING" => $"In orbit of {Log.LastBody}",
            _ => $"In flight at {Log.LastBody}",
        },
        var o => FlightLogs.EndText(o),
    };

    /// <summary>Δv used, where it went (or how high, around one body), and how long; led by how it went when the heading is the rocket.</summary>
    public string Figures =>
        (grouped ? "" : $"{Outcome} · ") +
        $"{Log.DeltaVUsed:N0} m/s used · " +
        (Log.Route is { } route && route.Contains('›') ? route : $"up to {Log.MaxAltitudeM / 1000:N1} km") +
        $" · {FlightLogs.Duration(Log.DurationS)}";

    /// <summary>What's missing from the recording in a few words, for the row (the details are in <see cref="Gaps"/>), or null.</summary>
    public string? GapsShort
    {
        get
        {
            var parts = new List<string>();
            if (!Log.FromLaunch) parts.Add("joined in flight");
            if (NotableWarps.Sum(w => w.DurationS) is var warp and > 0) parts.Add($"warped {FlightLogs.Duration(warp)}");
            var gaps = NotRecorded.ToList();
            if (gaps.Count > 0) parts.Add(gaps.Sum(g => g.DurationS) is var length and > 0 ? $"{FlightLogs.Duration(length)} not recorded" : "not all recorded");
            if (Quickloads > 0) parts.Add(Times("quickloaded", Quickloads));
            return parts.Count == 0 ? null : Capitalised(string.Join(" · ", parts));
        }
    }

    /// <summary>What's missing from the recording, a line per kind (joined late, warp, gaps, quickloads), or null.</summary>
    public string? Gaps
    {
        get
        {
            var lines = new List<string>();
            if (!Log.FromLaunch) lines.Add("Joined in flight: recorded from partway through");
            var warps = NotableWarps.ToList();
            if (warps.Count > 0)
                lines.Add($"Time warp: {FlightLogs.Duration(warps.Sum(w => w.DurationS))}" + (warps.Count > 1 ? $" over {warps.Count} warps" : "") + " (path worked out from the orbit)");

            var gaps = NotRecorded.ToList();
            if (gaps.Count > 0)
            {
                var why = new List<string>();
                if (gaps.Count(g => g.Kind == FlightLogEventKind.Returned) is var switched and > 0) why.Add(Times("switched away", switched));
                if (gaps.Count(g => g.Kind == FlightLogEventKind.Reconnected) is var dropped and > 0) why.Add(Times("disconnected", dropped));
                var length = gaps.Sum(g => g.DurationS);
                lines.Add((length > 0 ? $"Not recorded: {FlightLogs.Duration(length)}" : "Not recorded") + $" ({string.Join(", ", why)})");
            }

            if (Quickloads > 0) lines.Add(Times("Loaded a quicksave", Quickloads));
            return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
        }
    }

    public bool HasGaps => GapsShort is not null;

    // Short warps from logs stored before they were skipped have no length; leave them out.
    private IEnumerable<FlightLogEvent> NotableWarps =>
        Log.Events.Where(e => e.Kind == FlightLogEventKind.Warp && e.DurationS >= FlightLogRecorder.NotableWarpS);

    private IEnumerable<FlightLogEvent> NotRecorded =>
        Log.Events.Where(e => e.Kind is FlightLogEventKind.Reconnected or FlightLogEventKind.Returned);

    private int Quickloads => Log.Events.Count(e => e.Kind == FlightLogEventKind.Quickload);

    private static string Times(string what, int count) => count == 1 ? what : $"{what} {count}×";

    private static string Capitalised(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}

/// <summary>The Flight log pane: every recorded flight, grouped by rocket, and what's being recorded now.</summary>
public sealed class FlightLogViewModel : ObservableObject
{
    private readonly FlightLogRepository _logs;
    private readonly SaveSync _sync;
    private Dictionary<long, SavedVessel?> _vessels = [];
    private FlightLogItem? _selected;
    private IReadOnlyList<FlightLogSummary> _all = [];
    private string _search = "";
    private Choice<FlightLogShow> _show;
    private Choice<FlightLogSort> _sort;

    public FlightLogViewModel(FlightLogRepository logs, FlightRecorder recorder, SaveSync sync, PlannerEvents events,
        FlightReportViewModel report, FlightCompareViewModel compare)
    {
        _logs = logs;
        _sync = sync;
        Recorder = recorder;
        Report = report;
        Compare = compare;
        recorder.LogsChanged += Refresh;
        events.SaveChanged += Refresh;
        // Renames and new vessels show once KSP writes the save (or the live game says so).
        sync.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SaveSync.Save) && FindVessels()) Apply(); };
        DeleteCommand = new RelayCommand(Delete, () => _selected is not null);
        ShowVesselCommand = new RelayCommand(() => { if (_selected?.Vessel is { } v) ShowVesselRequested?.Invoke(v.Id); },
            () => _selected?.Vessel is not null);
        ClearFilterCommand = new RelayCommand(ClearFilter);
        _show = ShowChoices[0];
        _sort = SortChoices[0];
        Refresh();
    }

    public FlightRecorder Recorder { get; }

    /// <summary>The picked flight, in the Flight report document.</summary>
    public FlightReportViewModel Report { get; }

    /// <summary>A rocket's flights side by side, in the Compare flights document.</summary>
    public FlightCompareViewModel Compare { get; }

    /// <summary>Puts the picked flight's rocket in the Compare flights document.</summary>
    public void CompareSelected()
    {
        if (_selected is { } item) Compare.Show(item.Rocket);
    }

    /// <summary>The flights shown: those matching the search and filter, in the chosen order.</summary>
    public ObservableCollection<FlightLogItem> Logs { get; } = [];

    /// <summary>No flights recorded in this save at all (not just none matching).</summary>
    public bool HasNoLogs => _all.Count == 0;

    /// <summary>Flights are recorded, but the search or filter hides them all.</summary>
    public bool HasNoMatches => _all.Count > 0 && Logs.Count == 0;

    public bool IsFiltered => !string.IsNullOrWhiteSpace(_search) || _show.Value != FlightLogShow.All;

    /// <summary>"4 of 12 flights" while filtered, else "12 flights".</summary>
    public string CountText => IsFiltered ? $"{Logs.Count} of {Flights(_all.Count)}" : Flights(_all.Count);

    /// <summary>The list groups by rocket when it's ordered by rocket.</summary>
    public bool GroupByRocket => _sort.Value == FlightLogSort.Rocket;

    /// <summary>Words to look for in the rocket's name and the bodies it went to.</summary>
    public string Search
    {
        get => _search;
        set { if (SetProperty(ref _search, value ?? "")) Apply(); }
    }

    public IReadOnlyList<Choice<FlightLogShow>> ShowChoices { get; } =
    [
        new("All flights", FlightLogShow.All), new("In flight", FlightLogShow.InFlight), new("Recovered", FlightLogShow.Recovered),
        new("Lost", FlightLogShow.Lost), new("Reverted", FlightLogShow.Reverted),
    ];

    public Choice<FlightLogShow> Show
    {
        get => _show;
        set { if (value is not null && SetProperty(ref _show, value)) Apply(); }
    }

    public IReadOnlyList<Choice<FlightLogSort>> SortChoices { get; } =
    [
        new("Rocket", FlightLogSort.Rocket), new("Newest first", FlightLogSort.Newest), new("Oldest first", FlightLogSort.Oldest),
        new("Most Δv used", FlightLogSort.MostDeltaV), new("Longest", FlightLogSort.Longest), new("Highest", FlightLogSort.Highest),
    ];

    public Choice<FlightLogSort> Sort
    {
        get => _sort;
        set
        {
            if (value is null || !SetProperty(ref _sort, value)) return;
            OnPropertyChanged(nameof(GroupByRocket));
            Apply();
        }
    }

    public RelayCommand ClearFilterCommand { get; }

    public RelayCommand DeleteCommand { get; }

    /// <summary>Opens the picked flight's vessel in the Vessels pane.</summary>
    public RelayCommand ShowVesselCommand { get; }

    /// <summary>The Vessels pane should show this vessel (its save pid).</summary>
    public event Action<string>? ShowVesselRequested;

    /// <summary>What the picked flight's Edit dialog starts from, or null with nothing picked.</summary>
    public FlightLogEditViewModel? EditSelected() =>
        _selected is { } item ? new FlightLogEditViewModel(item.Log, _sync.Save, _all.Select(l => l.VesselName).Distinct(), Recorder.Retag) : null;

    /// <summary>Picks a flight (clearing a search or filter that hides it) and opens it in the Flight report.</summary>
    public void SelectLog(long id)
    {
        if (Logs.All(l => l.Id != id) && _all.Any(l => l.Id == id)) ClearFilter();
        SelectedLog = Logs.FirstOrDefault(l => l.Id == id);
    }

    /// <summary>Asks before deleting; the view supplies the dialog.</summary>
    public Func<string, bool> Confirm { get; set; } = _ => true;

    public FlightLogItem? SelectedLog
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedVesselText));
            if (value is not null && value.Id != Report.LogId) Report.Show(value.Id);
        }
    }

    public bool HasSelection => _selected is not null;

    /// <summary>Which of the save's vessels the picked flight is, for the line above the buttons.</summary>
    public string? SelectedVesselText => _selected is not { } item ? null
        : item.Vessel is { } v ? $"Vessel: {v.Name}" + (item.IsTagged ? "" : " (found by launch time)")
        : item.Log.VesselId == FlightLogs.NoVessel ? "Not tagged to a vessel"
        : item.Log.Outcome == FlightLogOutcome.InFlight ? "Vessel: not found in the save (Edit log to tag it)" : null;

    private void Refresh()
    {
        _all = _logs.GetAll();
        FindVessels();
        Apply();
        Compare.Refresh();
        // The report follows a flight still being recorded, and lets go of a deleted one.
        if (Report.LogId is { } shown) Report.Show(_all.Any(l => l.Id == shown) ? shown : null);
    }

    /// <summary>Fills the list from the stored flights through the search, filter and order.</summary>
    private void Apply()
    {
        var keep = _selected?.Id;
        Logs.Clear();
        foreach (var log in FlightLogQuery.Apply(_all, _search, _show.Value, _sort.Value, l => _vessels.GetValueOrDefault(l.Id)?.Name))
            Logs.Add(new FlightLogItem(log, GroupByRocket, _vessels.GetValueOrDefault(log.Id)));
        SelectedLog = Logs.FirstOrDefault(l => l.Id == keep);
        OnPropertyChanged(nameof(HasNoLogs));
        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(IsFiltered));
        OnPropertyChanged(nameof(CountText));
    }

    /// <summary>Finds each flight's vessel in the save; whether any flight's vessel (or its name) changed.</summary>
    private bool FindVessels()
    {
        var found = _sync.Save is { } save ? _all.ToDictionary(l => l.Id, l => FlightLogs.VesselOf(l, save)) : [];
        var changed = found.Count != _vessels.Count ||
                      found.Any(f => _vessels.GetValueOrDefault(f.Key) is var was && (was?.Id, was?.Name) != (f.Value?.Id, f.Value?.Name));
        _vessels = found;
        return changed;
    }

    private void ClearFilter()
    {
        _search = "";
        _show = ShowChoices[0];
        OnPropertyChanged(nameof(Search));
        OnPropertyChanged(nameof(Show));
        Apply();
    }

    private static string Flights(int count) => count == 1 ? "1 flight" : $"{count:N0} flights";

    private void Delete()
    {
        if (_selected is not { } item) return;
        if (!Confirm($"Delete the log of {item.Rocket}'s flight launched {GameCalendar.Kerbin.FormatDate(item.Log.LaunchUt)}?")) return;
        Recorder.Delete(item.Id);
    }
}
