using System.Globalization;
using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core.Ksp;

/// <summary>A craft a Survey Engineer can watch: its save id (pid) and name.</summary>
public sealed record WatchedCraft(string Pid, string Name);

/// <summary>
/// A Survey Engineer on duty as the add-on last saw them: where they're posted and the craft whose scanners they can watch
/// (the station's own, and linked probes). docs/KERBIN-RESOURCES.md "Survey Engineer at work".
/// </summary>
public sealed record SurveyWorker(string Kerbal, string PostName, string PostPid, string BodyName, bool AtKsc, IReadOnlyList<WatchedCraft> Crafts);

/// <summary>A finished body's survey report: being written (<see cref="FiledUt"/> null), or filed. docs/SURVEY.md "When a body is done".</summary>
public sealed record SurveyReportJob(string BodyName, double DoneUt, double? FiledUt, string FiledBy, IReadOnlyList<string> Writers, double WorkedS, double NeedS)
{
    public bool Filed => FiledUt is not null;
}

/// <summary>A craft came within range of an anomaly, which identifies it.</summary>
public sealed record AnomalyVisit(string BodyName, string Name, double Ut, string Vessel);

/// <summary>An anomaly as the game has it: a surface object on a body, its internal name and the name the game shows.</summary>
public sealed record SurveyAnomaly(string BodyName, string Name, string DisplayName, double LatitudeDeg, double LongitudeDeg);

/// <summary>The crew roles' survey rules from the save's difficulty settings (the add-on's RolesSettings).</summary>
public sealed record RolesRules(bool SurveyEngineers, int DonePercent, bool HideLandmarks)
{
    public static readonly RolesRules Default = new(true, 95, true);
}

/// <summary>Reads the add-on's crew-role answers (tab-separated lines, mod/…/Roles/RolesService.cs) and its part of the save.</summary>
public static class RolesReader
{
    public static IReadOnlyList<SurveyWorker> Workers(IEnumerable<string> lines) =>
        lines.Select(l => l.Split('\t')).Where(f => f.Length >= 6 && f[0].Length > 0)
            .Select(f => new SurveyWorker(f[0], f[1], f[2], f[3], f[4] == "1",
                f[5].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(c => c.Split('|')).Where(c => c[0].Length > 0)
                    .Select(c => new WatchedCraft(c[0], c.Length > 1 ? c[1] : "")).ToList()))
            .ToList();

    public static IReadOnlyList<SurveyReportJob> Reports(IEnumerable<string> lines) =>
        lines.Select(l => l.Split('\t')).Where(f => f.Length >= 7 && f[0].Length > 0)
            .Select(f => new SurveyReportJob(f[0], Num(f[1]), double.IsFinite(Num(f[2])) ? Num(f[2]) : null, f[3],
                f[4].Split(',', StringSplitOptions.RemoveEmptyEntries), Num(f[5]), Num(f[6])))
            .ToList();

    public static IReadOnlyList<AnomalyVisit> Visits(IEnumerable<string> lines) =>
        lines.Select(l => l.Split('\t')).Where(f => f.Length >= 4 && f[1].Length > 0)
            .Select(f => new AnomalyVisit(f[0], f[1], Num(f[2]), f[3])).ToList();

    public static IReadOnlyList<SurveyAnomaly> Anomalies(IEnumerable<string> lines) =>
        lines.Select(l => l.Split('\t')).Where(f => f.Length >= 5 && f[1].Length > 0)
            .Select(f => new SurveyAnomaly(f[0], f[1], f[2].Length > 0 ? f[2] : f[1], Num(f[3]), Num(f[4]))).ToList();

    /// <summary>The add-on's RolesScenario in a save: workers, reports and anomaly visits.</summary>
    internal static (IReadOnlyList<SurveyWorker> Workers, IReadOnlyList<SurveyReportJob> Reports, IReadOnlyList<AnomalyVisit> Visits) FromSave(ConfigNode? scenario)
    {
        if (scenario is null) return ([], [], []);
        var workers = scenario.GetNodes("WORKER").Select(w => new SurveyWorker(
                w.GetValue("kerbal") ?? "", w.GetValue("post") ?? "", w.GetValue("postPid") ?? "", w.GetValue("body") ?? "", w.GetValue("ksc") == "True",
                w.GetNodes("CRAFT").Select(c => new WatchedCraft(c.GetValue("pid") ?? "", c.GetValue("name") ?? "")).Where(c => c.Pid.Length > 0).ToList()))
            .Where(w => w.Kerbal.Length > 0).ToList();
        var reports = scenario.GetNodes("REPORT").Select(r => new SurveyReportJob(
                r.GetValue("body") ?? "", Num(r.GetValue("doneUt")), r.GetValue("filedUt") is { } f ? Num(f) : null, r.GetValue("filedBy") ?? "",
                r.GetValues("writer").ToList(), Num(r.GetValue("workedS")), Num(r.GetValue("needS"))))
            .Where(r => r.BodyName.Length > 0).ToList();
        var visits = scenario.GetNodes("VISIT").Select(v => new AnomalyVisit(v.GetValue("body") ?? "", v.GetValue("name") ?? "", Num(v.GetValue("ut")), v.GetValue("vessel") ?? ""))
            .Where(v => v.Name.Length > 0).ToList();
        return (workers, reports, visits);
    }

    /// <summary>The add-on's settings in the save's difficulty parameters (GAME › PARAMETERS › RolesSettings).</summary>
    internal static RolesRules Rules(ConfigNode? settings) =>
        settings is null ? RolesRules.Default
            : new RolesRules(settings.GetValue("SurveyEngineers") != "False",
                int.TryParse(settings.GetValue("DonePercent"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 95,
                settings.GetValue("HideLandmarks") != "False");

    private static double Num(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
}

/// <summary>One craft in a Survey Engineer's reading: its orbit and scanners then.</summary>
public sealed record SurveyCraftState(string Pid, string Name, OrbitalElements Orbit, bool HasSurveyScanner, bool HasNarrowBand);

/// <summary>
/// One reading of a Survey Engineer (from a save, or the live game): the body they survey and the craft they can watch,
/// each with its orbit. Coverage is credited between two readings of the same engineer (docs/SURVEY.md).
/// </summary>
public sealed record SurveyCrewReading(double Ut, string Kerbal, string BodyName, IReadOnlyList<SurveyCraftState> Crafts, bool BodyScanned)
{
    /// <summary>
    /// The worker's craft as the save has them now: each one found by its save id, in a closed orbit round the worker's
    /// body. Craft the save doesn't have (or not in orbit there) are left out.
    /// </summary>
    public static SurveyCrewReading Of(SurveyWorker worker, KspSave save, double ut)
    {
        var crafts = new List<SurveyCraftState>();
        foreach (var c in worker.Crafts)
        {
            var v = save.Vessels.FirstOrDefault(x => string.Equals(x.Id, c.Pid, StringComparison.OrdinalIgnoreCase));
            if (v?.Orbit is not { IsClosed: true } orbit || orbit.BodyName != worker.BodyName) continue;
            crafts.Add(new SurveyCraftState(v.Id, v.Name, orbit.ToElements(),
                v.Parts.Any(SurveyStation.IsSurveyScanner), v.Parts.Any(SurveyStation.IsNarrowBand)));
        }
        return new SurveyCrewReading(ut, worker.Kerbal, worker.BodyName, crafts, save.ScannedBodies.Contains(worker.BodyName));
    }

    /// <summary>Same body, same craft with the same orbits and scanners: nothing changed but the time.</summary>
    public bool SameAs(SurveyCrewReading other) =>
        other.Kerbal == Kerbal && other.BodyName == BodyName && other.BodyScanned == BodyScanned && other.Crafts.SequenceEqual(Crafts);
}

/// <summary>Who was watching which craft from when, while crediting: the reports name them.</summary>
public sealed record SurveyWatch(double FromUt, string Kerbal, string Craft);

/// <summary>A landmark's stage in the survey (docs/SURVEY.md "Landmarks are found by the survey").</summary>
public enum LandmarkStage
{
    /// <summary>Not covered by a narrow-band strip yet: not shown.</summary>
    Hidden,

    /// <summary>Covered: an anomaly shows as "Unknown object", a natural feature by name.</summary>
    Spotted,

    /// <summary>An anomaly a craft has visited: its real name shows.</summary>
    Identified,

    /// <summary>Always known (launch sites), or hiding is switched off.</summary>
    Known,
}

/// <summary>A landmark as the planner shows it now.</summary>
public sealed record LandmarkView(string Name, string Kind, double LatitudeDeg, double LongitudeDeg, string? Note, LandmarkStage Stage)
{
    public bool Shown => Stage != LandmarkStage.Hidden;

    public Landmark ToLandmark() => new(Name, Kind, LatitudeDeg, LongitudeDeg, Note);
}

/// <summary>Which landmarks the survey has found, and what they're called so far.</summary>
public static class LandmarkSurvey
{
    public const string Anomaly = "anomaly", Feature = "feature", LaunchSite = "launch-site";

    /// <summary>
    /// The body's landmarks with their stage. The game's anomalies (when fetched) replace the wiki's; the wiki's natural
    /// features and launch sites stay. An anomaly or feature is spotted once the narrow-band has covered its spot
    /// (<paramref name="coverage"/>, null: nothing covered); an anomaly is identified once visited. Launch sites, and
    /// everything when <paramref name="hide"/> is off, are known.
    /// </summary>
    public static IReadOnlyList<LandmarkView> For(string bodyName, IReadOnlyList<Landmark> wiki, IReadOnlyList<SurveyAnomaly> game,
        SurveyCoverage? coverage, IReadOnlyCollection<AnomalyVisit> visits, bool hide)
    {
        var list = new List<LandmarkView>();
        var anomalies = game.Where(a => a.BodyName == bodyName).ToList();
        var visited = visits.Where(v => v.BodyName == bodyName).Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var m in wiki)
        {
            if (m.Kind == Anomaly && anomalies.Count > 0) continue; // the game's list replaces the wiki's anomalies
            var covered = Covered(coverage, m.LatitudeDeg, m.LongitudeDeg);
            var stage = !hide || m.Kind == LaunchSite ? LandmarkStage.Known : !covered ? LandmarkStage.Hidden : LandmarkStage.Spotted;
            var name = stage == LandmarkStage.Spotted && m.Kind == Anomaly ? Unknown(m.LatitudeDeg, m.LongitudeDeg) : m.Name;
            list.Add(new LandmarkView(name, m.Kind, m.LatitudeDeg, m.LongitudeDeg, stage == LandmarkStage.Spotted && m.Kind == Anomaly ? null : m.Note, stage));
        }
        foreach (var a in anomalies)
        {
            var stage = !hide ? LandmarkStage.Known
                : visited.Contains(a.Name) ? LandmarkStage.Identified
                : Covered(coverage, a.LatitudeDeg, a.LongitudeDeg) ? LandmarkStage.Spotted : LandmarkStage.Hidden;
            var note = wiki.FirstOrDefault(w => w.Kind == Anomaly && string.Equals(w.Name, a.DisplayName, StringComparison.OrdinalIgnoreCase))?.Note;
            list.Add(stage == LandmarkStage.Spotted
                ? new LandmarkView(Unknown(a.LatitudeDeg, a.LongitudeDeg), Anomaly, a.LatitudeDeg, a.LongitudeDeg, null, stage)
                : new LandmarkView(a.DisplayName, Anomaly, a.LatitudeDeg, a.LongitudeDeg, note, stage));
        }
        return list;
    }

    /// <summary>"Unknown object (23.8° N 61.0° E)": unique per spot, so lists can tell them apart.</summary>
    public static string Unknown(double lat, double lon) => $"Unknown object ({SurveyReports.LatLon(lat, lon)})";

    /// <summary>Has the narrow-band covered the grid point nearest this spot?</summary>
    public static bool Covered(SurveyCoverage? coverage, double lat, double lon) => CoveredAt(coverage, lat, lon) is not null;

    /// <summary>When the narrow-band first covered the grid point nearest this spot; null if it hasn't.</summary>
    public static double? CoveredAt(SurveyCoverage? coverage, double lat, double lon)
    {
        if (coverage is null) return null;
        var step = coverage.StepDeg;
        int rows = SurfaceBake.GridRows(step), columns = SurfaceBake.GridColumns(step);
        var row = Math.Clamp((int)Math.Round((lat + 90) / step), 0, rows - 1);
        var column = ((int)Math.Round((lon + 180) / step) % columns + columns) % columns;
        var t = coverage.Estimate[row * columns + column];
        return float.IsNaN(t) ? null : t;
    }
}
