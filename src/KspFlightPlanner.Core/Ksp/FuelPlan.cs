namespace KspFlightPlanner.Core.Ksp;

/// <summary>An amount of one propellant: KSP's units (what its tank windows show) and mass.</summary>
public sealed record FuelAmount(string Resource, string DisplayName, double Units, double MassT);

/// <summary>The propellant one phase of a mission burns, by resource.</summary>
public sealed record PhaseFuel(int Phase, double MassT, IReadOnlyList<FuelAmount> Fuel);

/// <summary>One propellant for a refuel: what's aboard, what the trip burns, what the tanks hold, and what to add.</summary>
public sealed record RefuelLine(string Resource, string DisplayName, double Have, double Need, double Capacity)
{
    /// <summary>Units to pump in before leaving (0 when there's enough).</summary>
    public double Add => Math.Max(0, Need - Have);

    /// <summary>The tanks hold enough for the trip when full.</summary>
    public bool Fits => Need <= Capacity + 1e-6;
}

/// <summary>A craft's refuel for a trip: its propellants, and the propellant mass the trip burns.</summary>
public sealed record Refuel(IReadOnlyList<RefuelLine> Lines, double NeedMassT)
{
    public bool NeedsFuel => Lines.Any(l => l.Add > 0.5);
    public bool Fits => Lines.All(l => l.Fits);
}

/// <summary>
/// Δv as fuel: how much of each propellant a phase burns, and what a craft already in flight needs pumped in for a trip
/// (a shuttle refuelling at a station). Mass burned for a Δv from mass m is m·(1 − e^(−Δv / (Isp·g₀))); it splits across
/// the propellants as the engines draw them. docs/MATH.md §7.9.
/// </summary>
public static class FuelPlan
{
    /// <summary>
    /// A craft in flight as a one-stage design, so it can be checked against a mission like one (<see cref="DesignCheck"/>):
    /// its Δv with what's in its tanks now, the propellant that burns, and its thrust.
    /// </summary>
    /// <param name="surfaceGravity">For the TWR figures (Kerbin's, as for designs).</param>
    public static DesignReport AsDesign(SavedVessel vessel, VesselReport report, PartCatalog catalog, double surfaceGravity)
    {
        var exhaust = report.EffectiveIspS * VesselAnalysis.G0;
        var stages = new List<StageResult>();
        if (report.DeltaVVacuum > 0 && exhaust > 0)
        {
            var end = report.WetMassT * Math.Exp(-report.DeltaVVacuum / exhaust);
            var thrust = report.ThrustKn;
            stages.Add(new StageResult(0, report.WetMassT, end, report.DeltaVVacuum, report.DeltaVVacuum, report.BurnTimeS,
                thrust, thrust, thrust / (report.WetMassT * surfaceGravity), thrust / (report.WetMassT * surfaceGravity),
                report.Engines.Where(e => e.CountedForDeltaV).Select(e => (e.Title, e.Count)).ToList())
            {
                Burned = Split(report, catalog, report.WetMassT - end).ToDictionary(f => f.Resource, f => f.Units),
            });
        }
        return new DesignReport(vessel.Name, "In flight", vessel.Parts.Count, report.WetMassT, report.DryMassT, stages, report.Resources,
            report.Parts, report.UnknownParts, report.Notes);
    }

    /// <summary>
    /// The propellant each phase burns, from how <paramref name="check"/> pours the design's stages into the phases: within a
    /// stage, each phase's share burns from the mass the one before leaves, at the stage's own exhaust speed, and takes the
    /// stage's mix of propellants. Phases no stage reaches burn nothing here (they're short).
    /// </summary>
    public static IReadOnlyList<PhaseFuel> PerPhase(DesignReport design, DesignCheckResult check, PartCatalog catalog)
    {
        var mass = new double[check.Phases.Count];
        var units = Enumerable.Range(0, check.Phases.Count).Select(_ => new Dictionary<string, double>()).ToArray();
        foreach (var use in check.Stages)
        {
            if (design.Stages.FirstOrDefault(s => s.Stage == use.Stage) is not { } stage) continue;
            var burnedT = stage.StartMassT - stage.EndMassT;
            if (burnedT <= 0 || stage.DeltaVVacuum <= 0) continue;
            var exhaust = stage.DeltaVVacuum / Math.Log(stage.StartMassT / stage.EndMassT);
            var m = stage.StartMassT;
            foreach (var (phase, dv) in use.Phases)
            {
                if (phase < 0 || phase >= mass.Length || dv <= 0) continue;
                var burned = m * (1 - Math.Exp(-dv / exhaust));
                m -= burned;
                mass[phase] += burned;
                foreach (var (resource, stageUnits) in stage.Burned)
                    units[phase][resource] = units[phase].GetValueOrDefault(resource) + stageUnits * burned / burnedT;
            }
        }
        return Enumerable.Range(0, mass.Length).Select(i => new PhaseFuel(i, mass[i],
            units[i].Select(kv => new FuelAmount(kv.Key, catalog.DisplayName(kv.Key), kv.Value, kv.Value * catalog.Density(kv.Key)))
                .OrderBy(f => f.DisplayName, StringComparer.Ordinal).ToList())).ToList();
    }

    /// <summary>
    /// What a craft in flight burns for <paramref name="deltaV"/> and what to pump in first. Everything that isn't propellant
    /// (the craft, crew, monopropellant, ore…) stays aboard, so the trip burns m_empty·(e^(Δv / (Isp·g₀)) − 1); the craft
    /// leaves with exactly that, plus whatever is left over after. Null when it has no engine that works in a vacuum.
    /// </summary>
    public static Refuel? ForTrip(VesselReport report, PartCatalog catalog, double deltaV)
    {
        var exhaust = report.EffectiveIspS * VesselAnalysis.G0;
        if (exhaust <= 0 || report.Engines.All(e => !e.CountedForDeltaV)) return null;
        var empty = report.WetMassT - report.PropellantMassT;
        var needT = empty * (Math.Exp(Math.Max(0, deltaV) / exhaust) - 1);
        var lines = Split(report, catalog, needT).Select(f =>
        {
            var aboard = report.Resources.FirstOrDefault(r => r.Name == f.Resource);
            return new RefuelLine(f.Resource, f.DisplayName, aboard?.Amount ?? 0, f.Units, aboard?.MaxAmount ?? 0);
        }).ToList();
        return new Refuel(lines, needT);
    }

    /// <summary>
    /// <paramref name="massT"/> of propellant split the way the craft's engines draw it: each engine's mass flow
    /// F / (Isp·g₀), shared across its propellants by mass (ratio × density).
    /// </summary>
    private static IEnumerable<FuelAmount> Split(VesselReport report, PartCatalog catalog, double massT)
    {
        var share = new Dictionary<string, double>();
        foreach (var e in report.Engines.Where(e => e.CountedForDeltaV && e.IspVacuumS > 0))
        {
            var thrust = e.MaxThrustKn * e.Count * (e.ThrustLimitPercent > 0 ? e.ThrustLimitPercent / 100 : 1);
            var flow = thrust / (e.IspVacuumS * VesselAnalysis.G0);
            var mix = e.Propellants.Where(p => catalog.Density(p.Name) > 0).ToList();
            var mixDensity = mix.Sum(p => p.Ratio * catalog.Density(p.Name));
            if (mixDensity <= 0) continue;
            foreach (var p in mix) share[p.Name] = share.GetValueOrDefault(p.Name) + flow * p.Ratio * catalog.Density(p.Name) / mixDensity;
        }
        var total = share.Values.Sum();
        if (total <= 0) yield break;
        foreach (var (name, flow) in share.OrderBy(kv => catalog.DisplayName(kv.Key), StringComparer.Ordinal)) // Liquid Fuel before Oxidizer, as KSP lists them
        {
            var t = massT * flow / total;
            yield return new FuelAmount(name, catalog.DisplayName(name), t / catalog.Density(name), t);
        }
    }
}
