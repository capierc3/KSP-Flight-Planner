namespace KspFlightPlanner.Core.Ksp;

/// <summary>What the engine chart picks the best engine by.</summary>
public enum EngineChartGoal { LightestStage, CheapestStage }

/// <summary>
/// The stage the engine chart sizes: a payload to push, the Δv it needs, and the thrust it must have where it fires.
/// </summary>
/// <param name="PayloadT">Everything above the stage (the stages after it, the probe, the lander…).</param>
/// <param name="DeltaV">Vacuum Δv the stage must give (the Δv map's figures already include losses).</param>
/// <param name="MinTwr">The least TWR the stage may start with (full tanks).</param>
/// <param name="SurfaceGravity">Gravity the TWR is measured against (Kerbin's 9.81 for "in space").</param>
/// <param name="PressureAtm">Air pressure where it fires: engines lose thrust with it (0 in vacuum).</param>
/// <param name="MaxEngines">The most engines of one kind the stage may use.</param>
public sealed record StageNeed(double PayloadT, double DeltaV, double MinTwr, double SurfaceGravity = 9.81, double PressureAtm = 0,
    int MaxEngines = 4);

/// <summary>Tanks for an engine's propellant: their dry mass and cost per tonne of propellant they hold.</summary>
/// <param name="DryPerTonne">Tank dry mass per tonne of propellant (stock liquid fuel + oxidizer tanks: 1/8).</param>
/// <param name="CostPerTonne">Cost of the tank, full, per tonne of propellant.</param>
/// <param name="Tank">The tank the figures come from.</param>
public sealed record TankModel(double DryPerTonne, double CostPerTonne, KspPart Tank);

/// <summary>One engine sized for a <see cref="StageNeed"/>: how many, how much propellant and tankage, and what it all weighs and costs.</summary>
/// <param name="Mode">The engine mode used (multi-mode engines like the R.A.P.I.E.R. have a rocket mode).</param>
/// <param name="TotalMassT">The whole stage with the payload, full, at ignition.</param>
/// <param name="Twr">TWR at ignition, at the need's gravity and pressure.</param>
/// <param name="Cost">Engines plus tanks, full (the payload's cost isn't included).</param>
/// <param name="MaxDeltaV">The most Δv this many engines can give the payload (with endless tanks for liquid engines).</param>
public sealed record EngineOption(KspPart Engine, PartEngine Mode, int Count, bool IsSolid, double PropellantT, double TankDryT,
    double TotalMassT, double Twr, double Cost, double MaxDeltaV, TankModel? Tanks)
{
    /// <summary>Mass of the stage itself: everything but the payload.</summary>
    public double StageMassT(double payloadT) => TotalMassT - payloadT;
}

/// <summary>
/// The engine chart: for a payload and the Δv it needs, the lightest (or cheapest) stage each engine makes, and which
/// engine wins (docs/MATH.md §7.6). Liquid engines get as much tankage as they need (tank dry mass from the best
/// tank for their propellant); solid boosters come with their fuel, so only their number can change.
/// </summary>
public sealed class EngineChart
{
    private readonly IReadOnlyList<Candidate> _candidates;

    private sealed record Candidate(KspPart Engine, PartEngine Mode, TankModel? Tanks, double OwnPropellantT, bool IsSolid);

    /// <param name="engines">The engines it may use (rocket engines; jets and engines without a rocket mode are skipped).</param>
    /// <param name="tanks">The tanks it may use for liquid engines' propellant.</param>
    public EngineChart(IEnumerable<KspPart> engines, IEnumerable<KspPart> tanks, PartCatalog catalog)
    {
        var tankList = tanks.ToList();
        var candidates = new List<Candidate>();
        foreach (var engine in engines)
            foreach (var mode in engine.Engines.Where(m => !m.BreathesAir && m.MaxThrustKn > 0 && m.IspVacuumS > 0))
            {
                var burns = Burns(mode);
                if (burns.Count == 0) continue;
                // Propellant the engine part carries itself (solid boosters; the Twin-Boar's tank).
                var own = engine.Resources.Where(r => burns.Contains(r.Name)).ToList();
                var ownMass = own.Sum(r => r.MaxAmount * catalog.Density(r.Name));
                var solid = burns.Count == 1 && catalog.Flow(burns.First()) == ResourceFlow.OwnPart;
                var model = solid ? null : TankFor(mode, tankList, catalog);
                if (solid ? ownMass <= 0 : model is null && ownMass <= 0) continue;
                candidates.Add(new Candidate(engine, mode, model, ownMass, solid));
            }
        _candidates = candidates;
    }

    /// <summary>The engines the chart can choose from, in no particular order.</summary>
    public IReadOnlyList<KspPart> Engines => _candidates.Select(c => c.Engine).Distinct().ToList();

    /// <summary>Each engine's best stage for the need (the count that does best on the goal), best first; engines that can't do it are left out.</summary>
    public IReadOnlyList<EngineOption> Options(StageNeed need, EngineChartGoal goal = EngineChartGoal.LightestStage) =>
        _candidates.Select(c => BestCount(c, need, goal)).OfType<EngineOption>()
            .GroupBy(o => o.Engine.Name).Select(g => g.MinBy(o => Score(o, goal))!)
            .OrderBy(o => Score(o, goal)).ThenBy(o => o.Engine.Title).ToList();

    /// <summary>The winning engine for the need, or null if none can do it.</summary>
    public EngineOption? Best(StageNeed need, EngineChartGoal goal = EngineChartGoal.LightestStage)
    {
        EngineOption? best = null;
        foreach (var c in _candidates)
            if (BestCount(c, need, goal) is { } option && (best is null || Score(option, goal) < Score(best, goal)))
                best = option;
        return best;
    }

    /// <summary>
    /// The tank that holds an engine's propellant with the least dry mass per tonne: one whose resources are exactly
    /// what the engine burns (renewables aside), in the ratio it burns them. Null when there's none.
    /// </summary>
    public static TankModel? TankFor(PartEngine mode, IEnumerable<KspPart> tanks, PartCatalog catalog)
    {
        var burns = mode.Propellants.Where(p => !IsRenewable(p.Name)).ToList();
        if (burns.Count == 0) return null;
        TankModel? best = null;
        foreach (var tank in tanks)
        {
            if (tank.Engines.Count > 0 || tank.Resources.Count != burns.Count) continue;
            if (!burns.All(b => tank.Resources.Any(r => r.Name == b.Name))) continue;
            // Same mix: amounts in proportion to the engine's ratios (LF 0.9 : Ox 1.1 ↔ 90 : 110 units).
            var scale = tank.Resources.First(r => r.Name == burns[0].Name).MaxAmount / burns[0].Ratio;
            if (scale <= 0 || burns.Any(b => Math.Abs(tank.Resources.First(r => r.Name == b.Name).MaxAmount - b.Ratio * scale) > 0.01 * b.Ratio * scale)) continue;
            var propellant = tank.Resources.Sum(r => r.MaxAmount * catalog.Density(r.Name));
            if (propellant <= 0) continue;
            var model = new TankModel(tank.MassT / propellant, tank.Cost / propellant, tank);
            if (best is null || model.DryPerTonne < best.DryPerTonne - 1e-12
                || Math.Abs(model.DryPerTonne - best.DryPerTonne) <= 1e-12 && model.CostPerTonne < best.CostPerTonne)
                best = model;
        }
        return best;
    }

    /// <summary>Thrust at a pressure (§7.4): vacuum thrust, falling with Isp as pressure rises; never below 0.</summary>
    public static double ThrustAt(PartEngine mode, double pressureAtm)
    {
        var seaLevel = mode.MaxThrustKn * mode.IspSeaLevelS / mode.IspVacuumS;
        return Math.Max(0, mode.MaxThrustKn + (seaLevel - mode.MaxThrustKn) * Math.Max(0, pressureAtm));
    }

    private static EngineOption? BestCount(Candidate c, StageNeed need, EngineChartGoal goal)
    {
        EngineOption? best = null;
        for (var n = 1; n <= Math.Max(1, need.MaxEngines); n++)
            if (Size(c, n, need) is { } option && option.Twr >= need.MinTwr && (best is null || Score(option, goal) < Score(best, goal)))
                best = option;
        return best;
    }

    /// <summary>n engines of a kind sized for the need (§7.6), or null if they can't give the Δv.</summary>
    private static EngineOption? Size(Candidate c, int n, StageNeed need)
    {
        var ve = c.Mode.IspVacuumS * VesselAnalysis.G0;
        var r = Math.Exp(need.DeltaV / ve) - 1; // propellant ÷ burnout mass
        var payload = need.PayloadT;
        var engines = n * c.Engine.MassT;
        var own = n * c.OwnPropellantT;
        double propellant, tankDry, cost, maxDv;

        if (c.IsSolid)
        {
            // Boosters burn all they carry: Δv = ve · ln((P + n·m_full) / (P + n·m_dry)).
            maxDv = ve * Math.Log((payload + engines + own) / (payload + engines));
            if (maxDv < need.DeltaV) return null;
            propellant = own;
            tankDry = 0;
            cost = n * c.Engine.Cost;
        }
        else
        {
            var k = c.Tanks?.DryPerTonne ?? double.PositiveInfinity;
            // With no tanks needed the engines' own propellant is enough: m_p = r · (P + n·m_e).
            propellant = r * (payload + engines);
            if (propellant > own)
            {
                if (c.Tanks is null || r * k >= 1) return null; // tanks heavier than the Δv they add
                // m_p = r · (P + n·m_e + k·(m_p − own))  →  m_p = r · (P + n·m_e − k·own) / (1 − r·k)
                propellant = r * (payload + engines - k * own) / (1 - r * k);
            }
            tankDry = c.Tanks is null ? 0 : c.Tanks.DryPerTonne * Math.Max(0, propellant - own);
            cost = n * c.Engine.Cost + (c.Tanks?.CostPerTonne ?? 0) * Math.Max(0, propellant - own);
            maxDv = c.Tanks is null ? ve * Math.Log((payload + engines + own) / (payload + engines))
                : ve * Math.Log(1 + 1 / c.Tanks.DryPerTonne);
        }

        var total = payload + engines + tankDry + propellant;
        var twr = n * ThrustAt(c.Mode, need.PressureAtm) / (total * need.SurfaceGravity);
        return new EngineOption(c.Engine, c.Mode, n, c.IsSolid, propellant, tankDry, total, twr, cost, maxDv, c.Tanks);
    }

    private static double Score(EngineOption o, EngineChartGoal goal) => goal == EngineChartGoal.CheapestStage ? o.Cost : o.TotalMassT;

    private static HashSet<string> Burns(PartEngine mode) => mode.Propellants.Where(p => !IsRenewable(p.Name)).Select(p => p.Name).ToHashSet();

    // Made as the engine runs (solar panels, generators), so not carried as propellant.
    private static bool IsRenewable(string resource) => resource is "ElectricCharge" or "IntakeAir";
}
