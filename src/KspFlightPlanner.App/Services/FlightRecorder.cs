using System.ComponentModel;
using System.Windows.Threading;
using KspFlightPlanner.App.ViewModels;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App.Services;

/// <summary>
/// Records the flight log: while connected and flying, the flown vessel is read twice a second (over kRPC streams) and
/// its flight stored, from the launch on. A flight left (another vessel, another scene, the link dropped) is picked up
/// again when the player comes back to it; the gap goes in its timeline. Flights end when the save file no longer has the
/// vessel (recovered or lost) or the game goes back to before the launch (reverted).
/// </summary>
public sealed class FlightRecorder : ObservableObject, IDisposable
{
    private static readonly TimeSpan ReadEvery = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan StoreEvery = TimeSpan.FromSeconds(15);

    /// <summary>A gap in the recording shorter than this (game seconds) isn't worth a line in the timeline.</summary>
    private const double NotableGapS = 5;

    private readonly KspLink _link;
    private readonly SaveSync _sync;
    private readonly FlightLogRepository _logs;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<long, (FlightLogEventKind Kind, string Why)> _leftBecause = [];
    private List<(long Id, double LaunchUt)> _open = [];
    private (string Name, double LaunchUt)? _deleted; // the flight being flown was deleted: don't start it again
    private LiveTelemetry? _telemetry;
    private bool _opening;
    private FlightLogRecorder? _recorder;
    private DateTime _storedAt;
    private string _status = "";

    public FlightRecorder(KspLink link, SaveSync sync, FlightLogRepository logs)
    {
        _link = link;
        _sync = sync;
        _logs = logs;
        _timer = new DispatcherTimer { Interval = ReadEvery };
        _timer.Tick += (_, _) => Tick();
        link.PropertyChanged += OnLinkChanged;
        link.GameUpdated += game => { if (game is not null) EndReverted(game.Ut); };
        sync.PropertyChanged += OnSyncChanged;
        LogsChanged += RefreshOpen;
        RefreshOpen();
        UpdateStatus();
    }

    /// <summary>Raised when a log was stored, ended or deleted.</summary>
    public event Action? LogsChanged;

    /// <summary>The planner flight being flown with a vessel of this name, if any (a new log is linked to it).</summary>
    public Func<string, long?> FlightFor { get; set; } = _ => null;

    /// <summary>The log being recorded now, or null.</summary>
    public FlightLog? Current => _recorder?.Log;

    /// <summary>One line on what's being recorded.</summary>
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private bool Enabled => _sync.Settings.RecordFlights;

    public void Delete(long id)
    {
        if (_recorder is { } r && r.Id == id)
        {
            _deleted = (r.VesselName, r.LaunchUt);
            Leave(FlightLogEventKind.SwitchedAway, "", store: false);
        }
        _logs.Delete(id);
        LogsChanged?.Invoke();
    }

    public void Dispose()
    {
        Leave(FlightLogEventKind.Disconnected, "Flight disconnected (the planner closed)");
        _timer.Stop();
    }

    private void OnLinkChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(KspLink.IsConnected) or nameof(KspLink.InFlight))) return;
        if (!_link.IsConnected) Leave(FlightLogEventKind.Disconnected, "Flight disconnected");
        else if (!_link.InFlight) Leave(FlightLogEventKind.SwitchedAway, "Left the flight");
        if (_link.IsConnected && _link.InFlight && Enabled) _timer.Start();
        else _timer.Stop();
        UpdateStatus();
    }

    private void OnSyncChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SaveSync.Settings))
        {
            if (!Enabled) Leave(FlightLogEventKind.SwitchedAway, "Recording turned off");
            if (_link.IsConnected && _link.InFlight && Enabled) _timer.Start();
            UpdateStatus();
        }
        else if (e.PropertyName == nameof(SaveSync.FileSave) && _sync.FileSave is { } file) EndFromSave(file);
    }

    private void Tick()
    {
        if (_opening) return;
        if (_telemetry is null)
        {
            _ = OpenAsync();
            return;
        }

        var sample = _telemetry.Read(out var end);
        if (sample is null)
        {
            if (end == TelemetryEnd.OtherBody)
            {
                // Same flight, now around another body: read against that one, and carry on with the same log.
                var stale = _telemetry;
                _telemetry = null;
                _ = _link.CloseTelemetryAsync(stale);
                _ = OpenAsync();
                return;
            }
            // Another vessel, or this one's gone: open again for whatever is flown now.
            Leave(FlightLogEventKind.SwitchedAway, end == TelemetryEnd.OtherVessel ? "Switched to another vessel" : "Lost the vessel's readings");
            return;
        }
        if (_recorder is { } current && !(_telemetry.VesselName == current.VesselName
                                          && Math.Abs(sample.Ut - sample.MetS - current.LaunchUt) <= FlightLogs.SameLaunchS))
        {
            // Reopened after a change of body, but the player has switched vessel meanwhile.
            Leave(FlightLogEventKind.SwitchedAway, "Switched to another vessel");
            return;
        }
        if (_recorder is null)
        {
            if (sample.Situation == "PRELAUNCH") { UpdateStatus(sample); return; } // not launched yet
            if (_deleted is { } d && d.Name == _telemetry.VesselName && Math.Abs(sample.Ut - sample.MetS - d.LaunchUt) <= FlightLogs.SameLaunchS)
            {
                Status = $"Not recording {d.Name}: its log was deleted.";
                return;
            }
            _recorder = Resume(_telemetry.VesselName, sample)
                        ?? new FlightLogRecorder(_telemetry.VesselName, sample, FlightFor(_telemetry.VesselName));
            Store();
        }
        else
        {
            _recorder.Add(sample);
            if (DateTime.UtcNow - _storedAt >= StoreEvery && _recorder.Dirty) Store();
        }
        UpdateStatus(sample);
    }

    private async Task OpenAsync()
    {
        _opening = true;
        try
        {
            _telemetry = await _link.OpenTelemetryAsync();
        }
        catch (Exception)
        {
            _telemetry = null; // between scenes, or the link dropped (it says why)
        }
        finally
        {
            _opening = false;
        }
        // Closed or turned off while opening.
        if (_telemetry is not null && !(_link.IsConnected && _link.InFlight && Enabled))
        {
            var stale = _telemetry;
            _telemetry = null;
            _ = _link.CloseTelemetryAsync(stale);
        }
    }

    /// <summary>The stored log of this flight, picked up where it was left, with the gap noted.</summary>
    private FlightLogRecorder? Resume(string vesselName, FlightSample sample)
    {
        if (_logs.GetInFlight().FirstOrDefault(l => l.IsFlightOf(vesselName, sample.Ut, sample.MetS)) is not { } log) return null;
        var recorder = new FlightLogRecorder(log);
        var gap = sample.Ut - log.LastUt;
        if (gap >= NotableGapS)
        {
            var (kind, why) = _leftBecause.GetValueOrDefault(log.Id, (FlightLogEventKind.Disconnected, "Flight disconnected"));
            recorder.Note(kind == FlightLogEventKind.Disconnected ? FlightLogEventKind.Reconnected : FlightLogEventKind.Returned,
                $"{why}: {FlightLogs.Duration(gap)} not recorded", gap);
        }
        _leftBecause.Remove(log.Id);
        recorder.Add(sample);
        return recorder;
    }

    /// <summary>Stops recording the current flight (it stays open, to be picked up again) and closes the streams.</summary>
    private void Leave(FlightLogEventKind kind, string why, bool store = true)
    {
        if (_recorder is { } recorder)
        {
            recorder.Flush();
            if (store) Store();
            _leftBecause[recorder.Id] = (kind, why);
            _recorder = null;
        }
        if (_telemetry is { } telemetry)
        {
            _telemetry = null;
            _ = _link.CloseTelemetryAsync(telemetry);
        }
        UpdateStatus();
    }

    private void Store()
    {
        if (_recorder is not { } recorder) return;
        recorder.Id = _logs.Save(recorder.Log);
        recorder.MarkSaved();
        _storedAt = DateTime.UtcNow;
        LogsChanged?.Invoke();
    }

    /// <summary>Ends flights the save no longer has (recovered or lost), or that the game went back before (reverted).</summary>
    private void EndFromSave(KspSave file)
    {
        var changed = false;
        foreach (var log in _logs.GetInFlight())
        {
            var current = _recorder?.Id == log.Id ? _recorder.Log : log;
            if (FlightLogs.EndedIn(current, file) is not { } outcome) continue;
            End(log, outcome);
            changed = true;
        }
        if (changed) LogsChanged?.Invoke();
    }

    /// <summary>Ends flights launched after the game's time now: the player reverted (or loaded an older save).</summary>
    private void EndReverted(double ut)
    {
        var reverted = _open.Where(o => ut < o.LaunchUt - FlightLogs.SameLaunchS).Select(o => o.Id).ToList();
        if (reverted.Count == 0) return;
        foreach (var log in _logs.GetInFlight().Where(l => reverted.Contains(l.Id)))
            End(_recorder?.Id == log.Id ? _recorder.Log : log, FlightLogOutcome.Reverted);
    }

    private void RefreshOpen() =>
        _open = _logs.GetAll().Where(l => l.Outcome == FlightLogOutcome.InFlight).Select(l => (l.Id, l.LaunchUt)).ToList();

    private void End(FlightLog log, FlightLogOutcome outcome)
    {
        var recorder = _recorder?.Id == log.Id ? _recorder : new FlightLogRecorder(log);
        if (recorder == _recorder) _recorder = null;
        recorder.End(outcome, FlightLogs.EndText(outcome));
        _logs.Save(recorder.Log);
        _leftBecause.Remove(log.Id);
        LogsChanged?.Invoke();
        UpdateStatus();
    }

    private void UpdateStatus(FlightSample? sample = null)
    {
        Status = !Enabled ? "Recording is off (File › Settings › Live connection)."
            : !_link.IsConnected ? "Connect to KSP to record flights."
            : !_link.InFlight ? "Records when you fly a vessel."
            : _recorder is { } r && sample is not null
                ? $"Recording {r.VesselName} · MET {FlightLogs.Duration(sample.MetS)} · {r.DeltaVUsed:N0} m/s used"
            : sample?.Situation == "PRELAUNCH" ? $"{_telemetry?.VesselName} on the pad: recording starts at launch."
            : "Waiting for the flown vessel…";
    }
}
