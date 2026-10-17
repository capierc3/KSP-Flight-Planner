using System.Collections.ObjectModel;
using KspFlightPlanner.App.Services;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App.ViewModels;

/// <summary>One recorded flight in the Flight log list.</summary>
public sealed class FlightLogItem(FlightLogSummary log)
{
    public FlightLogSummary Log { get; } = log;
    public long Id => Log.Id;

    /// <summary>The rocket: the vessel's name at launch (the list groups by it).</summary>
    public string Rocket => Log.VesselName;

    public string Launched => (Log.FromLaunch ? "" : "Joined in flight · ") + GameCalendar.Kerbin.FormatDate(Log.LaunchUt);

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

    /// <summary>Δv used, where it went (or how high, around one body), and how long.</summary>
    public string Figures =>
        $"{Log.DeltaVUsed:N0} m/s used · " +
        (Log.Route is { } route && route.Contains('›') ? route : $"up to {Log.MaxAltitudeM / 1000:N1} km") +
        $" · {FlightLogs.Duration(Log.DurationS)}";

    /// <summary>What's missing from the recording, a line per kind (warp, gaps, quickloads), or null.</summary>
    public string? Gaps
    {
        get
        {
            var lines = new List<string>();
            // Short warps from logs stored before they were skipped have no length; leave them out.
            var warps = Log.Events.Where(e => e.Kind == FlightLogEventKind.Warp && e.DurationS >= FlightLogRecorder.NotableWarpS).ToList();
            if (warps.Count > 0)
                lines.Add($"Time warp: {FlightLogs.Duration(warps.Sum(w => w.DurationS))}" + (warps.Count > 1 ? $" over {warps.Count} warps" : "") + " (path worked out from the orbit)");

            var gaps = Log.Events.Where(e => e.Kind is FlightLogEventKind.Reconnected or FlightLogEventKind.Returned).ToList();
            if (gaps.Count > 0)
            {
                var why = new List<string>();
                if (gaps.Count(g => g.Kind == FlightLogEventKind.Returned) is var switched and > 0) why.Add(Times("switched away", switched));
                if (gaps.Count(g => g.Kind == FlightLogEventKind.Reconnected) is var dropped and > 0) why.Add(Times("disconnected", dropped));
                var length = gaps.Sum(g => g.DurationS);
                lines.Add((length > 0 ? $"Not recorded: {FlightLogs.Duration(length)}" : "Not recorded") + $" ({string.Join(", ", why)})");
            }

            if (Log.Events.Count(e => e.Kind == FlightLogEventKind.Quickload) is var loads and > 0) lines.Add(Times("Loaded a quicksave", loads));
            return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
        }
    }

    private static string Times(string what, int count) => count == 1 ? what : $"{what} {count}×";

    public bool HasGaps => Gaps is not null;
}

/// <summary>The Flight log pane: every recorded flight, grouped by rocket, and what's being recorded now.</summary>
public sealed class FlightLogViewModel : ObservableObject
{
    private readonly FlightLogRepository _logs;
    private FlightLogItem? _selected;

    public FlightLogViewModel(FlightLogRepository logs, FlightRecorder recorder, PlannerEvents events, FlightReportViewModel report,
        FlightCompareViewModel compare)
    {
        _logs = logs;
        Recorder = recorder;
        Report = report;
        Compare = compare;
        recorder.LogsChanged += Refresh;
        events.SaveChanged += Refresh;
        DeleteCommand = new RelayCommand(Delete, () => _selected is not null);
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
    public ObservableCollection<FlightLogItem> Logs { get; } = [];
    public bool HasNoLogs => Logs.Count == 0;
    public RelayCommand DeleteCommand { get; }

    /// <summary>Asks before deleting; the view supplies the dialog.</summary>
    public Func<string, bool> Confirm { get; set; } = _ => true;

    public FlightLogItem? SelectedLog
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            if (value is not null) Report.Show(value.Id);
        }
    }

    public bool HasSelection => _selected is not null;

    private void Refresh()
    {
        var keep = _selected?.Id;
        Logs.Clear();
        foreach (var log in _logs.GetAll().OrderBy(l => l.VesselName, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(l => l.LaunchUt))
            Logs.Add(new FlightLogItem(log));
        SelectedLog = Logs.FirstOrDefault(l => l.Id == keep);
        OnPropertyChanged(nameof(HasNoLogs));
        Compare.Refresh();
        // The report follows a flight still being recorded, and lets go of a deleted one.
        if (Report.LogId is { } shown) Report.Show(Logs.Any(l => l.Id == shown) ? shown : null);
    }

    private void Delete()
    {
        if (_selected is not { } item) return;
        if (!Confirm($"Delete the log of {item.Rocket}'s flight launched {GameCalendar.Kerbin.FormatDate(item.Log.LaunchUt)}?")) return;
        Recorder.Delete(item.Id);
    }
}
