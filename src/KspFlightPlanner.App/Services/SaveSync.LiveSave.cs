using System.IO;
using System.Windows.Threading;
using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.App.Services;

/// <summary>
/// Which save the running game is playing (kRPC can't name it). Checked on connecting and on every scene change by
/// looking for the game's vessels in each save, and whenever KSP writes a save: it writes the one it's playing at every
/// scene change, so a save written with the game's mode and time is the one (docs/SAVE-AND-LIVE-GAME.md §1). While the game plays
/// another save, its live time and orbits aren't laid over the chosen one, and the flight log doesn't record.
/// </summary>
public sealed partial class SaveSync
{
    private readonly DispatcherTimer _savesDebounce;
    private readonly HashSet<string> _written = new(StringComparer.OrdinalIgnoreCase); // saves KSP wrote, to check
    private readonly Dictionary<string, (DateTime Write, KspSave Save)> _saveCache = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _savesWatcher;
    private SaveVerdict _verdict = SaveVerdict.Unknown;
    private SaveVerdict? _dismissed; // the mismatch the player said "not now" to
    private bool _liveSaveChecked;
    private int _generation; // bumped on starting over: checks from before are dropped
    private int _vesselCheck; // a newer vessel check supersedes an older one

    /// <summary>Raised (on the UI thread) when the game turns out to be playing another save, unless the player waved it off.</summary>
    public event Action? OtherSaveFound;

    /// <summary>Connected, and the game's save has been checked (or there was nothing to check it by).</summary>
    public bool LiveSaveChecked { get => _liveSaveChecked; private set => SetProperty(ref _liveSaveChecked, value); }

    /// <summary>The game is playing another save than the chosen one.</summary>
    public bool PlayingOtherSave => _verdict.Kind is SaveVerdictKind.Other or SaveVerdictKind.NotChosen;

    /// <summary>The other save the game is playing, when it could be told; null if not playing another or unknown.</summary>
    public string? PlayingSave => _verdict.Kind == SaveVerdictKind.Other ? _verdict.Save : null;

    /// <summary>One line on the mismatch for the status bar; empty when there's none.</summary>
    public string OtherSaveText => !PlayingOtherSave ? ""
        : PlayingSave is { } other ? $"KSP is playing {other}, not {SaveName}"
        : $"KSP isn't playing {SaveName}";

    /// <summary>The player chose to carry on as it is: don't raise <see cref="OtherSaveFound"/> again for this mismatch.</summary>
    public void DismissOtherSave() => _dismissed = _verdict;

    private void OnLinkChanged(string? property)
    {
        if (property == nameof(KspLink.IsConnected)) RestartLiveSaveCheck();
        else if (property == nameof(KspLink.Scene) && _link.IsConnected && _link.Scene is not null) _ = CheckVesselsAsync();
    }

    /// <summary>Starts over: on connecting or disconnecting, and when the settings change (another save, another install).</summary>
    private void RestartLiveSaveCheck()
    {
        StopWatchingSaves();
        _generation++;
        _dismissed = null;
        LiveSaveChecked = false;
        SetVerdict(SaveVerdict.Unknown);
        if (!_link.IsConnected || _settings.KspSaveName is null || _settings.KspInstallPath is not { } install) return;

        var dir = Path.Combine(install, "saves");
        if (Directory.Exists(dir))
        {
            _savesWatcher = new FileSystemWatcher(dir, KspSaveReader.PersistentFileName)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            FileSystemEventHandler written = (_, e) => SaveWritten(e.FullPath);
            _savesWatcher.Changed += written;
            _savesWatcher.Created += written;
            _savesWatcher.Renamed += (_, e) => SaveWritten(e.FullPath);
            _savesWatcher.EnableRaisingEvents = true;
        }
        if (_link.Scene is not null) _ = CheckVesselsAsync(); // else the first read's scene change starts it
    }

    private void StopWatchingSaves()
    {
        _savesDebounce.Stop();
        _savesWatcher?.Dispose();
        _savesWatcher = null;
        lock (_written) _written.Clear();
    }

    /// <summary>A watcher event (worker thread): note which save, and check once KSP has finished writing.</summary>
    private void SaveWritten(string path)
    {
        if (Path.GetFileName(Path.GetDirectoryName(path)) is not { Length: > 0 } save) return;
        lock (_written) _written.Add(save);
        _savesDebounce.Dispatcher.BeginInvoke(() =>
        {
            _savesDebounce.Stop();
            _savesDebounce.Start();
        });
    }

    /// <summary>Looks for the game's vessels in every save.</summary>
    private async Task CheckVesselsAsync()
    {
        var (generation, check) = (_generation, ++_vesselCheck);
        var chosen = _settings.KspSaveName;
        var install = _settings.KspInstallPath;
        if (chosen is null || install is null) return;
        try
        {
            var game = await _link.ReadIdentityAsync(withVessels: true);
            var saves = await Task.Run(() => ReadSaves(install));
            if (generation != _generation || check != _vesselCheck) return;
            var verdict = LiveSaveMatch.Judge(chosen, saves, game);
            if (verdict.Kind != SaveVerdictKind.Unknown) SetVerdict(verdict);
        }
        catch (Exception)
        {
            // Between scenes (kRPC has no game to answer for), or the link dropped: the next scene or save write tells.
        }
        if (generation == _generation && _link.IsConnected) LiveSaveChecked = true;
    }

    /// <summary>Checks the saves KSP just wrote: one written with the game's mode and time is the one it's playing.</summary>
    private async Task CheckWrittenAsync()
    {
        List<string> written;
        lock (_written)
        {
            written = [.. _written];
            _written.Clear();
        }
        var chosen = _settings.KspSaveName;
        if (!_link.IsConnected || chosen is null || _settings.KspInstallPath is not { } install || written.Count == 0) return;
        var generation = _generation;
        try
        {
            var files = await Task.Run(() => written.Select(name => (name, ReadSave(install, name))).ToList());
            var game = await _link.ReadIdentityAsync(withVessels: false);
            if (generation != _generation) return;
            foreach (var (name, file) in files)
            {
                if (file is null || !LiveSaveMatch.WrittenByGame(file, game)) continue;
                SetVerdict(string.Equals(name, chosen, StringComparison.OrdinalIgnoreCase)
                    ? new SaveVerdict(SaveVerdictKind.Chosen, chosen)
                    : new SaveVerdict(SaveVerdictKind.Other, name));
                LiveSaveChecked = true;
                return;
            }
        }
        catch (Exception)
        {
            // Between scenes kRPC can't answer: try again shortly (dropped if the link went or the check started over).
            if (!_link.IsConnected || generation != _generation) return;
            lock (_written) _written.UnionWith(written);
            _savesDebounce.Start();
        }
    }

    private void SetVerdict(SaveVerdict verdict)
    {
        if (verdict == _verdict) return;
        _verdict = verdict;
        OnPropertyChanged(nameof(PlayingOtherSave));
        OnPropertyChanged(nameof(PlayingSave));
        OnPropertyChanged(nameof(OtherSaveText));
        ApplyLive(fileChanged: true); // lay the live game over the save again, or take it off
        if (PlayingOtherSave && _dismissed != verdict) OtherSaveFound?.Invoke();
    }

    /// <summary>Every save of the install, by folder name (read again only when KSP has written it). Runs on a worker thread.</summary>
    private Dictionary<string, KspSave> ReadSaves(string install)
    {
        var saves = new Dictionary<string, KspSave>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in KspSaveReader.FindSaves(install))
            if (ReadSave(install, folder.Name) is { } save) saves[folder.Name] = save;
        return saves;
    }

    private KspSave? ReadSave(string install, string name)
    {
        var path = KspSaveReader.PersistentPath(install, name);
        try
        {
            var write = File.GetLastWriteTimeUtc(path);
            lock (_saveCache)
                if (_saveCache.TryGetValue(name, out var cached) && cached.Write == write) return cached.Save;
            var save = KspSaveReader.Read(path);
            lock (_saveCache) _saveCache[name] = (write, save);
            return save;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }
}
