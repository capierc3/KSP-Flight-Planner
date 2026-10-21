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

    // Dockings and undockings: a reading that hints at one (the mass jumped, or the flown vessel vanished into another) is
    // checked against the next listing of the game's vessels, which shows which vessel went or came.
    private enum CheckKind { Joined, Left, Split }
    private sealed record DockCheck(CheckKind Kind, double Ut, ulong VesselId, long LogId, string Name, IReadOnlyList<LiveVessel> Before);
    private readonly List<DockCheck> _checks = [];
    private IReadOnlyList<LiveVessel>? _vessels; // the latest listing of the game's vessels
    private FlightSample? _lastRead; // the reading before, to spot a mass jump
    private long? _lastLogId; // the log recorded last: a vessel that comes off it carries on a log docked into it
    private readonly Dictionary<ulong, List<Action<FlightLogRecorder>>> _notesFor = []; // for a vessel not recorded yet

    public FlightRecorder(KspLink link, SaveSync sync, FlightLogRepository logs)
    {
        _link = link;
        _sync = sync;
        _logs = logs;
        _timer = new DispatcherTimer { Interval = ReadEvery };
        _timer.Tick += (_, _) => Tick();
        link.PropertyChanged += OnLinkChanged;
        link.GameUpdated += game =>
        {
            if (game is null) return;
            if (SaveConfirmed) EndReverted(game.Ut);
            OnVesselsRead(game);
        };
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
            // Another vessel, or this one's gone: open again for whatever is flown now. The flown vessel may have docked into
            // the one flown now (KSP keeps one of the two): see what the next listing of vessels says.
            if (end == TelemetryEnd.OtherVessel && _recorder is { } left && _lastRead is { Situation: not "PRELAUNCH" } last)
                Check(CheckKind.Left, last.Ut, left);
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
            if (_notesFor.Remove(_recordingId, out var notes))
                foreach (var note in notes) note(_recorder);
            Store();
            _vesselOf[_recorder.Id] = _recordingId;
        }
        else
        {
            _recorder.Add(sample);
            // Another vessel joined this one (docked) or came off it (undocked, or released without staging).
            if (_lastRead is { } before && FlightDocking.MassJump(before, sample) is var jump and not 0)
                Check(jump > 0 ? CheckKind.Joined : CheckKind.Split, sample.Ut, _recorder);
            if (DateTime.UtcNow - _storedAt >= StoreEvery && _recorder.Dirty) Store();
        }
        _lastRead = sample;
        UpdateStatus(sample);
    }

    private async Task OpenAsync()
    {
        _opening = true;
        try
        {
            _telemetry = await _link.OpenTelemetryAsync();
            _unsettledReads = SettleReads;
            _lastRead = null;
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
        var open = _logs.GetInFlight();
        var launched = open.Where(l => !l.Away && l.LaunchedAt(sample.Ut, sample.MetS)).ToList();
        var log = launched.FirstOrDefault(l => _vesselOf.GetValueOrDefault(l.Id) == vesselId)
            ?? launched.FirstOrDefault(l => l.VesselName == vesselName);
        FlightLogRecorder recorder;
        if (log is null && !FlightDocking.JustLaunched(sample)
            && FlightDocking.AwayLogNamed(vesselName, open.Select(l => l.Summary), _lastLogId) is { } awaySummary
            && open.FirstOrDefault(l => l.Id == awaySummary.Id) is { } away)
        {
            // A vessel that docked into another one (or was carried off by it) and has come back off as itself: KSP gave
            // it back its name and started its MET again.
            log = away;
            recorder = new FlightLogRecorder(log);
            NoteUndocked(recorder, sample.Ut, sample.Ut - sample.MetS);
        }
        else
        {
            log ??= launched.Count == 1 ? launched[0] : null;
            if (log is null) return null;
            recorder = new FlightLogRecorder(log);
            var gap = sample.Ut - log.LastUt;
            if (gap >= NotableGapS)
            {
                var (kind, why) = _leftBecause.GetValueOrDefault(log.Id, (FlightLogEventKind.Disconnected, "Flight disconnected"));
                recorder.Note(kind == FlightLogEventKind.Disconnected ? FlightLogEventKind.Reconnected : FlightLogEventKind.Returned,
                    $"{why}: {FlightLogs.Duration(gap)} not recorded", gap);
            }
        }
        _leftBecause.Remove(log.Id);
        recorder.Add(sample);
        return recorder;
    }

    /// <summary>
    /// Notes on an away flight that its vessel is its own again, as the vessel launched (came off) at
    /// <paramref name="newLaunchUt"/>, and on the vessel it was docked into that it undocked.
    /// </summary>
    private void NoteUndocked(FlightLogRecorder recorder, double ut, double newLaunchUt)
    {
        var log = recorder.Log;
        var docked = log.DockedIn;
        var since = log.Events.Last(e => e.Joined);
        var partner = docked?.Partner ?? since.Partner;
        var name = FlightLogs.AwayName(log.VesselName, log.Events);
        var after = docked is null ? "" : $" after {FlightLogs.Duration(ut - docked.Ut)} docked";
        recorder.NoteDocking(ut, FlightLogEventKind.Undocked, (partner is null ? "Undocked" : $"Undocked from {partner}") + after,
            partner, since.PartnerLogId, joined: true, asName: name, newLaunchUt: newLaunchUt);
        if (docked?.PartnerLogId is { } host)
            NoteOn(host, null, r =>
            {
                if (!r.Log.Events.Any(e => e.Kind == FlightLogEventKind.Undocked && e.Partner == name && e.Ut >= docked.Ut))
                    r.NoteDocking(ut, FlightLogEventKind.Undocked, $"{name} undocked", name, recorder.Id, joined: false);
            });
    }

    /// <summary>Checks the next listing of vessels for a docking or undocking hinted at by the flown vessel's readings.</summary>
    private void Check(CheckKind kind, double ut, FlightLogRecorder recorder)
    {
        if (_vessels is not { } before) return; // nothing to compare with yet
        _checks.Add(new DockCheck(kind, ut, _recordingId, recorder.Id, _telemetry?.VesselName ?? recorder.VesselName, before));
        _link.ReadVesselsSoon();
    }

    private void OnVesselsRead(LiveGame game)
    {
        if (game.Vessels is not { } after || ReferenceEquals(after, _vessels)) return; // not read again since
        _vessels = after;
        if (!SaveConfirmed) { _checks.Clear(); return; }
        foreach (var check in _checks.Where(c => c.Ut <= game.Ut + FlightLogs.SameLaunchS).ToList())
        {
            _checks.Remove(check);
            _ = ResolveAsync(check, after);
        }
    }

    private async Task ResolveAsync(DockCheck check, IReadOnlyList<LiveVessel> after)
    {
        try
        {
            if (check.Kind == CheckKind.Split) await ResolveSplitAsync(check, after);
            else ResolveDocking(check, after);
        }
        catch (Exception)
        {
            // The link dropped meanwhile: the save's docking ports still tell (FlightLogs.EndedIn).
        }
    }

    /// <summary>Vessels gone into the one KSP kept: their flights wait docked in it, and it notes who docked.</summary>
    private void ResolveDocking(DockCheck check, IReadOnlyList<LiveVessel> after)
    {
        // Joined: the flown vessel was kept and another went into it. Left: the flown vessel went into the one flown now.
        var hostId = check.Kind == CheckKind.Joined ? check.VesselId : after.FirstOrDefault(v => v.IsActive)?.KrpcId;
        if (hostId is not { } id || after.FirstOrDefault(v => v.KrpcId == id) is not { } host) return;
        var gone = FlightDocking.Gone(check.Before, after, id);
        if (check.Kind == CheckKind.Left) gone = gone.Where(v => v.KrpcId == check.VesselId).ToList();
        if (gone.Count == 0) return;

        var summaries = _logs.GetAll();
        var hostLog = _recorder is { } r && _recordingId == id ? r.Id
            : FlightDocking.LogOf(host, summaries, _vesselOf, NameNow)?.Id;
        foreach (var vessel in gone)
        {
            long? goneId = check.Kind == CheckKind.Left ? check.LogId : FlightDocking.LogOf(vessel, summaries, _vesselOf, NameNow)?.Id;
            if (goneId == hostLog) goneId = null;
            if (goneId is { } logId && _logs.Get(logId) is { } goneLog && !goneLog.Away)
            {
                var recorder = new FlightLogRecorder(goneLog);
                recorder.NoteDocking(check.Ut, FlightLogEventKind.Docked, $"Docked to {host.Name}", host.Name, hostLog, joined: true, asName: vessel.Name);
                _logs.Save(recorder.Log);
                _vesselOf.Remove(logId);
                _leftBecause.Remove(logId);
            }
            var text = vessel.Type == "SpaceObject" ? $"Grabbed {vessel.Name}" : $"{vessel.Name} docked";
            NoteOn(hostLog, id, h => h.NoteDocking(check.Ut, FlightLogEventKind.Docked, text, vessel.Name, goneId, joined: false));
        }
        LogsChanged?.Invoke();
    }

    /// <summary>
    /// Vessels come off the flown one: a flight docked into it carries on with the vessel of its name. When KSP gave the
    /// flown vessel itself a docked flight's name, that flight carries on with it, and this flight with the vessel that
    /// came off.
    /// </summary>
    private async Task ResolveSplitAsync(DockCheck check, IReadOnlyList<LiveVessel> after)
    {
        var appeared = FlightDocking.Appeared(check.Before, after, check.VesselId).Where(v => v.Type != "Debris").ToList();
        var now = after.FirstOrDefault(v => v.KrpcId == check.VesselId);
        var renamed = now is not null && now.Name != check.Name ? now.Name : null;
        var summaries = _logs.GetAll();
        var docked = appeared.Select(v => (Vessel: v, Log: FlightDocking.AwayLogNamed(v.Name, summaries, check.LogId)))
            .Where(p => p.Log is not null).ToList();
        var swapped = renamed is not null ? FlightDocking.AwayLogNamed(renamed, summaries, check.LogId) : null;
        if (docked.Count == 0 && swapped is null) return;

        var ids = appeared.Select(v => v.KrpcId).ToList();
        var launches = ids.Count > 0 ? await _link.LaunchTimesAsync(ids) : new Dictionary<ulong, double>();
        foreach (var (vessel, summary) in docked)
        {
            // Without its launch time (gone again already) it's picked up by name when it's flown.
            if (!launches.TryGetValue(vessel.KrpcId, out var launch) || _logs.Get(summary!.Id) is not { Away: true } log) continue;
            var recorder = new FlightLogRecorder(log);
            NoteUndocked(recorder, check.Ut, launch);
            _logs.Save(recorder.Log);
        }

        if (swapped is not null && _recorder is { } flown && flown.Id == check.LogId && _recordingId == check.VesselId)
        {
            // The flown vessel is now the docked flight's (KSP named it so): this flight goes with the vessel that came off
            // under its old name, found by that launch time, or by name when flown next. The streams reopen, and the
            // docked flight is picked up again on this vessel.
            var off = appeared.FirstOrDefault(v => v.Name == check.Name);
            double? offLaunch = off is not null && launches.TryGetValue(off.KrpcId, out var launch) ? launch : null;
            flown.NoteDocking(check.Ut, FlightLogEventKind.Undocked, $"Undocked from {renamed}: carried on as a new vessel", renamed,
                swapped.Id, joined: true, asName: check.Name, newLaunchUt: offLaunch);
            _vesselOf.Remove(flown.Id);
            Leave(FlightLogEventKind.SwitchedAway, "Undocked");
        }
        LogsChanged?.Invoke();
    }

    /// <summary>
    /// Adds a note to a log: the one being recorded, else the stored one, else (no log yet) the next one recorded for
    /// vessel <paramref name="vesselId"/>.
    /// </summary>
    private void NoteOn(long? logId, ulong? vesselId, Action<FlightLogRecorder> note)
    {
        if (_recorder is { } r && (r.Id == logId || vesselId is not null && _recordingId == vesselId))
        {
            note(r);
            Store();
        }
        else if (logId is { } id && _logs.Get(id) is { Outcome: FlightLogOutcome.InFlight } log)
        {
            var recorder = new FlightLogRecorder(log);
            note(recorder);
            _logs.Save(recorder.Log);
            LogsChanged?.Invoke();
        }
        else if (vesselId is { } v)
        {
            if (!_notesFor.TryGetValue(v, out var list)) _notesFor[v] = list = [];
            list.Add(note);
        }
    }

    /// <summary>The name a log's vessel goes by in the save now (it may have been renamed since launch).</summary>
    private string? NameNow(FlightLogSummary log) => _sync.Save is { } save ? FlightLogs.VesselOf(log, save)?.Name : null;

    /// <summary>Stops recording the current flight (it stays open, to be picked up again) and closes the streams.</summary>
    private void Leave(FlightLogEventKind kind, string why, bool store = true)
    {
        if (_recorder is { } recorder)
        {
            recorder.Flush();
            if (store) Store();
            _leftBecause[recorder.Id] = (kind, why);
            _lastLogId = recorder.Id;
            _recorder = null;
        }
        _lastRead = null;
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
        var outcomes = _logs.GetAll().ToDictionary(l => l.Id, l => l.Outcome);
        FlightLogOutcome? OutcomeOf(long id) => outcomes.TryGetValue(id, out var o) && o != FlightLogOutcome.InFlight ? o : null;
        // Flights docked into another vessel last: they go the way that one went.
        foreach (var log in _logs.GetInFlight().OrderBy(l => l.DockedIn is null ? 0 : 1))
        {
            var flying = _recorder?.Id == log.Id;
            var current = flying ? _recorder!.Log : log;
            if (!flying && file.Ut >= log.LastUt && FlightLogs.DockedUnseen(log, file) is { } host)
            {
                // Docked while the planner wasn't looking: wait docked in the vessel that remembers it.
                var docked = new FlightLogRecorder(log);
                var hostLog = FlightLogs.LogsOf(host, file, _logs.GetAll()).FirstOrDefault(l => l.Id != log.Id)?.Id;
                docked.NoteDocking(log.LastUt, FlightLogEventKind.Docked, $"Docked to {host.Name} (found in the save)", host.Name, hostLog,
                    joined: true, asName: log.VesselName);
                _logs.Save(docked.Log);
                changed = true;
                continue;
            }
            if (FlightLogs.EndedIn(current, file, OutcomeOf) is not { } outcome) continue;
            if (flying && outcome != FlightLogOutcome.Reverted) continue;
            End(log, outcome);
            outcomes[log.Id] = outcome;
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
