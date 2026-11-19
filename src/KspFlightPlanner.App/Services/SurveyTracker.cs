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
    private readonly Dictionary<string, SurveyCrewReading?> _crew = new(StringComparer.Ordinal);
    private double _lastUt = double.NaN;
    private bool _busy, _again;
    private bool _anomaliesTried;

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

    /// <summary>The Survey Engineers on duty as last read (live from the game, or from the save), with the craft each can watch.</summary>
    public IReadOnlyList<SurveyWorker> Workers { get; private set; } = [];

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
                return FetchProblem = "The planner's add-on isn't in KSP: install mod/KspFlightPlanner.AddOns into GameData and restart the game.";
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
            _crew.Clear();
            _lastUt = double.NaN;
            _anomaliesTried = false;
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
        var live = _sync.IsLive && _link.IsConnected;
        _ = Task.Run(async () =>
        {
            IReadOnlyList<SurveyReportRow> filed = [];
            IReadOnlyList<SurveyWork> work = [];
            var changed = false;
            // Live, the add-on is asked who's surveying now (links change as craft go behind a moon); otherwise the save says.
            var workers = save.SurveyWorkers;
            if (live)
            {
                try { workers = await _link.SurveyWorkersAsync(); }
                catch (Exception ex) when (ex is InvalidOperationException or KRPC.Client.RPCException) { /* an older add-on: the save's */ }
            }
            Workers = workers;
            try { (filed, work, changed) = Process(save, now, bodies, workers); }
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
                _ = TellGameWorkAsync(work, save);
                _ = FetchAnomaliesSoon();
                if (again) Schedule();
            });
        });
    }

    private (IReadOnlyList<SurveyReportRow> Filed, IReadOnlyList<SurveyWork> Work, bool Changed) Process(
        KspSave save, double now, IReadOnlyDictionary<string, CelestialBody> bodies, IReadOnlyList<SurveyWorker> workers)
    {
        var changed = false;
        var touched = new HashSet<string>(StringComparer.Ordinal);
        var readings = new Dictionary<string, SurveyReading>(StringComparer.Ordinal);
        var watches = new Dictionary<string, List<SurveyWatch>>(StringComparer.Ordinal);
        var surveyors = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
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
                _repository.DeleteDoneAfter(now);
                foreach (var id in _readings.Keys.ToList()) _readings[id] = null;
                foreach (var id in _crew.Keys.ToList()) _crew[id] = null;
                changed = true;
            }
            _lastUt = now;

            // With Survey Engineers on (the add-on's setting), only engineers on duty survey, each watching one craft at a
            // time (docs/KERBIN-RESOURCES.md "Survey Engineer at work"). With it off, any engineer aboard a station does, as before.
            if (save.Roles.SurveyEngineers)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var worker in workers)
                {
                    seen.Add(worker.Kerbal);
                    var reading = SurveyCrewReading.Of(worker, save, now);
                    var previous = CrewLocked(worker.Kerbal);
                    if (previous is not null && now < previous.Ut) continue;
                    if (previous is not null && Map(worker.BodyName) is { } map && bodies.TryGetValue(worker.BodyName, out var body))
                    {
                        var coverage = CoverageLocked(worker.BodyName, map.StepDeg);
                        if (!watches.TryGetValue(worker.BodyName, out var list)) watches[worker.BodyName] = list = [];
                        if (coverage.CreditCrew(previous, reading, map, body.Mu, list) > 0) touched.Add(worker.BodyName);
                    }
                    else if (reading.Crafts.Count > 0 && Map(worker.BodyName) is null && _link.IsConnected) _ = FetchSoon(worker.BodyName);
                    if (!surveyors.TryGetValue(worker.BodyName, out var names)) surveyors[worker.BodyName] = names = new(StringComparer.Ordinal);
                    names.Add(worker.Kerbal);
                    if (previous is null || !reading.SameAs(previous)) StoreCrew(reading);
                    else _crew[worker.Kerbal] = reading;
                }
                // Off duty since the last reading: the gap isn't bridged when they come back.
                foreach (var kerbal in _crew.Where(kv => kv.Value is { Crafts.Count: > 0 } && !seen.Contains(kv.Key)).Select(kv => kv.Key).ToList())
                    StoreCrew(_crew[kerbal]! with { Ut = now, Crafts = [] });
                foreach (var body in touched) _repository.SaveCoverage(body, _coverage[body]);
            }
            else
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
        var work = new List<SurveyWork>();
        foreach (var (bodyName, reading) in readings) filed.AddRange(File(bodyName, reading, save, now));
        foreach (var bodyName in touched.Where(_ => save.Roles.SurveyEngineers))
            filed.AddRange(FileCrew(bodyName, watches.GetValueOrDefault(bodyName) ?? [], surveyors.GetValueOrDefault(bodyName) ?? [], save, now, work));
        foreach (var bodyName in touched.Union(readings.Keys))
        {
            if (bodies.TryGetValue(bodyName, out var body))
                filed.AddRange(FileLandmarks(bodyName, body, watches.GetValueOrDefault(bodyName) ?? [], save, work));
            filed.AddRange(CheckDone(bodyName, surveyors.GetValueOrDefault(bodyName), readings.GetValueOrDefault(bodyName), save, now));
        }
        filed.AddRange(FileVisitsAndReports(save));
        return (filed.OrderByDescending(r => r.Ut).ToList(), work, changed || touched.Count > 0 || filed.Count > 0);
    }

    /// <summary>A survey find to put on a kerbal's work log in the game (Kerbin Resources).</summary>
    public sealed record SurveyWork(string Kerbal, string Kind, string Body, string Vessel, string Detail, double Ut);

    /// <summary>Who was watching which craft at a time on a body: the last switch before it.</summary>
    private static SurveyWatch? WatchAt(IReadOnlyList<SurveyWatch> watches, double ut) =>
        watches.Where(w => w.FromUt <= ut + 1).OrderBy(w => w.FromUt).LastOrDefault() ?? watches.OrderBy(w => w.FromUt).FirstOrDefault();

    /// <summary>Reports for biomes that crossed a level, naming the engineer and the craft whose scanner did it.</summary>
    private List<SurveyReportRow> FileCrew(string bodyName, IReadOnlyList<SurveyWatch> watches, IReadOnlySet<string> surveyors, KspSave save, double now, List<SurveyWork> work)
    {
        var filed = new List<SurveyReportRow>();
        if (Map(bodyName) is not { } map) return filed;
        foreach (var biome in SurveyReports.Build(map, Coverage(bodyName), bodyName, save.AnalysedBiomes))
        {
            void Add(double ut, SurveyLevel level, Func<string, string, string> text, string scanner)
            {
                var watch = WatchAt(watches, ut);
                var who = watch?.Kerbal ?? (surveyors.Count > 0 ? string.Join(" and ", surveyors) : "The survey");
                var craft = watch?.Craft ?? "";
                var line = text(who, craft);
                if (!_repository.AddReport(ut, bodyName, biome.Biome, level, line)) return;
                filed.Add(new SurveyReportRow(0, ut, bodyName, biome.Biome, level, line));
                if (watch is not null) work.Add(new SurveyWork(watch.Kerbal, "survey", bodyName, craft, $"{biome.Biome}, {scanner}", ut));
            }
            if (biome.RoughUt is { } rough)
                Add(rough, SurveyLevel.Rough, (who, craft) => $"{who} mapped {biome.Biome} from orbit{(craft.Length > 0 ? $" with {craft}'s survey scanner" : "")}: ore looks {SurveyReports.Word(biome.Estimate).ToLowerInvariant()}.", "survey scanner");
            if (biome.EstimateUt is { } estimate)
                Add(estimate, SurveyLevel.Estimate, (who, craft) => $"{who} mapped {biome.Biome} with {(craft.Length > 0 ? craft + "'s" : "the")} narrow-band: ore ≈ {Math.Round(biome.Estimate * 100):0}%.", "narrow-band");
            if (biome.Analysed && _repository.AddReport(now, bodyName, biome.Biome, SurveyLevel.Surveyed, Analysed(biome)))
                filed.Add(new SurveyReportRow(0, now, bodyName, biome.Biome, SurveyLevel.Surveyed, Analysed(biome)));
        }
        return filed;
    }

    private static string Analysed(BiomeSurvey biome) => biome.Spots.Count > 0
        ? $"{biome.Biome} analysed: {SurveyReports.Percent(biome.Spots[0].Abundance)} at best, at {LatLon(biome.Spots[0])}."
        : $"{biome.Biome} analysed: {SurveyReports.Percent(biome.TrueAverage ?? 0)} on average.";

    /// <summary>
    /// A report for each landmark the narrow-band has newly covered (docs/SURVEY.md "Landmarks are found by the survey"):
    /// an anomaly as something odd at a spot, a natural feature by name. Launch sites aren't reported.
    /// </summary>
    private List<SurveyReportRow> FileLandmarks(string bodyName, CelestialBody body, IReadOnlyList<SurveyWatch> watches, KspSave save, List<SurveyWork> work)
    {
        var filed = new List<SurveyReportRow>();
        if (Map(bodyName) is null) return filed;
        var coverage = Coverage(bodyName);
        var anomalies = _repository.Anomalies().Where(a => a.BodyName == bodyName).ToList();
        var spots = body.Landmarks.Where(m => m.Kind == LandmarkSurvey.Feature || m.Kind == LandmarkSurvey.Anomaly && anomalies.Count == 0)
            .Select(m => (Key: m.Name, m.Kind, Name: m.Name, m.LatitudeDeg, m.LongitudeDeg))
            .Concat(anomalies.Select(a => (Key: a.Name, Kind: LandmarkSurvey.Anomaly, Name: a.DisplayName, a.LatitudeDeg, a.LongitudeDeg)));
        foreach (var spot in spots)
        {
            if (LandmarkSurvey.CoveredAt(coverage, spot.LatitudeDeg, spot.LongitudeDeg) is not { } ut) continue;
            var watch = WatchAt(watches, ut);
            var who = watch?.Kerbal ?? "The survey";
            var text = spot.Kind == LandmarkSurvey.Anomaly
                ? $"{who} spotted something odd on {bodyName} at {SurveyReports.LatLon(spot.LatitudeDeg, spot.LongitudeDeg)}."
                : $"{who} found {spot.Name} on {bodyName}.";
            if (!_repository.AddReport(ut, bodyName, spot.Key, SurveyLevel.Spotted, text)) continue;
            filed.Add(new SurveyReportRow(0, ut, bodyName, spot.Key, SurveyLevel.Spotted, text));
            if (watch is not null) work.Add(new SurveyWork(watch.Kerbal, "spotted", bodyName, watch.Craft, spot.Kind == LandmarkSurvey.Anomaly ? "Unknown object" : spot.Name, ut));
        }
        return filed;
    }

    /// <summary>
    /// The body done (docs/SURVEY.md "When a body is done"): noted once, with who surveyed it, and the game told so they
    /// write the report (<see cref="TellGameWorkAsync"/>).
    /// </summary>
    private List<SurveyReportRow> CheckDone(string bodyName, IReadOnlySet<string>? surveyors, SurveyReading? stationReading, KspSave save, double now)
    {
        var filed = new List<SurveyReportRow>();
        if (Map(bodyName) is not { } map) return filed;
        var coverage = Coverage(bodyName);
        var biomes = SurveyReports.Build(map, coverage, bodyName, save.AnalysedBiomes);
        if (!SurveyReports.IsDone(coverage, biomes, save.Roles.DonePercent)) return filed;
        var writers = surveyors?.ToList() ?? stationReading?.Engineers.ToList() ?? [];
        if (!_repository.AddDone(bodyName, now, writers)) return filed;
        var text = $"{bodyName} is mapped ({SurveyReports.AreaCovered(coverage, SurveyLevel.Estimate):P0} of it, every biome reported). " +
                   (writers.Count > 0 ? $"{string.Join(" and ", writers)} start writing the survey report." : "Someone has to write the survey report.");
        if (_repository.AddReport(now, bodyName, "", SurveyLevel.Done, text))
            filed.Add(new SurveyReportRow(0, now, bodyName, "", SurveyLevel.Done, text));
        return filed;
    }

    /// <summary>Anomalies visited (identified) and final reports filed, as the save tells it.</summary>
    private List<SurveyReportRow> FileVisitsAndReports(KspSave save)
    {
        var filed = new List<SurveyReportRow>();
        var anomalies = _repository.Anomalies();
        foreach (var v in save.AnomalyVisits)
        {
            var a = anomalies.FirstOrDefault(x => x.BodyName == v.BodyName && x.Name == v.Name);
            var text = $"{v.Vessel} identified {a?.DisplayName ?? v.Name} on {v.BodyName}" + (a is null ? "." : $", at {SurveyReports.LatLon(a.LatitudeDeg, a.LongitudeDeg)}.");
            if (_repository.AddReport(v.Ut, v.BodyName, v.Name, SurveyLevel.Identified, text))
                filed.Add(new SurveyReportRow(0, v.Ut, v.BodyName, v.Name, SurveyLevel.Identified, text));
        }
        foreach (var job in save.SurveyReportJobs.Where(j => j.Filed))
        {
            var text = $"{(job.FiledBy.Length > 0 ? job.FiledBy : "The survey team")} filed the {job.BodyName} survey report.";
            if (_repository.AddReport(job.FiledUt!.Value, job.BodyName, "", SurveyLevel.Filed, text))
                filed.Add(new SurveyReportRow(0, job.FiledUt.Value, job.BodyName, "", SurveyLevel.Filed, text));
        }
        return filed;
    }

    /// <summary>The finished report for a body, if its surveyors have filed it (the save's add-on records).</summary>
    public SurveyReportJob? ReportJob(string bodyName) => _sync.Save?.SurveyReportJobs.FirstOrDefault(j => j.BodyName == bodyName);

    /// <summary>The body is done by the planner's count (noted, whether or not the game has been told).</summary>
    public bool IsDone(string bodyName) => _repository.DoneBodies().Any(d => d.Body == bodyName);

    /// <summary>
    /// Tells the game what the planner worked out: bodies done (so their surveyors write the report), and finds for the
    /// engineers' work logs. Only while connected; a body not yet told is told the next time.
    /// </summary>
    private async Task TellGameWorkAsync(IReadOnlyList<SurveyWork> work, KspSave save)
    {
        if (!_link.IsConnected) return;
        try
        {
            if (await _link.RolesAddOnVersionAsync() is null) return;
            foreach (var w in work) await _link.LogSurveyAsync(w.Kerbal, w.Kind, w.Body, w.Vessel, w.Detail, w.Ut);
            foreach (var done in _repository.DoneBodies().Where(d => !d.Told))
            {
                if (save.SurveyReportJobs.Any(j => j.BodyName == done.Body)) { _repository.MarkDoneTold(done.Body); continue; }
                var answer = await _link.SurveyDoneAsync(done.Body, done.Ut, done.Writers);
                if (answer is "started" or "already") _repository.MarkDoneTold(done.Body);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or KRPC.Client.RPCException) { /* tried again next update */ }
    }

    /// <summary>The game's anomaly list, fetched once per save while connected (landmarks are found by the survey).</summary>
    private async Task FetchAnomaliesSoon()
    {
        lock (_gate)
        {
            if (_anomaliesTried || !_link.IsConnected) return;
            _anomaliesTried = true;
        }
        try
        {
            if (_repository.Anomalies().Count > 0 || await _link.RolesAddOnVersionAsync() is null) return;
            var anomalies = await _link.FetchAnomaliesAsync();
            if (anomalies.Count == 0) return;
            _repository.SaveAnomalies(anomalies);
            RaiseChanged();
        }
        catch (Exception ex) when (ex is InvalidOperationException or KRPC.Client.RPCException) { lock (_gate) _anomaliesTried = false; }
    }

    /// <summary>The game's anomalies for this save (empty until fetched).</summary>
    public IReadOnlyList<SurveyAnomaly> Anomalies() => _repository.Anomalies();

    /// <summary>
    /// A body's landmarks as the planner shows them now: hidden until the narrow-band covers them, anomalies "Unknown object"
    /// until visited (docs/SURVEY.md). With the save's setting off, all of them, named.
    /// </summary>
    public IReadOnlyList<LandmarkView> Landmarks(CelestialBody body)
    {
        var save = _sync.Save;
        var hide = save?.Roles.HideLandmarks ?? true;
        return LandmarkSurvey.For(body.Name, body.Landmarks, _repository.Anomalies(), Map(body.Name) is null ? null : Coverage(body.Name),
            save?.AnomalyVisits ?? [], hide);
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

    private SurveyCrewReading? CrewLocked(string kerbal)
    {
        if (_crew.TryGetValue(kerbal, out var reading)) return reading;
        return _crew[kerbal] = _repository.GetCrewReading(kerbal);
    }

    private void StoreCrew(SurveyCrewReading reading)
    {
        _crew[reading.Kerbal] = reading;
        _repository.SaveCrewReading(reading);
    }

    private void Store(SavedVessel vessel, SurveyReading reading)
    {
        _readings[vessel.Id] = reading;
        _repository.SaveReading(vessel.Id, vessel.Name, reading);
    }

    private void RaiseChanged() => Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());
}
