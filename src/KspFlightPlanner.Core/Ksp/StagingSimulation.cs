namespace KspFlightPlanner.Core.Ksp;

/// <summary>One stage of a design: what it starts and ends at, the Δv it gives and how hard it pushes.</summary>
/// <param name="Stage">KSP's stage number (the first to fire has the highest number).</param>
/// <param name="TwrVacuum">Thrust-to-weight at the start of the stage, weight measured at the reference body's surface.</param>
public sealed record StageResult(
    int Stage,
    double StartMassT,
    double EndMassT,
    double DeltaVVacuum,
    double DeltaVSeaLevel,
    double BurnTimeS,
    double ThrustVacuumKn,
    double ThrustSeaLevelKn,
    double TwrVacuum,
    double TwrSeaLevel,
    IReadOnlyList<(string Title, int Count)> Engines);

/// <summary>Everything worked out for a saved design (docs/MATH.md §7.2).</summary>
/// <param name="LaunchMassT">Mass when the first stage lights (launch clamps excluded, as KSP does).</param>
public sealed record DesignReport(
    string Name,
    string Facility,
    int PartCount,
    double LaunchMassT,
    double DryMassT,
    IReadOnlyList<StageResult> Stages,
    IReadOnlyList<ResourceTotal> Resources,
    IReadOnlyList<(string Title, int Count)> Parts,
    IReadOnlyList<string> UnknownParts,
    IReadOnlyList<string> Notes)
{
    public double TotalDeltaVVacuum => Stages.Sum(s => s.DeltaVVacuum);

    /// <summary>The first stage's TWR off the launch pad at sea level (0 when it has no engines).</summary>
    public double LaunchTwr => Stages.FirstOrDefault()?.TwrSeaLevel ?? 0;

    /// <summary>Ids of the craft's engine parts that burned propellant in some stage.</summary>
    public IReadOnlySet<string> FiredEngineIds { get; init; } = new HashSet<string>();
}

/// <summary>
/// Flies a design's staging the way KSP's stage Δv readout does: stage by stage from the first to fire, with only the
/// parts still attached, each running engine drawing from the tanks it can reach, until its propellant runs out.
/// <list type="bullet">
/// <item>Solid fuel stays in its own part; liquid fuel and oxidizer flow through parts that allow crossfeed (stack and
/// radial attachments, decouplers with crossfeed on) and along fuel lines; monopropellant and xenon reach the whole vessel.</item>
/// <item>Tanks that will be dropped soonest are drained first (asparagus staging, drop tanks); tanks at the same level drain together.</item>
/// <item>A stage ends when its engines run dry, or when everything the next stage drops (boosters, drop tanks) has
/// been in use and is now spent — so empty boosters are released rather than carried.</item>
/// <item>Sea-level figures keep the same fuel flow with the sea-level Isp, so thrust scales by Isp_ASL / Isp_vac.</item>
/// </list>
/// </summary>
public static class StagingSimulation
{
    private const double Empty = 1e-9;
    private static readonly HashSet<string> Renewable = ["ElectricCharge", "IntakeAir"];

    private sealed class SimPart(CraftPart craft, KspPart? definition)
    {
        public CraftPart Craft { get; } = craft;
        public KspPart? Definition { get; } = definition;
        public double DryMassT { get; } = (definition?.MassT ?? 0) + craft.ExtraMassT;
        public Dictionary<string, double> Amounts { get; } = craft.Resources.GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));
        public bool Crossfeed => Craft.CrossfeedSwitch ?? Definition?.FuelCrossfeed ?? true;
        public SimPart? Parent { get; set; }
        public double Mass(PartCatalog catalog) => DryMassT + Amounts.Sum(kv => kv.Value * catalog.Density(kv.Key));
    }

    private sealed record SimEngine(SimPart Part, PartEngine Engine, double Limit)
    {
        public double ThrustKn => Engine.MaxThrustKn * Limit / 100;
        public double ThrustSeaLevelKn => Engine.IspVacuumS > 0 ? ThrustKn * Engine.IspSeaLevelS / Engine.IspVacuumS : 0;
        public double MassFlow => Engine.IspVacuumS > 0 ? ThrustKn / (Engine.IspVacuumS * VesselAnalysis.G0) : 0; // t/s
    }

    /// <param name="surfaceGravity">For TWR: the launch body's surface gravity (Kerbin 9.81 m/s²).</param>
    public static DesignReport Simulate(CraftFile craft, PartCatalog catalog, double surfaceGravity = 9.81)
    {
        var notes = new List<string>();
        var unknown = craft.Parts.Select(p => p.Name).Where(n => catalog.Part(n) is null).Distinct().ToList();
        var parts = craft.Parts.Select(p => new SimPart(p, catalog.Part(p.Name))).ToList();
        var byId = parts.GroupBy(p => p.Craft.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var p in parts)
            foreach (var child in p.Craft.Children)
                if (byId.TryGetValue(child, out var c)) c.Parent = p;
        var bySuffix = parts.GroupBy(p => Suffix(p.Craft.Id)).ToDictionary(g => g.Key, g => g.First());

        var engines = new List<SimEngine>();
        foreach (var p in parts.Where(p => p.Definition is { Engines.Count: > 0 }))
        {
            var defs = p.Definition!.Engines;
            var indexes = p.Craft.RunningPrimaryMode is { } primary ? [primary ? 0 : Math.Min(1, defs.Count - 1)] : Enumerable.Range(0, defs.Count);
            foreach (var i in indexes)
            {
                if (defs[i].BreathesAir) continue;
                var limit = i < p.Craft.Engines.Count ? p.Craft.Engines[i].ThrustPercent : 100;
                engines.Add(new SimEngine(p, defs[i], double.IsFinite(limit) ? Math.Clamp(limit, 0, 100) : 100));
            }
        }
        if (parts.Any(p => p.Definition?.Engines.Any(e => e.BreathesAir) == true))
            notes.Add("Jet engines are left out: their thrust depends on speed and altitude, so they have no fixed Δv.");

        var resources = parts.SelectMany(p => p.Craft.Resources).GroupBy(r => r.Name)
            .Select(g => new ResourceTotal(g.Key, catalog.DisplayName(g.Key), g.Sum(r => r.Amount), g.Sum(r => r.MaxAmount),
                g.Sum(r => r.Amount) * catalog.Density(g.Key)))
            .OrderByDescending(r => r.MassT).ThenBy(r => r.DisplayName).ToList();
        var dryMass = parts.Sum(p => p.DryMassT);

        var stages = new List<StageResult>();
        var fired = new HashSet<string>();
        double? launchMass = null;
        for (var stage = craft.FirstStage; stage >= 0; stage--)
        {
            // Parts dropped by this stage's decouplers (or earlier ones) are gone; -1 means never dropped.
            var present = parts.Where(p => p.Craft.SeparationStage < stage).ToList();
            var presentSet = present.ToHashSet();
            var startMass = present.Sum(p => p.Mass(catalog));
            launchMass ??= startMass;
            var active = engines.Where(e => presentSet.Contains(e.Part) && e.Part.Craft.ActivationStage >= stage && e.ThrustKn > 0).ToList();
            if (active.Count == 0) continue;

            var groups = CrossfeedGroups(present, presentSet);
            var feeds = FuelLineFeeds(present, presentSet, bySuffix, groups);
            var thrust = active.Sum(e => e.ThrustKn);
            var thrustSeaLevel = active.Sum(e => e.ThrustSeaLevelKn);
            var nextDrop = present.Where(p => p.Craft.SeparationStage == stage - 1).ToHashSet();
            var result = Burn(active, present, groups, feeds, nextDrop, catalog, startMass, fired);

            if (result.DeltaVVacuum <= 0 && result.BurnTimeS <= 0) continue;
            stages.Add(new StageResult(stage, startMass, result.EndMass, result.DeltaVVacuum, result.DeltaVSeaLevel, result.BurnTimeS,
                thrust, thrustSeaLevel, thrust / (startMass * surfaceGravity), thrustSeaLevel / (startMass * surfaceGravity),
                active.GroupBy(e => e.Part.Definition!.Title).Select(g => (g.Key, g.Select(e => e.Part).Distinct().Count())).ToList()));
        }

        if (unknown.Count > 0)
            notes.Add($"{unknown.Count} part type(s) aren't in the part data (read it again in Settings), so their mass and engines are missing.");
        if (engines.Count > 0 && stages.Count == 0) notes.Add("No stage has an engine with fuel it can reach.");

        var partCounts = parts.GroupBy(p => p.Definition?.Title ?? p.Craft.Name).Select(g => (g.Key, g.Count()))
            .OrderByDescending(p => p.Item2).ThenBy(p => p.Key).ToList();
        return new DesignReport(craft.Name, craft.Facility, parts.Count, launchMass ?? parts.Sum(p => p.Mass(catalog)), dryMass, stages,
            resources, partCounts, unknown, notes) { FiredEngineIds = fired };
    }

    /// <summary>Parts that share liquid fuel: connected through attachments where both parts allow crossfeed.</summary>
    private static Dictionary<SimPart, int> CrossfeedGroups(List<SimPart> present, HashSet<SimPart> presentSet)
    {
        var index = present.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i);
        var root = Enumerable.Range(0, present.Count).ToArray(); // union-find
        int Find(int i)
        {
            while (root[i] != i) i = root[i] = root[root[i]];
            return i;
        }
        foreach (var p in present)
            if (p.Parent is { } parent && presentSet.Contains(parent) && p.Crossfeed && parent.Crossfeed)
                root[Find(index[p])] = Find(index[parent]);
        return present.ToDictionary(p => p, p => Find(index[p]));
    }

    /// <summary>For each group, the groups that feed it through fuel lines (source → target), followed transitively.</summary>
    private static Dictionary<int, HashSet<int>> FuelLineFeeds(List<SimPart> present, HashSet<SimPart> presentSet,
        Dictionary<string, SimPart> bySuffix, Dictionary<SimPart, int> groups)
    {
        var direct = new Dictionary<int, HashSet<int>>();
        foreach (var line in present.Where(p => p.Craft.FuelLineTarget is not null))
            if (line.Parent is { } source && presentSet.Contains(source)
                && bySuffix.TryGetValue(line.Craft.FuelLineTarget!, out var target) && presentSet.Contains(target))
            {
                if (!direct.TryGetValue(groups[target], out var set)) direct[groups[target]] = set = [];
                set.Add(groups[source]);
            }

        var all = new Dictionary<int, HashSet<int>>();
        foreach (var g in groups.Values.Distinct())
        {
            var reach = new HashSet<int> { g };
            var queue = new Queue<int>([g]);
            while (queue.Count > 0)
                foreach (var from in direct.GetValueOrDefault(queue.Dequeue()) ?? [])
                    if (reach.Add(from)) queue.Enqueue(from);
            all[g] = reach;
        }
        return all;
    }

    private static (double DeltaVVacuum, double DeltaVSeaLevel, double BurnTimeS, double EndMass) Burn(List<SimEngine> active,
        List<SimPart> present, Dictionary<SimPart, int> groups, Dictionary<int, HashSet<int>> feeds, HashSet<SimPart> nextDrop,
        PartCatalog catalog, double startMass, HashSet<string> fired)
    {
        // The tanks each engine can draw each propellant from, in drain order: soonest-dropped first.
        List<List<SimPart>> Tiers(SimEngine e, string resource)
        {
            IEnumerable<SimPart> reachable = catalog.Flow(resource) switch
            {
                ResourceFlow.OwnPart => [e.Part],
                ResourceFlow.WholeVessel => present,
                _ => present.Where(p => feeds[groups[e.Part]].Contains(groups[p])),
            };
            return reachable.Where(p => p.Amounts.ContainsKey(resource))
                .GroupBy(p => p.Craft.SeparationStage).OrderByDescending(g => g.Key).Select(g => g.ToList()).ToList();
        }
        var sources = active.ToDictionary(e => e, e => e.Engine.Propellants.Where(p => !Renewable.Contains(p.Name))
            .ToDictionary(p => p.Name, p => Tiers(e, p.Name)));

        double mass = startMass, dvVac = 0, dvSea = 0, time = 0;
        var running = active.ToList();
        var dropInUse = false; // whether the parts the next stage drops have had a running engine or a draining tank
        for (var step = 0; step < 10_000 && running.Count > 0; step++)
        {
            // An engine stops when any propellant it needs is gone from every tank it can reach.
            running.RemoveAll(e => sources[e].Count == 0 || sources[e].Any(kv => kv.Value.All(t => t.All(p => p.Amounts[kv.Key] <= Empty))));
            if (running.Count == 0) break;

            var rates = new Dictionary<(SimPart Part, string Resource), double>();
            var massFlow = 0.0;
            foreach (var e in running)
            {
                var mix = e.Engine.Propellants.Where(p => !Renewable.Contains(p.Name)).ToList();
                var mixDensity = mix.Sum(p => p.Ratio * catalog.Density(p.Name));
                if (mixDensity <= 0) continue;
                massFlow += e.MassFlow;
                foreach (var p in mix)
                {
                    var demand = e.MassFlow * p.Ratio / mixDensity; // units/s
                    var tier = sources[e][p.Name].First(t => t.Any(part => part.Amounts[p.Name] > Empty));
                    var total = tier.Sum(part => Math.Max(0, part.Amounts[p.Name]));
                    foreach (var part in tier.Where(part => part.Amounts[p.Name] > Empty))
                        rates[(part, p.Name)] = rates.GetValueOrDefault((part, p.Name)) + demand * part.Amounts[p.Name] / total;
                }
            }
            if (massFlow <= 0 || rates.Count == 0) break;

            // Spent boosters / drop tanks: stage here so the next stage releases them.
            var dropBusy = running.Any(e => nextDrop.Contains(e.Part)) || rates.Keys.Any(k => nextDrop.Contains(k.Part));
            if (dropInUse && !dropBusy) break;
            dropInUse |= dropBusy;

            var dt = rates.Min(kv => kv.Key.Part.Amounts[kv.Key.Resource] / kv.Value);
            if (dt > 0) fired.UnionWith(running.Select(e => e.Part.Craft.Id));
            foreach (var ((part, resource), rate) in rates)
                part.Amounts[resource] = Math.Max(0, part.Amounts[resource] - rate * dt);

            var burned = rates.Sum(kv => kv.Value * dt * catalog.Density(kv.Key.Resource));
            var next = mass - burned;
            if (next <= 0) break;
            var thrust = running.Sum(e => e.ThrustKn);
            var thrustSea = running.Sum(e => e.ThrustSeaLevelKn);
            var ln = Math.Log(mass / next);
            dvVac += thrust / massFlow * ln;    // Isp·g₀ = F / ṁ
            dvSea += thrustSea / massFlow * ln;
            time += dt;
            mass = next;
        }
        return (dvVac, dvSea, time, mass);
    }

    private static string Suffix(string id) => id[(id.LastIndexOf('_') + 1)..];
}
