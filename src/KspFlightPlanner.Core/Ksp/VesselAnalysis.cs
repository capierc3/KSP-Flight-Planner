namespace KspFlightPlanner.Core.Ksp;

/// <summary>How much of one resource a vessel carries.</summary>
public sealed record ResourceTotal(string Name, string DisplayName, double Amount, double MaxAmount, double MassT)
{
    public double Fraction => MaxAmount > 0 ? Amount / MaxAmount : 0;
}

/// <summary>Engines of one kind on a vessel (e.g. 3 × Ant).</summary>
public sealed record EngineGroup(string Title, int Count, double MaxThrustKn, double ThrustLimitPercent, double IspVacuumS,
    double IspSeaLevelS, IReadOnlyList<Propellant> Propellants, bool CountedForDeltaV);

/// <summary>Mass, fuel and Δv of a vessel as it is in the save (docs/MATH.md §16).</summary>
public sealed record VesselReport(
    double DryMassT,
    double WetMassT,
    IReadOnlyList<ResourceTotal> Resources,
    IReadOnlyList<EngineGroup> Engines,
    double DeltaVVacuum,
    double EffectiveIspS,
    double ThrustKn,
    double FullThrustKn,
    double BurnTimeS,
    IReadOnlyList<(string Title, int Count)> Parts,
    IReadOnlyList<string> UnknownParts,
    IReadOnlyList<string> Notes)
{
    public double PropellantMassT => WetMassT - DryMassT - Resources.Where(r => !IsPropellant(r.Name)).Sum(r => r.MassT);

    /// <summary>Acceleration at the start of a burn with the thrust limiters as set (m/s²).</summary>
    public double Acceleration => WetMassT > 0 ? ThrustKn / WetMassT : 0;

    private bool IsPropellant(string resource) => Engines.Any(e => e.CountedForDeltaV && e.Propellants.Any(p => p.Name == resource));
}

/// <summary>
/// Works out a vessel's remaining Δv from its parts and what's left in its tanks, using part data from the catalog.
/// All counted engines fire together until the first propellant they need runs out; every tank is assumed to feed
/// every engine (no staging or fuel-line limits), which is how most vessels already in orbit are flown.
/// </summary>
public static class VesselAnalysis
{
    public const double G0 = 9.80665;

    /// <summary>Resources that don't run out (solar panels recharge) and have no mass, so never limit a burn.</summary>
    private static readonly HashSet<string> Renewable = ["ElectricCharge", "IntakeAir"];

    public static VesselReport Analyze(SavedVessel vessel, PartCatalog catalog)
    {
        var unknown = new List<string>();
        var notes = new List<string>();
        var dryMass = 0.0;
        var amounts = new Dictionary<string, (double Amount, double Max)>();
        var engines = new List<(KspPart Part, PartEngine Engine, double Limit)>();

        foreach (var part in vessel.Parts)
        {
            var definition = catalog.Part(part.Name);
            if (definition is null && !unknown.Contains(part.Name)) unknown.Add(part.Name);
            dryMass += double.IsFinite(part.MassT) && part.MassT > 0 ? part.MassT : definition?.MassT ?? 0;

            foreach (var r in part.Resources)
            {
                var (a, m) = amounts.GetValueOrDefault(r.Name);
                amounts[r.Name] = (a + r.Amount, m + r.MaxAmount);
            }

            if (definition is null || definition.Engines.Count == 0) continue;
            // Multi-mode engines only run one mode at a time; otherwise every engine module counts.
            var indexes = part.RunningPrimaryMode is { } primary ? [primary ? 0 : Math.Min(1, definition.Engines.Count - 1)]
                : Enumerable.Range(0, definition.Engines.Count);
            foreach (var i in indexes)
            {
                var limit = i < part.Engines.Count ? part.Engines[i].ThrustPercent : 100;
                engines.Add((definition, definition.Engines[i], double.IsFinite(limit) ? Math.Clamp(limit, 0, 100) : 100));
            }
        }

        var resources = amounts
            .Select(kv => new ResourceTotal(kv.Key, catalog.DisplayName(kv.Key), kv.Value.Amount, kv.Value.Max, kv.Value.Amount * catalog.Density(kv.Key)))
            .OrderByDescending(r => r.MassT).ThenBy(r => r.DisplayName).ToList();
        var wetMass = dryMass + resources.Sum(r => r.MassT);

        // Air-breathing engines don't work in a vacuum, and an engine with no thrust or Isp adds nothing.
        var usable = engines.Where(e => !e.Engine.BreathesAir && e.Engine.MaxThrustKn > 0 && e.Engine.IspVacuumS > 0).ToList();
        if (engines.Any(e => e.Engine.BreathesAir)) notes.Add("Jet engines are left out of the Δv: they need air.");

        var (deltaV, isp, thrust, fullThrust, burnTime) = Burn(usable, amounts, catalog, wetMass, notes);

        var groups = engines
            .GroupBy(e => (e.Part.Title, e.Engine, e.Limit))
            .Select(g => new EngineGroup(g.Key.Title, g.Count(), g.Key.Engine.MaxThrustKn, g.Key.Limit, g.Key.Engine.IspVacuumS,
                g.Key.Engine.IspSeaLevelS, g.Key.Engine.Propellants, usable.Contains(g.First())))
            .ToList();
        var partCounts = vessel.Parts
            .GroupBy(p => catalog.Part(p.Name)?.Title ?? p.Name)
            .Select(g => (g.Key, g.Count()))
            .OrderByDescending(p => p.Item2).ThenBy(p => p.Key).ToList();
        if (unknown.Count > 0)
            notes.Add($"{unknown.Count} part type(s) aren't in the part data (rescan it in Settings), so their engines and mass may be missing.");

        return new VesselReport(dryMass, wetMass, resources, groups, deltaV, isp, thrust, fullThrust, burnTime, partCounts, unknown, notes);
    }

    /// <summary>
    /// All engines burning together. Each engine's mass flow is F / (Isp·g₀), split across its propellants by mass;
    /// the burn lasts until the first propellant is used up. Δv = Isp_eff·g₀·ln(m₀ / m₁) with Isp_eff = ΣF / (g₀·Σṁ).
    /// Thrust limiters change how long the burn takes and, with mixed engines, the blend of Isp.
    /// </summary>
    private static (double DeltaV, double Isp, double Thrust, double FullThrust, double BurnTime) Burn(
        IReadOnlyList<(KspPart Part, PartEngine Engine, double Limit)> engines, Dictionary<string, (double Amount, double Max)> amounts,
        PartCatalog catalog, double wetMass, List<string> notes)
    {
        if (engines.Count == 0) return (0, 0, 0, 0, 0);

        // With every limiter at 0 the engines still have a full-thrust rating; use it so Isp blending still works.
        var limited = engines.Any(e => e.Limit > 0);
        double Thrust((KspPart Part, PartEngine Engine, double Limit) e) => e.Engine.MaxThrustKn * (limited ? e.Limit / 100 : 1);

        var massFlow = 0.0;                         // t/s
        var unitsPerSecond = new Dictionary<string, double>();
        foreach (var e in engines)
        {
            var flow = Thrust(e) / (e.Engine.IspVacuumS * G0); // kN / (m/s) = t/s
            massFlow += flow;
            var mix = e.Engine.Propellants.Where(p => !Renewable.Contains(p.Name)).ToList();
            var mixMassPerVolume = mix.Sum(p => p.Ratio * catalog.Density(p.Name));
            if (mixMassPerVolume <= 0) continue;
            foreach (var p in mix)
                unitsPerSecond[p.Name] = unitsPerSecond.GetValueOrDefault(p.Name) + flow * p.Ratio / mixMassPerVolume;
        }
        var thrust = engines.Sum(Thrust);
        var fullThrust = engines.Sum(e => e.Engine.MaxThrustKn);
        if (massFlow <= 0 || unitsPerSecond.Count == 0) return (0, 0, thrust, fullThrust, 0);

        var limiting = unitsPerSecond.MinBy(kv => amounts.GetValueOrDefault(kv.Key).Amount / kv.Value);
        var burnTime = amounts.GetValueOrDefault(limiting.Key).Amount / limiting.Value;
        var burned = Math.Min(massFlow * burnTime, wetMass);
        var isp = thrust / (G0 * massFlow);
        var deltaV = burned > 0 && burned < wetMass ? isp * G0 * Math.Log(wetMass / (wetMass - burned)) : 0;

        if (burnTime > 0)
            foreach (var (name, rate) in unitsPerSecond.Where(kv => kv.Key != limiting.Key))
            {
                var left = amounts.GetValueOrDefault(name).Amount - rate * burnTime;
                if (left > 0.01 * amounts.GetValueOrDefault(name).Max && left > 0.05)
                    notes.Add($"{catalog.DisplayName(name)} runs out last: {left:N1} units are left when {catalog.DisplayName(limiting.Key)} is used up.");
            }
        if (!limited) notes.Add("Every thrust limiter is at 0 %: burn time assumes full thrust.");
        return (deltaV, isp, thrust, fullThrust, burnTime);
    }
}
