using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core.Ksp;

/// <summary>Where a craft is, for its power: how far from the Sun, and how long its nights and days last.</summary>
/// <param name="SunDistanceM">From the Sun (the planet's semi-major axis, for a moon its planet's).</param>
/// <param name="NightS">The longest time without sunlight.</param>
/// <param name="DayS">The sunlit time between two nights.</param>
public sealed record PowerSituation(string Where, double SunDistanceM, double NightS, double DayS);

/// <summary>One thing that makes or uses charge on the craft, all of that kind together.</summary>
/// <param name="EcPerSecond">For all of them (positive whether made or used).</param>
/// <param name="IsOn">Counted in the budget (loads switched off, fuel cells left off, aren't).</param>
public sealed record PowerLine(string Title, string Kind, int Count, double EcPerSecond, bool Makes, bool IsOn, bool InSunOnly);

/// <summary>A craft's power over its longest night and the day that follows (docs/MATH.md §7.8).</summary>
/// <param name="MadeInSunEcS">Solar panels (at this distance) plus generators and fuel cells.</param>
/// <param name="MadeInDarkEcS">Generators, plus fuel cells up to what they can make.</param>
/// <param name="BatteryNeededEc">What the batteries must hold to last the night.</param>
/// <param name="BatteryLastsS">How long full batteries last in the dark; null if the dark doesn't drain them.</param>
/// <param name="RechargeS">How long the day's surplus takes to fill what the night used; null without a surplus.</param>
/// <param name="FuelPerNight">Propellant the fuel cells burn over a night, units of each.</param>
public sealed record PowerBudgetResult(
    IReadOnlyList<PowerLine> Lines,
    double MadeInSunEcS,
    double MadeInDarkEcS,
    double UsedEcS,
    double BatteryEc,
    PowerSituation Situation,
    double BatteryNeededEc,
    double? BatteryLastsS,
    double? RechargeS,
    IReadOnlyList<Propellant> FuelPerNight)
{
    public double NetInSunEcS => MadeInSunEcS - UsedEcS;
    public double NetInDarkEcS => MadeInDarkEcS - UsedEcS;
    public bool LastsTheNight => BatteryNeededEc <= BatteryEc + 1e-9;

    /// <summary>The day refills what the night took (or the night took nothing).</summary>
    public bool RechargesInTheDay => BatteryNeededEc <= 1e-9 || RechargeS is { } t && t <= Situation.DayS;
}

/// <summary>
/// Electric charge on a craft: what its panels, generators and fuel cells make, what its parts use, and whether
/// its batteries see it through the longest night (docs/MATH.md §7.8).
/// </summary>
public static class PowerBudget
{
    private const string Charge = "ElectricCharge";

    /// <summary>Kinds of load that are on unless switched off: keeping control of the craft.</summary>
    public static readonly IReadOnlySet<PowerKind> AlwaysOn = new HashSet<PowerKind> { PowerKind.Command };

    /// <summary>Solar output scales with the inverse square of the distance from the Sun (rated at home's distance).</summary>
    public static double SolarScale(double sunDistanceM, double homeSunDistanceM) =>
        sunDistanceM > 0 ? Math.Pow(homeSunDistanceM / sunDistanceM, 2) : 0;

    /// <summary>
    /// In a circular orbit at an altitude: the longest pass through the body's shadow (the Sun in the orbit's plane,
    /// the shadow a cylinder as wide as the body): t = T · asin(R / a) / π.
    /// </summary>
    public static PowerSituation InOrbit(CelestialBody body, double altitudeM, IReadOnlyCollection<CelestialBody> system)
    {
        var a = body.EquatorialRadiusM + altitudeM;
        var period = 2 * Math.PI * Math.Sqrt(a * a * a / body.Mu);
        var night = period * Math.Asin(Math.Min(1, body.EquatorialRadiusM / a)) / Math.PI;
        return new PowerSituation($"{body.Name} orbit at {altitudeM / 1000:0.#} km", SunDistance(body, system), night, period - night);
    }

    /// <summary>
    /// On the surface at the equator: night is half a solar day. The solar day is the rotation measured against the
    /// Sun, which the planet moves around: S = P · Y / (Y − P) (Y the planet's year; a moon uses its planet's).
    /// </summary>
    public static PowerSituation OnSurface(CelestialBody body, IReadOnlyCollection<CelestialBody> system)
    {
        var rotation = body.RotationPeriodS;
        var year = YearOf(body, system);
        var solarDay = rotation == 0 ? double.PositiveInfinity
            : double.IsFinite(year) && year > 0
                ? rotation > 0 ? rotation * year / (year - rotation) : -rotation * year / (year - rotation)
                : Math.Abs(rotation);
        return new PowerSituation($"{body.Name} surface (equator)", SunDistance(body, system), solarDay / 2, solarDay / 2);
    }

    /// <summary>Electric charge an engine uses at full thrust (ion engines burn it as a propellant).</summary>
    public static double EngineEcPerSecond(PartEngine engine, PartCatalog catalog)
    {
        var charge = engine.Propellants.FirstOrDefault(p => p.Name == Charge);
        if (charge is null || engine.IspVacuumS <= 0) return 0;
        var massFlow = engine.MaxThrustKn / (engine.IspVacuumS * VesselAnalysis.G0); // t/s
        var mixDensity = engine.Propellants.Sum(p => p.Ratio * catalog.Density(p.Name));
        return mixDensity > 0 ? massFlow * charge.Ratio / mixDensity : 0;
    }

    /// <param name="parts">The craft's parts and how many of each.</param>
    /// <param name="on">Loads switched on (see <see cref="AlwaysOn"/>); "Engine" for ion engines.</param>
    /// <param name="fuelCells">Run the fuel cells when the panels can't keep up.</param>
    public static PowerBudgetResult Work(IEnumerable<(KspPart Part, int Count)> parts, PartCatalog catalog, PowerSituation situation,
        double homeSunDistanceM, IReadOnlySet<string> on, bool fuelCells)
    {
        var list = parts.Where(p => p.Count > 0).ToList();
        var lines = new List<PowerLine>();
        var solar = SolarScale(situation.SunDistanceM, homeSunDistanceM);

        foreach (var (part, count) in list)
        {
            foreach (var group in part.PowerModules.GroupBy(m => m.Kind))
            {
                var kind = group.Key;
                // A part runs one converter mode at a time; the rest of its modules of a kind add up.
                var each = kind == PowerKind.Converter ? group.Max(m => m.EcPerSecond) : group.Sum(m => m.EcPerSecond);
                if (kind == PowerKind.Antenna) continue; // only one antenna transmits at a time: added once below
                var makes = group.First().Makes;
                var rate = kind == PowerKind.Solar ? each * solar : each;
                var isOn = makes ? kind != PowerKind.FuelCell || fuelCells : on.Contains(kind.ToString());
                lines.Add(new PowerLine(part.Title, kind.ToString(), count, rate * count, makes, isOn, kind == PowerKind.Solar));
            }
            var engine = part.Engines.Select(e => EngineEcPerSecond(e, catalog)).DefaultIfEmpty(0).Max();
            if (engine > 0) lines.Add(new PowerLine(part.Title, "Engine", count, engine * count, false, on.Contains("Engine"), false));
        }

        // Transmitting: the craft sends through one antenna, the hungriest it would pick being the worst case.
        var antenna = list.SelectMany(p => p.Part.PowerModules.Where(m => m.Kind == PowerKind.Antenna).Select(m => (p.Part, m.EcPerSecond)))
            .OrderByDescending(x => x.EcPerSecond).FirstOrDefault();
        if (antenna.Part is not null)
            lines.Add(new PowerLine(antenna.Part.Title, nameof(PowerKind.Antenna), 1, antenna.EcPerSecond, false, on.Contains(nameof(PowerKind.Antenna)), false));

        var used = lines.Where(l => !l.Makes && l.IsOn).Sum(l => l.EcPerSecond);
        var generators = lines.Where(l => l.Makes && l.IsOn && l.Kind == nameof(PowerKind.Generator)).Sum(l => l.EcPerSecond);
        var panels = lines.Where(l => l.Makes && l.IsOn && l.Kind == nameof(PowerKind.Solar)).Sum(l => l.EcPerSecond);
        var cells = lines.Where(l => l.Makes && l.IsOn && l.Kind == nameof(PowerKind.FuelCell)).Sum(l => l.EcPerSecond);

        // Fuel cells only cover what the rest can't (KSP stops them when the batteries are nearly full).
        var cellsInSun = Math.Min(cells, Math.Max(0, used - generators - panels));
        var cellsInDark = Math.Min(cells, Math.Max(0, used - generators));
        var madeInSun = panels + generators + cellsInSun;
        var madeInDark = generators + cellsInDark;
        var battery = list.Sum(p => p.Count * p.Part.Resources.Where(r => r.Name == Charge).Sum(r => r.MaxAmount));

        var deficit = Math.Max(0, used - madeInDark);
        var needed = deficit * situation.NightS;
        double? lasts = deficit > 0 ? battery / deficit : null;
        var surplus = madeInSun - used;
        double? recharge = needed <= 0 ? 0 : surplus > 0 ? needed / surplus : null;

        // Fuel burned over a night by the cells, in proportion to the charge they make.
        var fuel = new Dictionary<string, double>();
        if (cells > 0 && cellsInDark > 0)
            foreach (var (part, count) in list)
                foreach (var cell in part.PowerModules.Where(m => m.Kind == PowerKind.FuelCell && m.Inputs is not null))
                    foreach (var input in cell.Inputs!)
                        fuel[input.Name] = fuel.GetValueOrDefault(input.Name) + input.Ratio * count * (cellsInDark / cells) * situation.NightS;

        return new PowerBudgetResult(lines, madeInSun, madeInDark, used, battery, situation, needed, lasts, recharge,
            fuel.Select(f => new Propellant(f.Key, f.Value)).ToList());
    }

    /// <summary>From the Sun: the semi-major axis of the body (or the planet it belongs to) around the star.</summary>
    public static double SunDistance(CelestialBody body, IReadOnlyCollection<CelestialBody> system) =>
        PlanetOf(body, system)?.Orbit?.SemiMajorAxisM ?? 0;

    /// <summary>The planet's year: its orbital period around the star.</summary>
    private static double YearOf(CelestialBody body, IReadOnlyCollection<CelestialBody> system)
    {
        var planet = PlanetOf(body, system);
        var star = system.FirstOrDefault(b => b.ParentId is null);
        if (planet?.Orbit is not { } orbit || star is null || star.Mu <= 0) return double.PositiveInfinity;
        var a = orbit.SemiMajorAxisM;
        return 2 * Math.PI * Math.Sqrt(a * a * a / star.Mu);
    }

    /// <summary>The body, or its parent, … that orbits the star directly; null for the star.</summary>
    private static CelestialBody? PlanetOf(CelestialBody body, IReadOnlyCollection<CelestialBody> system)
    {
        var byId = system.ToDictionary(b => b.Id);
        var b = body;
        for (var i = 0; i < 10 && b.ParentId is { } parentId && byId.TryGetValue(parentId, out var parent); i++)
        {
            if (parent.ParentId is null) return b;
            b = parent;
        }
        return null;
    }
}
