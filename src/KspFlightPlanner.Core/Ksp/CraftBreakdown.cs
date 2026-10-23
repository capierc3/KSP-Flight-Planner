namespace KspFlightPlanner.Core.Ksp;

/// <summary>One part of a design as built: its mass with what its tanks hold, and its cost with those contents.</summary>
/// <param name="Definition">Null when the part isn't in the part data (a mod that was removed).</param>
/// <param name="SeparationStage">The stage whose firing drops it; -1 when it stays to the end.</param>
public sealed record BuiltPart(string Id, string Name, string Title, string? Category, KspPart? Definition,
    int ActivationStage, int SeparationStage, double DryMassT, double ResourceMassT, double Cost)
{
    public double MassT => DryMassT + ResourceMassT;
}

/// <summary>The parts that leave the craft when one stage fires (or, for stage -1, the parts that stay to the end).</summary>
public sealed record PartDrop(int Stage, IReadOnlyList<BuiltPart> Parts)
{
    public bool StaysToTheEnd => Stage < 0;

    /// <summary>Mass at launch, tanks as loaded.</summary>
    public double MassT => Parts.Sum(p => p.MassT);

    public double Cost => Parts.Sum(p => p.Cost);
}

/// <summary>One part category's share of the design (dry mass; what the tanks hold is counted separately).</summary>
public sealed record CategoryShare(string? Category, int Count, double DryMassT, double Cost);

/// <summary>
/// What a design is made of, beyond its staging Δv (<see cref="StagingSimulation"/>): its parts with mass and cost,
/// which stage drops each, mass and cost by part category, and things that look wrong (docs/MATH.md §7.5).
/// </summary>
/// <param name="FinalMassT">What's left once the last stage has burned.</param>
/// <param name="WeakestKeptPart">The part with the lowest crash tolerance among those that stay to the end.</param>
/// <param name="Warnings">Things that are probably mistakes (an engine that never fires, a launch TWR below 1).</param>
/// <param name="Notes">Things worth knowing that aren't mistakes (a deployed payload's engines).</param>
public sealed record CraftBreakdown(
    IReadOnlyList<BuiltPart> Parts,
    IReadOnlyList<PartDrop> Drops,
    IReadOnlyList<CategoryShare> Categories,
    double ResourceMassT,
    double Cost,
    int CrewCapacity,
    double FinalMassT,
    BuiltPart? WeakestKeptPart,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Notes)
{
    public double DryMassT => Parts.Sum(p => p.DryMassT);

    /// <summary>Cost of the parts that stay to the end (the ones that can come home to be recovered).</summary>
    public double KeptCost => Drops.Where(d => d.StaysToTheEnd).Sum(d => d.Cost);

    /// <summary>Below this, the launch stage can't lift the rocket off the pad.</summary>
    public const double MinimumLaunchTwr = 1.0;

    /// <param name="design">The staging worked out for the same craft and catalog.</param>
    public static CraftBreakdown Build(CraftFile craft, PartCatalog catalog, DesignReport design)
    {
        var parts = craft.Parts.Select(p => Built(p, catalog)).ToList();

        // Firing order (the highest stage number fires first), the parts that are never dropped last.
        var drops = parts.GroupBy(p => p.SeparationStage < 0 ? -1 : p.SeparationStage)
            .OrderByDescending(g => g.Key < 0 ? int.MinValue : g.Key)
            .Select(g => new PartDrop(g.Key, g.ToList()))
            .ToList();

        var categories = parts.GroupBy(p => p.Category)
            .Select(g => new CategoryShare(g.Key, g.Count(), g.Sum(p => p.DryMassT), g.Sum(p => p.Cost)))
            .OrderByDescending(c => c.DryMassT).ThenBy(c => c.Category)
            .ToList();

        var kept = parts.Where(p => p.SeparationStage < 0).ToList();
        var weakest = kept.Where(p => p.Definition is { CrashToleranceMs: > 0 }).MinBy(p => p.Definition!.CrashToleranceMs);
        var finalMass = design.Stages.Count > 0 ? design.Stages[^1].EndMassT : kept.Sum(p => p.MassT);

        var (warnings, notes) = Findings(craft, parts, design);
        return new CraftBreakdown(parts, drops, categories, parts.Sum(p => p.ResourceMassT), parts.Sum(p => p.Cost),
            parts.Sum(p => p.Definition?.CrewCapacity ?? 0), finalMass, weakest, warnings, notes);
    }

    /// <summary>A stage's TWR at the start of its burn on a body's surface (docs/MATH.md §7.4).</summary>
    public static double TwrAt(StageResult stage, double surfaceGravity, double surfacePressureAtm) =>
        stage.StartMassT > 0 && surfaceGravity > 0 ? DesignCheck.ThrustAt(stage, surfacePressureAtm) / (stage.StartMassT * surfaceGravity) : 0;

    private static BuiltPart Built(CraftPart p, PartCatalog catalog)
    {
        var definition = catalog.Part(p.Name);
        double UnitCost(string resource) => catalog.Resources.GetValueOrDefault(resource)?.UnitCost ?? 0;
        // KSP's part cost is with its tanks full; what the craft loads instead replaces that.
        var fullContents = definition?.Resources.Sum(r => r.MaxAmount * UnitCost(r.Name)) ?? 0;
        var loaded = p.Resources.Sum(r => r.Amount * UnitCost(r.Name));
        var cost = Math.Max(0, (definition?.Cost ?? 0) - fullContents + loaded);
        return new BuiltPart(p.Id, p.Name, definition?.Title ?? p.Name, definition?.Category, definition,
            p.ActivationStage, p.SeparationStage, (definition?.MassT ?? 0) + p.ExtraMassT,
            p.Resources.Sum(r => r.Amount * catalog.Density(r.Name)), cost);
    }

    /// <summary>Parts that leave the craft by the time their own stage fires (present while SeparationStage &lt; stage).</summary>
    private static bool IsPayload(BuiltPart part) => part.SeparationStage >= 0 && part.SeparationStage >= part.ActivationStage;

    private static (List<string> Warnings, List<string> Notes) Findings(CraftFile craft, List<BuiltPart> parts, DesignReport design)
    {
        var warnings = new List<string>();
        var notes = new List<string>();

        // Rocket engines the staging never burns: not activated, or nothing to draw from while they're attached. (Engines
        // with an air-breathing mode are left out: in that mode they have no fixed Δv to work out.)
        var idle = parts.Where(p => p.Definition is { Engines.Count: > 0 } d && d.Engines.All(e => !e.BreathesAir) && !design.FiredEngineIds.Contains(p.Id))
            .ToList();
        // Dropped by the time their own stage fires: a deployed payload's engine, flown on its own after separation.
        foreach (var engine in idle.Where(p => !IsPayload(p)).GroupBy(p => p.Title))
            warnings.Add($"{Count(engine.Count(), engine.Key)} never {(engine.Count() == 1 ? "fires" : "fire")} with fuel: " +
                         "no stage activates it while a tank it can draw from still has propellant.");
        foreach (var engine in idle.Where(IsPayload).GroupBy(p => p.Title))
            notes.Add($"{Count(engine.Count(), engine.Key)} {(engine.Count() == 1 ? "is" : "are")} dropped before {(engine.Count() == 1 ? "its" : "their")} " +
                      "own stage fires (a deployed payload?). Payloads are flown on their own after separation, so their Δv isn't counted here.");

        // A rocket from the VAB has to leave the pad on its first stage (planes take off on wheels and wings).
        if (craft.Facility == "VAB" && design.Stages.Count > 0 && design.LaunchTwr < MinimumLaunchTwr)
            warnings.Add($"The first stage's TWR at sea level is {design.LaunchTwr:0.00}: below {MinimumLaunchTwr:0.0} it can't lift off the pad.");
        return (warnings, notes);
    }

    private static string Count(int count, string title) => count == 1 ? title : $"{count} × {title}";
}
