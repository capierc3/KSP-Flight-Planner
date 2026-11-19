using KRPC.Client;
using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.App.Services;

/// <summary>Crew roles' calls to the planner's add-on ("PlannerRoles", mod/…/Roles), docs/KERBIN-RESOURCES.md "Survey and science roles".</summary>
public sealed partial class KspLink
{
    private const string RolesService = "PlannerRoles";

    /// <summary>The roles part of the add-on's version, or null when this add-on build doesn't have it.</summary>
    public async Task<string?> RolesAddOnVersionAsync()
    {
        try { return await CallAsync(c => Roles<string>(c, "Version")); }
        catch (RPCException) { return null; }
    }

    /// <summary>Survey Engineers on duty now and the craft each can watch (links checked again where the game can).</summary>
    public async Task<IReadOnlyList<SurveyWorker>> SurveyWorkersAsync() =>
        RolesReader.Workers(await CallAsync(c => Roles<IList<string>>(c, "SurveyWorkers")));

    /// <summary>Tells the game a body's survey is done, so its surveyors write the report. Returns "started", "already" or why not.</summary>
    public Task<string> SurveyDoneAsync(string body, double ut, IEnumerable<string> writers) =>
        CallAsync(c => Roles<string>(c, "SurveyDone", body, ut, string.Join(",", writers)));

    /// <summary>Finished bodies' reports, being written or filed.</summary>
    public async Task<IReadOnlyList<SurveyReportJob>> SurveyReportJobsAsync() =>
        RolesReader.Reports(await CallAsync(c => Roles<IList<string>>(c, "SurveyReports")));

    /// <summary>Every anomaly in the game, mods' too.</summary>
    public async Task<IReadOnlyList<SurveyAnomaly>> FetchAnomaliesAsync() =>
        RolesReader.Anomalies(await CallAsync(c => Roles<IList<string>>(c, "Anomalies")));

    /// <summary>Anomalies a craft has visited.</summary>
    public async Task<IReadOnlyList<AnomalyVisit>> AnomalyVisitsAsync() =>
        RolesReader.Visits(await CallAsync(c => Roles<IList<string>>(c, "Visits")));

    /// <summary>Puts a survey find on a kerbal's work log in Kerbin Resources ("survey" or "spotted").</summary>
    public Task LogSurveyAsync(string kerbal, string kind, string body, string vessel, string detail, double ut) =>
        CallAsync(c => { Roles<object?>(c, "LogSurvey", kerbal, kind, body, vessel, detail, ut); return true; });

    /// <summary>Science Officers at work: officer, probe, step, seconds left in it.</summary>
    public async Task<IReadOnlyList<string[]>> ScienceSessionsAsync() =>
        (await CallAsync(c => Roles<IList<string>>(c, "Sessions"))).Select(l => l.Split('\t')).Where(f => f.Length >= 4).ToList();

    private static T Roles<T>(Connection c, string procedure, params object[] args)
    {
        var encoded = args.Select(a => Encoder.Encode(a, a.GetType())).ToList();
        var result = c.Invoke(RolesService, procedure, encoded);
        return typeof(T) == typeof(object) ? default! : (T)Encoder.Decode(result, typeof(T), c);
    }
}
