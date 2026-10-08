namespace KspFlightPlanner.Core.Ksp;

/// <summary>A mission phase's Δv need (margins included), in burn order.</summary>
public sealed record PhaseNeed(string Name, double DeltaV);

/// <summary>How one phase is covered by the design's stages.</summary>
/// <param name="Stages">KSP stage numbers that burn for this phase, in firing order.</param>
public sealed record PhaseCoverage(string Name, double Needed, double Covered, IReadOnlyList<int> Stages)
{
    public double Shortfall => Math.Max(0, Needed - Covered);
    public bool IsCovered => Shortfall < 0.5;
}

/// <summary>Where one stage's Δv goes.</summary>
/// <param name="Phases">The phases this stage burns for, with the Δv spent on each.</param>
/// <param name="Spare">Δv left in the stage once every phase is covered.</param>
public sealed record StageUse(int Stage, double DeltaV, IReadOnlyList<(int Phase, double DeltaV)> Phases, double Spare);

/// <summary>A design checked against a mission (docs/MATH.md §16c).</summary>
public sealed record DesignCheckResult(IReadOnlyList<PhaseCoverage> Phases, IReadOnlyList<StageUse> Stages, double MissionDeltaV,
    double DesignDeltaV, double LaunchTwr)
{
    public bool IsEnough => Phases.All(p => p.IsCovered);

    /// <summary>Positive: Δv to spare after the whole mission. Negative: how much is missing.</summary>
    public double Surplus => DesignDeltaV - MissionDeltaV;

    /// <summary>A rocket with TWR below 1 can't leave the pad.</summary>
    public bool CanLiftOff => LaunchTwr >= 1;
}

/// <summary>
/// Checks a design against a mission: the stages' vacuum Δv is poured into the phases in order — the first stage
/// fills the first phase (launch), any rest carries into the next phase, and so on. A stage that runs out part-way
/// through a phase hands over to the next stage, as in flight.
/// </summary>
public static class DesignCheck
{
    public static DesignCheckResult Check(IReadOnlyList<PhaseNeed> phases, DesignReport design)
    {
        var covered = new double[phases.Count];
        var stagesPerPhase = phases.Select(_ => new List<int>()).ToArray();
        var uses = new List<StageUse>();
        var phase = NextPhase(phases, covered, 0);

        foreach (var stage in design.Stages)
        {
            var left = stage.DeltaVVacuum;
            var spent = new List<(int Phase, double DeltaV)>();
            while (left > 1e-9 && phase < phases.Count)
            {
                var use = Math.Min(left, phases[phase].DeltaV - covered[phase]);
                covered[phase] += use;
                left -= use;
                spent.Add((phase, use));
                stagesPerPhase[phase].Add(stage.Stage);
                if (phases[phase].DeltaV - covered[phase] <= 1e-9) phase = NextPhase(phases, covered, phase + 1);
            }
            uses.Add(new StageUse(stage.Stage, stage.DeltaVVacuum, spent, left));
        }

        var coverage = phases.Select((p, i) => new PhaseCoverage(p.Name, p.DeltaV, covered[i], stagesPerPhase[i])).ToList();
        return new DesignCheckResult(coverage, uses, phases.Sum(p => p.DeltaV), design.TotalDeltaVVacuum, design.LaunchTwr);
    }

    /// <summary>The next phase from <paramref name="from"/> that still needs Δv (phases needing none are skipped).</summary>
    private static int NextPhase(IReadOnlyList<PhaseNeed> phases, double[] covered, int from)
    {
        while (from < phases.Count && phases[from].DeltaV - covered[from] <= 1e-9) from++;
        return from;
    }
}
