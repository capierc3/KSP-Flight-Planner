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

    private static T Science<T>(Connection c, string procedure) =>
        (T)Encoder.Decode(c.Invoke(ScienceService, procedure, []), typeof(T), c);
}
