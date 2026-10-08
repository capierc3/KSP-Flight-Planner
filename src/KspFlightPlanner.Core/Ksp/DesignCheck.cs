namespace KspFlightPlanner.Core.Ksp;

/// <summary>A mission phase's Δv: what its legs need, and the margin added on top (percentage + extra), in burn order.</summary>
public sealed record PhaseNeed(string Name, double Required, double Margin = 0)
{
    public double Total => Required + Margin;
}

/// <summary>How well something is covered: with the margins, only by using part of them, or not at all.</summary>
public enum Coverage
{
    /// <summary>Covers what's needed and the whole margin.</summary>
    Comfortable,
    /// <summary>Covers what's needed, but eats into the margin.</summary>
    InsideMargin,
    /// <summary>Doesn't cover what's needed.</summary>
    Short,
}

/// <summary>How one phase is covered by the design's stages.</summary>
/// <param name="Stages">KSP stage numbers that burn for this phase, in firing order.</param>
/// <param name="Shortfall">Δv missing from what the phase's legs need (margins not counted); 0 unless <see cref="Status"/> is Short.</param>
public sealed record PhaseCoverage(string Name, double Required, double Margin, IReadOnlyList<int> Stages, Coverage Status, double Shortfall);

/// <summary>Touching down on a surface, or lifting off from one.</summary>
public enum SurfaceEventKind { Landing, LiftOff }

/// <summary>
/// A landing or lift-off the rocket has to make: in which phase, how far into that phase's Δv it happens, and the
/// body's surface gravity and pressure (0 for airless bodies).
/// </summary>
public sealed record SurfaceEvent(SurfaceEventKind Kind, int Phase, double OffsetInPhase, string BodyName, double SurfaceGravity,
    double SurfacePressureAtm = 0);

/// <summary>How a thrust-to-weight ratio rates: below 1 the engines can't hold the rocket up.</summary>
public enum TwrRating
{
    /// <summary>1.3 or more.</summary>
    Good,
    /// <summary>Between 1 and 1.3: it works, but slowly and with little room to correct (gravity losses, late suicide burns).</summary>
    Low,
    /// <summary>Below 1: can't lift off, can't stop a descent.</summary>
    TooLow,
    /// <summary>The stages have run out of Δv before this point.</summary>
    NoStage,
}

/// <summary>The TWR of the stage firing at a <see cref="SurfaceEvent"/>.</summary>
/// <param name="Stage">KSP stage number firing then; null if no stage is left.</param>
/// <param name="MassT">The rocket's mass at that moment.</param>
public sealed record SurfaceTwr(SurfaceEvent Event, int? Stage, double MassT, double ThrustKn, double Twr)
{
    public const double LowTwr = 1.3;

    public TwrRating Rating => Stage is null ? TwrRating.NoStage : Twr < 1 ? TwrRating.TooLow : Twr < LowTwr ? TwrRating.Low : TwrRating.Good;
}

/// <summary>Where one stage's Δv goes.</summary>
/// <param name="Phases">The phases this stage burns for, with the Δv spent on each.</param>
/// <param name="Spare">Δv left in the stage once every phase is covered.</param>
public sealed record StageUse(int Stage, double DeltaV, IReadOnlyList<(int Phase, double DeltaV)> Phases, double Spare);

/// <summary>A design checked against a mission (docs/MATH.md §16c).</summary>
public sealed record DesignCheckResult(IReadOnlyList<PhaseCoverage> Phases, IReadOnlyList<StageUse> Stages, double RequiredDeltaV,
    double MarginDeltaV, double DesignDeltaV, double LaunchTwr)
{
    /// <summary>The TWR at each landing and lift-off on the route, in route order.</summary>
    public IReadOnlyList<SurfaceTwr> Surfaces { get; init; } = [];

    /// <summary>The worst phase decides: any phase short → Short; any inside its margin → InsideMargin.</summary>
    public Coverage Status => Phases.Count == 0 ? Coverage.Comfortable : Phases.Max(p => p.Status);

    /// <summary>Δv left after flying the route with no margins (negative: missing).</summary>
    public double SpareOverRequired => DesignDeltaV - RequiredDeltaV;

    /// <summary>Δv left after the route and every margin (negative: how much of the margin can't be kept).</summary>
    public double SpareOverMargins => DesignDeltaV - RequiredDeltaV - MarginDeltaV;

    /// <summary>A rocket with TWR below 1 can't leave the pad.</summary>
    public bool CanLiftOff => LaunchTwr >= 1;
}

/// <summary>
/// Checks a design against a mission: the stages' vacuum Δv is poured into the phases in order — the first stage
/// fills the first phase (launch), any rest carries into the next phase, and so on. A stage that runs out part-way
/// through a phase hands over to the next stage, as in flight.
/// <para>It's poured twice: with the margins (is there room for them?) and without (can it fly the route at all?).
/// Margins are wiggle room, not a requirement, so a design that only fits by using some of them still makes it.</para>
/// </summary>
public static class DesignCheck
{
    /// <param name="surfaces">Landings and lift-offs on the route, to check the TWR of the stage firing at each.</param>
    public static DesignCheckResult Check(IReadOnlyList<PhaseNeed> phases, DesignReport design, IReadOnlyList<SurfaceEvent>? surfaces = null)
    {
        var withMargins = Pour(phases.Select(p => p.Total).ToList(), design);
        var routeOnly = Pour(phases.Select(p => p.Required).ToList(), design);

        var coverage = phases.Select((p, i) =>
        {
            var status = withMargins.Covered[i] >= p.Total - 0.5 ? Coverage.Comfortable
                : routeOnly.Covered[i] >= p.Required - 0.5 ? Coverage.InsideMargin
                : Coverage.Short;
            // Show the pour that matches how it's covered: with margins when there's room, else the route alone.
            var stages = (status == Coverage.Comfortable ? withMargins : routeOnly).StagesPerPhase[i];
            return new PhaseCoverage(p.Name, p.Required, p.Margin, stages, status,
                status == Coverage.Short ? p.Required - routeOnly.Covered[i] : 0);
        }).ToList();

        var comfortable = coverage.All(p => p.Status == Coverage.Comfortable);
        var display = comfortable ? withMargins : routeOnly;
        // Where each phase starts along the stages' Δv, in the same pour that's shown (margins sit at a phase's end).
        var needs = phases.Select(p => comfortable ? p.Total : p.Required).ToList();
        var phaseStart = needs.Select((_, i) => needs.Take(i).Sum()).ToList();

        return new DesignCheckResult(coverage, display.Uses, phases.Sum(p => p.Required), phases.Sum(p => p.Margin),
            design.TotalDeltaVVacuum, design.LaunchTwr)
        {
            Surfaces = (surfaces ?? []).Select(e => SurfaceCheck(e, phaseStart[e.Phase] + e.OffsetInPhase, design)).ToList(),
        };
    }

    /// <summary>
    /// The stage firing <paramref name="position"/> m/s into the stages, the rocket's mass then (constant Isp within a
    /// stage, so m = m_start · (m_end / m_start)^(x / Δv)) and its thrust at the body's surface pressure.
    /// </summary>
    private static SurfaceTwr SurfaceCheck(SurfaceEvent e, double position, DesignReport design)
    {
        var start = 0.0;
        foreach (var stage in design.Stages)
        {
            var end = start + stage.DeltaVVacuum;
            if (position < end - 1e-6)
            {
                var into = Math.Max(0, position - start);
                var mass = stage.DeltaVVacuum > 0 ? stage.StartMassT * Math.Pow(stage.EndMassT / stage.StartMassT, into / stage.DeltaVVacuum)
                    : stage.StartMassT;
                var thrust = ThrustAt(stage, e.SurfacePressureAtm);
                return new SurfaceTwr(e, stage.Stage, mass, thrust, thrust / (mass * e.SurfaceGravity));
            }
            start = end;
        }
        return new SurfaceTwr(e, null, 0, 0, 0);
    }

    /// <summary>
    /// Thrust at a surface pressure: engines lose thrust with Isp as pressure rises, interpolated between vacuum (0 atm)
    /// and sea level (1 atm) and carried on linearly above 1 atm (Eve); never below zero.
    /// </summary>
    public static double ThrustAt(StageResult stage, double pressureAtm) =>
        Math.Max(0, stage.ThrustVacuumKn + (stage.ThrustSeaLevelKn - stage.ThrustVacuumKn) * Math.Max(0, pressureAtm));

    private sealed record PourResult(double[] Covered, List<int>[] StagesPerPhase, List<StageUse> Uses);

    private static PourResult Pour(IReadOnlyList<double> needs, DesignReport design)
    {
        var covered = new double[needs.Count];
        var stagesPerPhase = needs.Select(_ => new List<int>()).ToArray();
        var uses = new List<StageUse>();
        var phase = NextPhase(needs, covered, 0);

        foreach (var stage in design.Stages)
        {
            var left = stage.DeltaVVacuum;
            var spent = new List<(int Phase, double DeltaV)>();
            while (left > 1e-9 && phase < needs.Count)
            {
                var use = Math.Min(left, needs[phase] - covered[phase]);
                covered[phase] += use;
                left -= use;
                spent.Add((phase, use));
                stagesPerPhase[phase].Add(stage.Stage);
                if (needs[phase] - covered[phase] <= 1e-9) phase = NextPhase(needs, covered, phase + 1);
            }
            uses.Add(new StageUse(stage.Stage, stage.DeltaVVacuum, spent, left));
        }
        return new PourResult(covered, stagesPerPhase, uses);
    }

    /// <summary>The next phase from <paramref name="from"/> that still needs Δv (phases needing none are skipped).</summary>
    private static int NextPhase(IReadOnlyList<double> needs, double[] covered, int from)
    {
        while (from < needs.Count && needs[from] - covered[from] <= 1e-9) from++;
        return from;
    }
}
