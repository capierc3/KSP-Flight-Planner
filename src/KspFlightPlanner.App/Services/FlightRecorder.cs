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
/// vessel (recovered or lost) or the game goes back to before the launch (reverted). Nothing is recorded or ended until
/// the game is known to be playing the chosen save (<see cref="SaveSync.PlayingOtherSave"/>), so another save's flights
/// never land in this one's log.
/// </summary>
public sealed class FlightRecorder : ObservableObject, IDisposable
{
    private static readonly TimeSpan ReadEvery = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan StoreEvery = TimeSpan.FromSeconds(15);

    /// <summary>A gap in the recording shorter than this (game seconds) isn't worth a line in the timeline.</summary>
    private const double NotableGapS = 5;

    /// <summary>
    /// Readings skipped after opening the streams: a vessel just switched to can read wrong while the game puts it
    /// together (a 4.9 t shuttle once read 19 t for its first reading).
    /// </summary>
    private const int SettleReads = 2;

    private readonly KspLink _link;
    private readonly SaveSync _sync;
    private readonly FlightLogRepository _logs;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<long, (FlightLogEventKind Kind, string Why)> _leftBecause = [];
    private readonly Dictionary<long, ulong> _vesselOf = []; // log → kRPC id of the vessel last recorded for it
    private int _unsettledReads; // readings still to skip from streams just opened
    private List<(long Id, double LaunchUt)> _open = [];
    private (string Name, double LaunchUt)? _deleted; // the flight being flown was deleted: don't start it again
    private LiveTelemetry? _telemetry;
    private ulong _recordingId; // kRPC's id for the vessel being recorded
    private (string Name, int Parts)? _pad; // the vessel on the pad and its part count, for the log made when it lifts off
    private bool _readingPad;
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
        link.GameUpdated += game => { if (game is not null && SaveConfirmed) EndReverted(game.Ut); };
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

    /// <summary>The game is playing the chosen save (as far as can be told): the flights it flies belong in this log.</summary>
    private bool SaveConfirmed => _sync.LiveSaveChecked && !_sync.PlayingOtherSave;

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

    /// <summary>Files a flight under another rocket name and/or tags it to one of the save's vessels (see <see cref="FlightLogRepository.Retag"/>).</summary>
    public void Retag(long id, string rocket, string? vesselId)
    {
        _logs.Retag(id, rocket, vesselId);
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
        else if (e.PropertyName is nameof(SaveSync.PlayingOtherSave) or nameof(SaveSync.LiveSaveChecked))
        {
            if (_sync.PlayingOtherSave) Leave(FlightLogEventKind.SwitchedAway, "KSP is playing another save");
            UpdateStatus();
        }
        else if (e.PropertyName == nameof(SaveSync.FileSave) && _sync.FileSave is { } file) EndFromSave(file);
    }

    private void Tick()
    {
        if (_opening) return;
        if (!SaveConfirmed)
        {
            if (_recorder is not null || _telemetry is not null)
                Leave(FlightLogEventKind.SwitchedAway, _sync.PlayingOtherSave ? "KSP is playing another save" : "Checking which save KSP is playing");
            UpdateStatus();
            return;
        }
        if (_telemetry is null)
        {
            _ = OpenAsync();
            return;
        }

        var sample = _telemetry.Read(out var end);
        if (sample is null)
        {
            if (end is TelemetryEnd.OtherBody or TelemetryEnd.Stale)
            {
                // Same flight, now around another body (or the orbit readings stopped following it, e.g. just after a
                // quickload): open the readings again, and carry on with the same log.
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
        if (_recorder is not null && _telemetry.VesselId != _recordingId)
        {
            // Reopened after a change of body, but the player has switched vessel meanwhile.
            Leave(FlightLogEventKind.SwitchedAway, "Switched to another vessel");
            return;
        }
        if (_unsettledReads > 0)
        {
            _unsettledReads--;
            return;
        }
        if (_recorder is null)
        {
            if (!Recordable(_telemetry.VesselType))
            {
                Status = $"Not recording {_telemetry.VesselName}: {(_telemetry.VesselType == "EVA" ? "a kerbal on EVA" : _telemetry.VesselType.ToLowerInvariant())}.";
                return;
            }
            // On rails the readings can be from different moments (kRPC doesn't refresh them all each frame), so the flight
            // is only told apart (by its launch time) once out of warp.
            if (sample.WarpRate > FlightLogRecorder.PhysicsWarpMax)
            {
                Status = $"{_telemetry.VesselName}: recording picks up when the warp ends.";
                return;
            }
            if (sample.Situation == "PRELAUNCH")
            {
                // Not launched yet: count its parts meanwhile (the design's fingerprint, with the launch mass).
                if (_pad?.Name != _telemetry.VesselName && !_readingPad) _ = ReadPadAsync(_telemetry.VesselName);
                UpdateStatus(sample);
                return;
            }
            if (_deleted is { } d && d.Name == _telemetry.VesselName && Math.Abs(sample.Ut - sample.MetS - d.LaunchUt) <= FlightLogs.SameLaunchS)
            {
                Status = $"Not recording {d.Name}: its log was deleted.";
                return;
            }
            _recorder = Resume(_telemetry.VesselName, _telemetry.VesselId, sample) ?? NewLog(_telemetry.VesselName, sample);
            _recordingId = _telemetry.VesselId;
            Store();
            _vesselOf[_recorder.Id] = _recordingId;
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
            _unsettledReads = SettleReads;
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

    /// <summary>Kerbals on EVA, debris, flags and the like aren't flights worth a log.</summary>
    private static bool Recordable(string vesselType) =>
        vesselType is not ("EVA" or "Debris" or "Flag" or "SpaceObject" or "DroppedPart" or "DeployedScienceController" or "DeployedSciencePart");

    private FlightLogRecorder NewLog(string vesselName, FlightSample first)
    {
        var recorder = new FlightLogRecorder(vesselName, first, FlightFor(vesselName));
        if (recorder.Log.FromLaunch && _pad is { } pad && pad.Name == vesselName) recorder.SetLaunchPartCount(pad.Parts);
        _pad = null;
        return recorder;
    }

    private async Task ReadPadAsync(string vesselName)
    {
        _readingPad = true;
        try { _pad = (vesselName, await _link.ActiveVesselPartCountAsync()); }
        catch (Exception) { /* left the pad or the link dropped: no count for this launch */ }
        finally { _readingPad = false; }
    }

    /// <summary>
    /// The stored log of this flight, picked up where it was left, with the gap noted. Found by launch time: the vessel may
    /// have been renamed since. Of logs launched together (vessels that split apart), the one last recorded with this kRPC
    /// vessel, else the one of this name.
    /// </summary>
    private FlightLogRecorder? Resume(string vesselName, ulong vesselId, FlightSample sample)
    {
        var launched = _logs.GetInFlight().Where(l => l.LaunchedAt(sample.Ut, sample.MetS)).ToList();
        var log = launched.FirstOrDefault(l => _vesselOf.GetValueOrDefault(l.Id) == vesselId)
            ?? launched.FirstOrDefault(l => l.VesselName == vesselName)
            ?? (launched.Count == 1 ? launched[0] : null);
        if (log is null) return null;
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

    /// <summary>
    /// Ends flights the save no longer has (recovered or lost), or that the game went back before (reverted). Not the one
    /// being flown, which can't be gone: recovering or losing it ends the recording first.
    /// </summary>
    private void EndFromSave(KspSave file)
    {
        var changed = false;
        foreach (var log in _logs.GetInFlight())
        {
            var flying = _recorder?.Id == log.Id;
            var current = flying ? _recorder!.Log : log;
            if (FlightLogs.EndedIn(current, file) is not { } outcome) continue;
            if (flying && outcome != FlightLogOutcome.Reverted) continue;
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
        if (recorder == _recorder)
        {
            // Open the streams again rather than carry on with these: they still have the vessel's old name, and a log
            // started from them straight away would be a zero-length twin of this one.
            _recorder = null;
            if (_telemetry is { } telemetry)
            {
                _telemetry = null;
                _ = _link.CloseTelemetryAsync(telemetry);
            }
        }
        recorder.End(outcome, FlightLogs.EndText(outcome));
        _logs.Save(recorder.Log);
        _leftBecause.Remove(log.Id);
        _vesselOf.Remove(log.Id);
        LogsChanged?.Invoke();
        UpdateStatus();
    }

    private void UpdateStatus(FlightSample? sample = null)
    {
        Status = !Enabled ? "Recording is off (File › Settings › Live connection)."
            : !_link.IsConnected ? "Connect to KSP to record flights."
            : _sync.PlayingOtherSave ? $"Not recording: {_sync.OtherSaveText}."
            : !_sync.LiveSaveChecked ? "Checking which save KSP is playing…"
            : !_link.InFlight ? "Records when you fly a vessel."
            : _recorder is { } r && sample is not null
                ? $"Recording {r.VesselName} · MET {FlightLogs.Duration(sample.MetS)} · {r.DeltaVUsed:N0} m/s used"
            : sample?.Situation == "PRELAUNCH" ? $"{_telemetry?.VesselName} on the pad: recording starts at launch."
            : "Waiting for the flown vessel…";
    }
}
