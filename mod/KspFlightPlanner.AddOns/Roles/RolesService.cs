using System.Collections.Generic;
using System.Linq;
using KRPC.Service;
using KRPC.Service.Attributes;
using KspFlightPlanner.KerbinResources;

namespace KspFlightPlanner.Roles
{
    /// <summary>
    /// kRPC service "PlannerRoles": what the planner needs from the crew roles (docs/KERBIN-RESOURCES.md "Survey and
    /// science roles"). Answers are lines of tab-separated fields, as the other planner services give.
    /// </summary>
    [KRPCService(Name = "PlannerRoles", GameScene = GameScene.All)]
    public static class RolesService
    {
        [KRPCProcedure]
        public static string Version() => typeof(RolesService).Assembly.GetName().Version.ToString();

        /// <summary>
        /// Survey Engineers on duty now and the craft each can watch: kerbal, post, post pid, body, at KSC (1/0), then
        /// pid|name pairs joined by commas. Looked at again now where CommNet runs; otherwise the last look.
        /// </summary>
        [KRPCProcedure]
        public static IList<string> SurveyWorkers()
        {
            var scenario = RolesScenario.Instance;
            if (scenario == null) return new List<string>();
            if (Scene.CanCheckLinks) scenario.Workers = SurveyRoles.Workers();
            return scenario.Workers.Select(w => w.Line()).ToList();
        }

        /// <summary>A body's survey is done: its surveyors (names joined by commas) start writing the report. Returns "started", "already" or why not.</summary>
        [KRPCProcedure]
        public static string SurveyDone(string body, double ut, string writers) =>
            RolesScenario.Instance?.SurveyDone(body, ut, (writers ?? "").Split(',').Select(w => w.Trim())) ?? "no game";

        /// <summary>Finished bodies' reports: body, done at, filed at (NaN while writing), filed by, writers, worked and needed seconds.</summary>
        [KRPCProcedure]
        public static IList<string> SurveyReports() =>
            RolesScenario.Instance?.Reports.Values.Select(r => r.Line()).ToList() ?? new List<string>();

        /// <summary>Every anomaly in the game: body, name, display name, latitude, longitude (degrees).</summary>
        [KRPCProcedure]
        public static IList<string> Anomalies() => Roles.Anomalies.Lines();

        /// <summary>Anomalies a craft has visited: body, name, game time, the craft.</summary>
        [KRPCProcedure]
        public static IList<string> Visits() =>
            RolesScenario.Instance?.Visits.Select(v => v.Line()).ToList() ?? new List<string>();

        /// <summary>
        /// Puts a survey find on a kerbal's work log: <paramref name="kind"/> "survey" (a biome mapped) or "spotted" (a
        /// landmark). Called by the planner, which works the survey out.
        /// </summary>
        [KRPCProcedure]
        public static void LogSurvey(string kerbal, string kind, string body, string vessel, string detail, double ut)
        {
            var duty = KerbinResourcesScenario.Instance?.Duty;
            var shift = duty?.ShiftOf(kerbal);
            KerbinResourcesScenario.Instance?.Work.Add(new WorkEntry
            {
                Ut = ut, Kerbal = kerbal ?? "", Kind = kind == WorkEntry.Spotted ? WorkEntry.Spotted : WorkEntry.Survey, Body = body ?? "",
                PostName = shift == null ? "" : shift.AtKsc ? DutyShift.Ksc : shift.PostName, Vessel = vessel ?? "", Detail = detail ?? "",
            });
        }

        /// <summary>Science Officers at work: officer, probe, step, seconds left in the step.</summary>
        [KRPCProcedure]
        public static IList<string> Sessions()
        {
            var scenario = RolesScenario.Instance;
            if (scenario == null) return new List<string>();
            var now = Planetarium.GetUniversalTime();
            return scenario.Officers.Sessions.Select(s => string.Join("\t", new[]
            {
                s.Officer, s.ProbeName, s.Stage.ToString(), System.Math.Max(0, s.Until - now).ToString("0", System.Globalization.CultureInfo.InvariantCulture),
            })).ToList();
        }
    }
}
