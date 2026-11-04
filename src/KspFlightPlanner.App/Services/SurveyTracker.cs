using System.ComponentModel;
using System.Windows;
using KspFlightPlanner.App.ViewModels;
using KspFlightPlanner.Core;
using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;
using KspFlightPlanner.Data;

namespace KspFlightPlanner.App.Services;

/// <summary>
/// Runs the ore survey (docs/SURVEY.md): every time the save changes (KSP wrote it, or the live game moved on) it reads
/// each surveying station, credits the coverage since its last reading, and files reports for biomes that crossed a
/// level. The work runs off the UI thread; a burst of updates is handled as one.
/// </summary>
public sealed class SurveyTracker
{
    private readonly SaveSync _sync;
    private readonly SurveyRepository _repository;
    private readonly KspLink _link;
    private readonly object _gate = new();
    private readonly Dictionary<string, SurveyMap?> _maps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SurveyCoverage> _coverage = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SurveyReading?> _readings = new(StringComparer.Ordinal);
    private readonly HashSet<string> _fetching = new(StringComparer.Ordinal);
    private double _lastUt = double.NaN;
    private bool _busy, _again;

    public SurveyTracker(SaveSync sync, SurveyRepository repository, KspLink link, PlannerEvents events)
    {
        _sync = sync;
        _repository = repository;
        _link = link;
        sync.PropertyChanged += OnSyncChanged;
        events.SaveChanged += Reset;
    }

    /// <summary>Coverage, maps or reports changed (raised on the UI thread).</summary>
    public event Action? Changed;

    /// <summary>New reports were filed (raised on the UI thread), newest first.</summary>
    public event Action<IReadOnlyList<SurveyReportRow>>? ReportsFiled;

    /// <summary>What went wrong last time a map was fetched, if anything.</summary>
    public string? FetchProblem { get; private set; }

    public SurveyMap? Map(string bodyName)
    {
        lock (_gate)
        {
            if (_maps.TryGetValue(bodyName, out var map)) return map;
            return _maps[bodyName] = _repository.GetMap(bodyName);
        }
    }

    /// <summary>A copy of the body's coverage, safe to read while the tracker works.</summary>
    public SurveyCoverage Coverage(string bodyName)
    {
        lock (_gate)
        {
            var c = CoverageLocked(bodyName, Map(bodyName)?.StepDeg ?? KspLink.SurveyStepDeg);
            return new SurveyCoverage(c.StepDeg, (float[])c.Rough.Clone(), (float[])c.Estimate.Clone());
        }
    }

    public IReadOnlyList<SurveyReportRow> Reports() => _repository.Reports();

    /// <summary>The body's biomes as the survey knows them, or empty without a map.</summary>
    public IReadOnlyList<BiomeSurvey> Biomes(string bodyName) =>
        Map(bodyName) is { } map && _sync.Save is { } save ? SurveyReports.Build(map, Coverage(bodyName), bodyName, save.AnalysedBiomes) : [];

    /// <summary>The engineer's top spots to try on the body, best first (docs/SURVEY.md).</summary>
    public IReadOnlyList<SurveyCandidate> Candidates(string bodyName) =>
        Map(bodyName) is { } map && _sync.Save is { } save
            ? SurveyReports.Candidates(map, Coverage(bodyName), SurveyReports.Build(map, Coverage(bodyName), bodyName, save.AnalysedBiomes))
            : [];

    /// <summary>The engineer's spots as spots to land at ("Ore survey"), numbered as in the pane and the game's waypoints.</summary>
    public IReadOnlyList<LandingTarget> Spots(string bodyName) =>
        Candidates(bodyName).Select((c, i) => new LandingTarget(SpotName(i, c), "Ore survey", c.LatitudeDeg, c.LongitudeDeg)).ToList();

    /// <summary>"Ore 1: Greater Flats ≈ 7%" (the game's waypoints use the same names).</summary>
    public static string SpotName(int index, SurveyCandidate c) => $"{KspLink.SurveyWaypointPrefix}{index + 1}: {c.Biome} {c.Value}";

    /// <summary>Fetches the body's ore map from the game through the add-on, and starts surveying it.</summary>
    public async Task<string?> FetchMapAsync(string bodyName)
    {
        lock (_gate)
        {
            if (!_fetching.Add(bodyName)) return null;
        }
        try
        {
            if (!_link.IsConnected) return FetchProblem = "Connect to KSP first (KSP › Connect to KSP).";
            if (await _link.SurveyAddOnVersionAsync() is null)
                return FetchProblem = "The planner's add-on isn't in KSP: install mod/KspFlightPlanner.Survey into GameData and restart the game.";
            var map = await _link.FetchSurveyMapAsync(bodyName);
            _repository.SaveMap(map);
            lock (_gate) _maps[bodyName] = map;
            FetchProblem = null;
            RaiseChanged();
            Schedule();
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or KRPC.Client.RPCException or ArgumentException)
        {
            return FetchProblem = $"Couldn't fetch {bodyName}'s ore map: {ex.Message}";
        }
        finally
        {
            lock (_gate) _fetching.Remove(bodyName);
        }
    }

    private void OnSyncChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SaveSync.Save)) Schedule();
    }

    private void Reset()
    {
        lock (_gate)
        {
            _maps.Clear();
            _coverage.Clear();
            _readings.Clear();
            _lastUt = double.NaN;
        }
        RaiseChanged();
        Schedule();
    }

    /// <summary>Works through the latest save on a worker thread; updates arriving meanwhile are taken in one go after.</summary>
    private void Schedule()
    {
        if (_sync.Save is not { } save) return;
        var now = _sync.IsLive ? _sync.Now ?? save.Ut : save.Ut;
        lock (_gate)
        {
            if (_busy) { _again = true; return; }
            _busy = true;
        }
        var bodies = _sync.StockBodies();
        _ = Task.Run(() =>
        {
            IReadOnlyList<SurveyReportRow> filed = [];
            var changed = false;
            try { (filed, changed) = Process(save, now, bodies); }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException) { /* tried again next update */ }
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                bool again;
                lock (_gate)
                {
                    _busy = false;
                    again = _again;
                    _again = false;
                }
                if (changed) Changed?.Invoke();
                if (filed.Count > 0)
                {
                    ReportsFiled?.Invoke(filed);
                    _ = TellGameAsync(filed);
                }
                if (again) Schedule();
            });
        });
    }

    private (IReadOnlyList<SurveyReportRow> Filed, bool Changed) Process(KspSave save, double now, IReadOnlyDictionary<string, CelestialBody> bodies)
    {
        var changed = false;
        var touched = new HashSet<string>(StringComparer.Ordinal);
        var readings = new Dictionary<string, SurveyReading>(StringComparer.Ordinal);
        lock (_gate)
        {
            // The game went back in time (a quickload or revert): what was found after that never happened.
            if (double.IsFinite(_lastUt) && now < _lastUt - 1)
            {
                foreach (var body in _repository.CoveredBodies())
                {
                    var c = CoverageLocked(body, Map(body)?.StepDeg ?? KspLink.SurveyStepDeg);
                    c.ForgetAfter(now);
                    _repository.SaveCoverage(body, c);
                }
                _repository.DeleteReportsAfter(now);
                foreach (var id in _readings.Keys.ToList()) _readings[id] = null;
                changed = true;
            }
            _lastUt = now;

            foreach (var vessel in save.Vessels)
            {
                var station = SurveyStation.From(vessel, save);
                var previous = ReadingLocked(vessel.Id);
                if (station is null)
                {
                    // It stopped being a station (left orbit, lost its scanners): don't bridge the gap later.
                    if (previous is { Engineers.Count: > 0 } && now >= previous.Ut)
                        Store(vessel, previous with { Ut = now, Engineers = [] });
                    continue;
                }
                var reading = SurveyReading.Of(station, now, save.ScannedBodies.Contains(station.BodyName));
                if (previous is not null && now < previous.Ut) continue;
                if (previous is not null && Map(station.BodyName) is { } map && bodies.TryGetValue(station.BodyName, out var body))
                {
                    var coverage = CoverageLocked(station.BodyName, map.StepDeg);
                    if (coverage.Credit(previous, reading, map, body.Mu) > 0)
                    {
                        touched.Add(station.BodyName);
                        readings[station.BodyName] = reading;
                    }
                }
                else if (station.IsSurveying && Map(station.BodyName) is null && _link.IsConnected) _ = FetchSoon(station.BodyName);
                if (previous is null || reading != previous) Store(vessel, reading);
                if (station.IsSurveying) readings.TryAdd(station.BodyName, reading);
            }
            foreach (var body in touched) _repository.SaveCoverage(body, _coverage[body]);
        }

        var filed = new List<SurveyReportRow>();
        foreach (var (bodyName, reading) in readings) filed.AddRange(File(bodyName, reading, save, now));
        return (filed.OrderByDescending(r => r.Ut).ToList(), changed || touched.Count > 0 || filed.Count > 0);
    }

    /// <summary>Files a report for every biome that has reached a level it hadn't reported yet.</summary>
    private List<SurveyReportRow> File(string bodyName, SurveyReading reading, KspSave save, double now)
    {
        var filed = new List<SurveyReportRow>();
        if (Map(bodyName) is not { } map) return filed;
        var who = reading.Engineers.Count > 0 ? string.Join(" and ", reading.Engineers) : "The survey";
        foreach (var biome in SurveyReports.Build(map, Coverage(bodyName), bodyName, save.AnalysedBiomes))
        {
            void Add(double ut, SurveyLevel level, string text)
            {
                if (_repository.AddReport(ut, bodyName, biome.Biome, level, text))
                    filed.Add(new SurveyReportRow(0, ut, bodyName, biome.Biome, level, text));
            }
            if (biome.RoughUt is { } rough)
                Add(rough, SurveyLevel.Rough, $"{who} mapped {biome.Biome} from orbit: ore looks {SurveyReports.Word(biome.Estimate).ToLowerInvariant()}.");
            if (biome.EstimateUt is { } estimate)
                Add(estimate, SurveyLevel.Estimate, $"{who} mapped {biome.Biome} with the narrow-band: ore ≈ {Math.Round(biome.Estimate * 100):0}%.");
            if (biome.Analysed)
                Add(now, SurveyLevel.Surveyed, biome.Spots.Count > 0
                    ? $"{biome.Biome} analysed: {SurveyReports.Percent(biome.Spots[0].Abundance)} at best, at {LatLon(biome.Spots[0])}."
                    : $"{biome.Biome} analysed: {SurveyReports.Percent(biome.TrueAverage ?? 0)} on average.");
        }
        return filed;
    }

    private static string LatLon(SurveySpot s) => SurveyReports.LatLon(s.LatitudeDeg, s.LongitudeDeg);

    /// <summary>
    /// Puts new reports on the game's screen: up to three one by one (oldest first), more as one message, so a long warp
    /// or the first map doesn't flood it.
    /// </summary>
    private async Task TellGameAsync(IReadOnlyList<SurveyReportRow> filed)
    {
        if (!_link.IsConnected) return;
        try
        {
            if (filed.Count <= 3)
                foreach (var r in filed.Reverse()) await _link.ShowMessageAsync($"Ore survey: {r.Text}");
            else
                await _link.ShowMessageAsync($"Ore survey: {filed.Count} new reports on {string.Join(", ", filed.Select(r => r.BodyName).Distinct())}. Latest: {filed[0].Text}", 12);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KRPC.Client.RPCException) { /* the pane still has them */ }
    }

    private async Task FetchSoon(string bodyName)
    {
        await Task.Yield();
        await Application.Current.Dispatcher.InvokeAsync(() => FetchMapAsync(bodyName)).Task.Unwrap();
    }

    private SurveyCoverage CoverageLocked(string bodyName, double stepDeg)
    {
        if (!_coverage.TryGetValue(bodyName, out var coverage) || coverage.StepDeg != stepDeg)
            _coverage[bodyName] = coverage = _repository.GetCoverage(bodyName, stepDeg);
        return coverage;
    }

    private SurveyReading? ReadingLocked(string vesselId)
    {
        if (_readings.TryGetValue(vesselId, out var reading)) return reading;
        return _readings[vesselId] = _repository.GetReading(vesselId);
    }

    private void Store(SavedVessel vessel, SurveyReading reading)
    {
        _readings[vessel.Id] = reading;
        _repository.SaveReading(vessel.Id, vessel.Name, reading);
    }

    private void RaiseChanged() => Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());
}
