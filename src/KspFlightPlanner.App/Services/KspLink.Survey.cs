using Google.Protobuf;
using KRPC.Client;
using KRPC.Client.Services.SpaceCenter;
using KRPC.Client.Services.UI;
using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.App.Services;

/// <summary>The ore survey's calls: the planner's own kRPC add-on ("PlannerSurvey", mod/), and the survey scanner's button.</summary>
public sealed partial class KspLink
{
    private const string SurveyService = "PlannerSurvey";

    /// <summary>The survey grid: one point per degree, like the surface bakes.</summary>
    public const double SurveyStepDeg = 1;

    /// <summary>The add-on answers at most this many points per call (SurveyService.MaxGridCells).</summary>
    private const int SurveyCellsPerCall = 20_000;

    /// <summary>The add-on's version, or null when it isn't installed (kRPC doesn't know the service).</summary>
    public async Task<string?> SurveyAddOnVersionAsync()
    {
        try { return await CallAsync(c => Survey<string>(c, "Version")); }
        catch (RPCException) { return null; }
    }

    /// <summary>
    /// The body's ore map as the game generates it for this save: true abundance, the scanners' estimate and the biomes
    /// on a 1° grid, with the body's rotation. A few calls of up to 20,000 points; each makes the game pause briefly.
    /// </summary>
    public async Task<SurveyMap> FetchSurveyMapAsync(string bodyName, IProgress<double>? progress = null)
    {
        var body = await ReadBodyAsync(bodyName);
        var names = await CallAsync(c => Survey<IList<string>>(c, "BiomeNames", bodyName));
        var step = SurveyStepDeg;
        var rows = SurfaceBake.GridRows(step);
        var columns = SurfaceBake.GridColumns(step);
        var rowsPerCall = Math.Max(1, SurveyCellsPerCall / columns);
        var abundance = new List<float>(rows * columns);
        var estimate = new List<float>(rows * columns);
        var biomes = new List<short>(rows * columns);
        for (var first = 0; first < rows; first += rowsPerCall)
        {
            var count = Math.Min(rowsPerCall, rows - first);
            var latMin = SurfaceBake.RowLatitude(step, first);
            var latMax = SurfaceBake.RowLatitude(step, first + count - 1);
            var lonMax = SurfaceBake.ColumnLongitude(step, columns - 1);
            abundance.AddRange(await CallAsync(c => Survey<IList<float>>(c, "AbundanceGrid", bodyName, "Ore", latMin, latMax, count, -180.0, lonMax, columns)));
            estimate.AddRange(await CallAsync(c => Survey<IList<float>>(c, "EstimateGrid", bodyName, "Ore", latMin, latMax, count, -180.0, lonMax, columns)));
            biomes.AddRange((await CallAsync(c => Survey<IList<int>>(c, "BiomeGrid", bodyName, latMin, latMax, count, -180.0, lonMax, columns)))
                .Select(b => (short)b));
            progress?.Report((first + count) / (double)rows);
        }
        return new SurveyMap(bodyName, step, body.RadiusM, body.RotationPeriodS, body.InitialRotationDeg,
            abundance.ToArray(), estimate.ToArray(), biomes.ToArray(), names.ToList(), DateTime.UtcNow);
    }

    /// <summary>
    /// Presses the survey scanner's <i>Perform orbital survey</i> on the vessel being flown. Returns why it couldn't, or null
    /// once the game has taken it (the game itself still checks the orbit, and says so on screen if it's wrong).
    /// </summary>
    public Task<string?> PerformOrbitalSurveyAsync() => CallAsync<string?>(c =>
    {
        var vessel = c.SpaceCenter().ActiveVessel;
        var module = vessel.Parts.All.SelectMany(p => p.Modules).FirstOrDefault(m => m.Name == "ModuleOrbitalSurveyor");
        if (module is null) return "The craft you're flying has no survey scanner.";
        var button = module.EventList.FirstOrDefault(e => e.Visible && e.Active && e.GuiName.Contains("orbital survey", StringComparison.OrdinalIgnoreCase));
        if (button is null) return "The survey scanner isn't offering its survey button (deploy it, or wait for it to finish).";
        if (!vessel.Comms.CanTransmitScience) return "The craft can't send the survey home: it needs a working antenna and a connection.";
        button.Trigger();
        return null;
    });

    /// <summary>Shows a message on the game's screen (top centre, like KSP's own), for <paramref name="seconds"/>.</summary>
    public Task ShowMessageAsync(string text, float seconds = 8) => CallAsync(c =>
    {
        c.UI().Message(text, seconds, KRPC.Client.Services.UI.MessagePosition.TopCenter, Tuple.Create(0.6, 1.0, 0.6), 20);
        return true;
    });

    /// <summary>Name prefix of the waypoints the survey puts in the game, so sending again replaces them.</summary>
    public const string SurveyWaypointPrefix = "Ore ";

    /// <summary>
    /// Puts the spots on the game's map as waypoints ("Ore 1: Greater Flats ≈ 7%"), replacing the survey's earlier ones on
    /// that body. Returns how many were added.
    /// </summary>
    public Task<int> SendSurveyWaypointsAsync(string bodyName, IReadOnlyList<(string Name, double LatitudeDeg, double LongitudeDeg)> spots) =>
        CallAsync(c =>
        {
            var space = c.SpaceCenter();
            var body = Body(c, bodyName);
            var manager = space.WaypointManager;
            foreach (var old in manager.Waypoints.Where(w => w.Body.Name == body.Name && w.Name.StartsWith(SurveyWaypointPrefix, StringComparison.Ordinal)).ToList())
                old.Remove();
            foreach (var (name, lat, lon) in spots) manager.AddWaypoint(lat, lon, body, name);
            return spots.Count;
        });

    private static T Survey<T>(Connection c, string procedure, params object[] args)
    {
        var encoded = args.Select(a => Encoder.Encode(a, a.GetType())).ToList();
        return (T)Encoder.Decode(c.Invoke(SurveyService, procedure, encoded), typeof(T), c);
    }
}
