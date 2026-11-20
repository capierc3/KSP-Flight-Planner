using KRPC.Client;
using KspFlightPlanner.Core.Ksp;

namespace KspFlightPlanner.App.Services;

/// <summary>Science gaps' calls to the planner's add-on ("PlannerScience", mod/…/Science), docs/SCIENCE.md.</summary>
public sealed partial class KspLink
{
    private const string ScienceService = "PlannerScience";

    /// <summary>The science part of the add-on's version, or null when this add-on build doesn't have it.</summary>
    public async Task<string?> ScienceAddOnVersionAsync()
    {
        try { return await CallAsync(c => Science<string>(c, "Version")); }
        catch (RPCException) { return null; }
    }

    /// <summary>The game's experiments, which parts run them, and every body's multipliers and biomes: three quick calls.</summary>
    public async Task<ScienceCatalog> FetchScienceCatalogAsync()
    {
        var experiments = await CallAsync(c => Science<IList<string>>(c, "Experiments"));
        var parts = await CallAsync(c => Science<IList<string>>(c, "PartExperiments"));
        var bodies = await CallAsync(c => Science<IList<string>>(c, "Bodies"));
        return ScienceCatalogReader.Parse(experiments, parts, bodies, DateTime.UtcNow);
    }

    /// <summary>
    /// The body's biomes on the survey's 1° grid, through the add-on's BiomeGrid (a few calls; the game pauses briefly
    /// for each), with its rotation.
    /// </summary>
    public async Task<ScienceBiomeMap> FetchScienceBiomeMapAsync(string bodyName)
    {
        var body = await ReadBodyAsync(bodyName);
        var names = await CallAsync(c => Survey<IList<string>>(c, "BiomeNames", bodyName));
        var step = SurveyStepDeg;
        var rows = SurfaceBake.GridRows(step);
        var columns = SurfaceBake.GridColumns(step);
        var rowsPerCall = Math.Max(1, SurveyCellsPerCall / columns);
        var biomes = new List<short>(rows * columns);
        for (var first = 0; first < rows; first += rowsPerCall)
        {
            var count = Math.Min(rowsPerCall, rows - first);
            var latMin = SurfaceBake.RowLatitude(step, first);
            var latMax = SurfaceBake.RowLatitude(step, first + count - 1);
            var lonMax = SurfaceBake.ColumnLongitude(step, columns - 1);
            biomes.AddRange((await CallAsync(c => Survey<IList<int>>(c, "BiomeGrid", bodyName, latMin, latMax, count, -180.0, lonMax, columns)))
                .Select(b => (short)b));
        }
        return new ScienceBiomeMap(bodyName, step, body.RadiusM, body.RotationPeriodS, body.InitialRotationDeg,
            biomes.ToArray(), names.ToList(), DateTime.UtcNow);
    }

    private static T Science<T>(Connection c, string procedure) =>
        (T)Encoder.Decode(c.Invoke(ScienceService, procedure, []), typeof(T), c);
}
