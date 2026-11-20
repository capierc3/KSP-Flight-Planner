using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KspFlightPlanner.KerbinResources;
using UnityEngine;

namespace KspFlightPlanner.Roles
{
    /// <summary>
    /// A finished body's survey report being written, then filed (docs/SURVEY.md "When a body is done"). The planner says
    /// when a body is done and who surveyed it; the writing takes <see cref="NeedS"/> of game time while at least one writer
    /// is on duty in a lab (or at KSC).
    /// </summary>
    internal sealed class SurveyReportJob
    {
        public string Body = "";
        public readonly List<string> Writers = new List<string>();
        public double DoneUt;
        public double NeedS;
        public double WorkedS;
        public double LastUt;
        public double FiledUt = double.NaN;
        public string FiledBy = "";

        internal bool Filed => !double.IsNaN(FiledUt);

        internal void Save(ConfigNode n)
        {
            n.AddValue("body", Body);
            foreach (var w in Writers) n.AddValue("writer", w);
            n.AddValue("doneUt", R(DoneUt));
            n.AddValue("needS", R(NeedS));
            n.AddValue("workedS", R(WorkedS));
            n.AddValue("lastUt", R(LastUt));
            if (Filed)
            {
                n.AddValue("filedUt", R(FiledUt));
                n.AddValue("filedBy", FiledBy);
            }
        }

        internal static SurveyReportJob Load(ConfigNode n)
        {
            var j = new SurveyReportJob
            {
                Body = n.GetValue("body") ?? "", DoneUt = Num(n, "doneUt"), NeedS = Num(n, "needS"), WorkedS = Num(n, "workedS"),
                LastUt = Num(n, "lastUt"), FiledUt = n.HasValue("filedUt") ? Num(n, "filedUt") : double.NaN, FiledBy = n.GetValue("filedBy") ?? "",
            };
            j.Writers.AddRange(n.GetValues("writer"));
            return j;
        }

        /// <summary>A line for the planner: body, done at, filed at (NaN while writing), filed by, writers joined by commas, worked and needed seconds.</summary>
        internal string Line() => string.Join("\t", new[]
        {
            Body, R(DoneUt), Filed ? R(FiledUt) : "NaN", FiledBy, string.Join(",", Writers.ToArray()), R(WorkedS), R(NeedS),
        });

        internal static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        internal static double Num(ConfigNode n, string key) =>
            double.TryParse(n.GetValue(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary>
    /// Survey Engineers and Science Officers (docs/KERBIN-RESOURCES.md "Survey and science roles"): gives their job titles
    /// to Kerbin Resources, keeps the Survey Engineers' craft for the planner, writes finished bodies' reports, notes visits
    /// to anomalies, and runs the Science Officers' probe sessions. What it keeps goes in the save, where the planner reads it.
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT, GameScenes.TRACKSTATION, GameScenes.SPACECENTER)]
    public sealed class RolesScenario : ScenarioModule
    {
        internal static RolesScenario Instance { get; private set; }

        /// <summary>Survey Engineers as last looked at, with the craft they can watch.</summary>
        internal List<SurveyWorker> Workers = new List<SurveyWorker>();

        /// <summary>Finished bodies' reports, by body: being written, or filed.</summary>
        internal readonly Dictionary<string, SurveyReportJob> Reports = new Dictionary<string, SurveyReportJob>();

        internal readonly List<AnomalyVisit> Visits = new List<AnomalyVisit>();

        internal readonly ScienceOfficers Officers = new ScienceOfficers();

        private float _nextLook = 2, _nextTick = 1;

        private static double Now => Planetarium.GetUniversalTime();

        public override void OnAwake()
        {
            Instance = this;
            DutyRoster.AddJobGiver(SurveyRoles.JobOf);
            KerbinResourcesScenario.AddCardRows(WorkRows);
        }

        /// <summary>The data card's survey and science lines (a method, so adding it again on every scene does nothing).</summary>
        private static IEnumerable<KeyValuePair<string, string>> WorkRows(string kerbal) =>
            KerbinResourcesScenario.Instance?.Work.CardRows(kerbal) ?? Enumerable.Empty<KeyValuePair<string, string>>();

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (HighLogic.CurrentGame == null) return;
            var now = Now;
            if (Time.timeSinceLevelLoad >= _nextTick)
            {
                _nextTick = Time.timeSinceLevelLoad + 1;
                if (Scene.CanCheckLinks)
                {
                    try { Officers.Tick(now); }
                    catch (Exception e) { Debug.LogWarning("[KspFlightPlanner] Roles: Science Officers failed: " + e); }
                }
            }
            if (Time.timeSinceLevelLoad < _nextLook) return;
            _nextLook = Time.timeSinceLevelLoad + 10;
            try
            {
                if (Scene.CanCheckLinks)
                {
                    Workers = SurveyRoles.Workers();
                    Anomalies.LookForVisits(Visits, now);
                }
                WriteReports(now);
            }
            catch (Exception e) { Debug.LogWarning("[KspFlightPlanner] Roles: look-over failed: " + e); }
        }

        /// <summary>
        /// The planner found a body done: its surveyors start writing the report. Nothing changes if it's already being
        /// written or filed. Returns what happened, for the planner.
        /// </summary>
        internal string SurveyDone(string body, double ut, IEnumerable<string> writers)
        {
            if (string.IsNullOrEmpty(body)) return "no body";
            if (Reports.ContainsKey(body)) return "already";
            var job = new SurveyReportJob { Body = body, DoneUt = ut, NeedS = RolesSettings.Current.ReportHours * 3600.0, LastUt = Now };
            job.Writers.AddRange((writers ?? Enumerable.Empty<string>()).Where(w => !string.IsNullOrEmpty(w)).Distinct());
            Reports[body] = job;
            KerbinResourcesScenario.Instance?.Duty.UpdateJobs(Now);
            var who = job.Writers.Count > 0 ? string.Join(" and ", job.Writers.ToArray()) : "Nobody";
            ScreenMessages.PostScreenMessage("Ore survey: " + body + " is mapped. " + who + (job.Writers.Count > 0 ? " start writing the report." : " is left to write the report."), 8f, ScreenMessageStyle.UPPER_CENTER);
            return "started";
        }

        /// <summary>Report writing moves on while a writer is on duty (in a lab, or at KSC); a finished one is filed.</summary>
        private void WriteReports(double now)
        {
            var duty = KerbinResourcesScenario.Instance?.Duty;
            if (duty == null) return;
            var changed = false;
            foreach (var job in Reports.Values.Where(j => !j.Filed))
            {
                var step = Math.Max(0, now - job.LastUt);
                job.LastUt = now;
                var writing = job.Writers.Select(duty.ShiftOf).FirstOrDefault(s => s != null && s.Job == SurveyRoles.WritingJob(job.Body));
                if (writing == null || step <= 0) continue;
                job.WorkedS += step;
                if (job.WorkedS < job.NeedS) continue;
                job.FiledUt = now;
                job.FiledBy = writing.Kerbal;
                changed = true;
                foreach (var w in job.Writers)
                    KerbinResourcesScenario.Instance?.Work.Add(new WorkEntry
                    {
                        Ut = now, Kerbal = w, Kind = WorkEntry.Report, Body = job.Body,
                        PostName = duty.ShiftOf(w) is { } sh ? (sh.AtKsc ? DutyShift.Ksc : sh.PostName) : "", Detail = job.Body + " survey report",
                    });
                var line = writing.Kerbal + " filed the " + job.Body + " survey report.";
                Debug.Log("[KspFlightPlanner] Roles: " + line);
                ScreenMessages.PostScreenMessage("Ore survey: " + line, 8f, ScreenMessageStyle.UPPER_CENTER);
            }
            if (changed) duty.UpdateJobs(now);
        }

        public override void OnSave(ConfigNode node)
        {
            foreach (var w in Workers) w.Save(node.AddNode("WORKER"));
            foreach (var r in Reports.Values) r.Save(node.AddNode("REPORT"));
            foreach (var v in Visits) v.Save(node.AddNode("VISIT"));
            Officers.Save(node.AddNode("OFFICERS"));
        }

        public override void OnLoad(ConfigNode node)
        {
            Workers = node.GetNodes("WORKER").Select(SurveyWorker.Load).Where(w => w.Kerbal.Length > 0).ToList();
            Reports.Clear();
            foreach (var n in node.GetNodes("REPORT"))
            {
                var r = SurveyReportJob.Load(n);
                if (r.Body.Length > 0) Reports[r.Body] = r;
            }
            Visits.Clear();
            Visits.AddRange(node.GetNodes("VISIT").Select(AnomalyVisit.Load).Where(v => v.Name.Length > 0));
            Officers.Load(node.GetNode("OFFICERS"));
        }
    }
}
