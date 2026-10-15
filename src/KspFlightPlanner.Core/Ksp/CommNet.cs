using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core.Ksp;

/// <summary>
/// KSP's CommNet: how a vessel's antennas add up, how far two of them can talk, how strong the signal is at a
/// distance, and how far each body gets from home (docs/MATH.md §7.7).
/// </summary>
public static class CommNet
{
    /// <summary>The tracking station's power at levels 1, 2 and 3 (m), before the DSN modifier.</summary>
    public static readonly double[] TrackingStationPower = [2e9, 50e9, 250e9];

    public static double TrackingStation(int level, double dsnModifier = 1) =>
        TrackingStationPower[Math.Clamp(level, 1, TrackingStationPower.Length) - 1] * dsnModifier;

    /// <summary>
    /// A vessel's antenna power: the strongest antenna, boosted by the others that combine with it,
    /// P = P_max · (ΣP / P_max)^e, with e the power-weighted average of their combinability exponents. An antenna that
    /// doesn't combine counts on its own. A probe core's internal antenna is only used when there's nothing else.
    /// </summary>
    /// <param name="rangeModifier">The game's range setting (difficulty options), applied to every antenna.</param>
    public static double CombinedPower(IEnumerable<PartAntenna> antennas, double rangeModifier = 1)
    {
        var all = antennas.Where(a => a.Power > 0).ToList();
        if (all.Any(a => a.Type != "INTERNAL")) all = all.Where(a => a.Type != "INTERNAL").ToList();
        if (all.Count == 0) return 0;

        var single = all.Where(a => !a.Combinable).Select(a => a.Power).DefaultIfEmpty(0).Max();
        var combinable = all.Where(a => a.Combinable).ToList();
        var combined = 0.0;
        if (combinable.Count > 0)
        {
            var strongest = combinable.Max(a => a.Power);
            var sum = combinable.Sum(a => a.Power);
            var exponent = combinable.Sum(a => a.Power * a.CombinableExponent) / sum;
            combined = strongest * Math.Pow(sum / strongest, exponent);
        }
        return Math.Max(single, combined) * rangeModifier;
    }

    /// <summary>The power a vessel passes others' signals on with: only its relay antennas count.</summary>
    public static double RelayPower(IEnumerable<PartAntenna> antennas, double rangeModifier = 1) =>
        CombinedPower(antennas.Where(a => a.Type == "RELAY"), rangeModifier);

    /// <summary>How far two antennas (or an antenna and the tracking station) can talk: √(P₁ · P₂).</summary>
    public static double Range(double power1, double power2) => Math.Sqrt(Math.Max(0, power1) * Math.Max(0, power2));

    /// <summary>
    /// Signal strength at a distance (0 to 1): x = 1 − d / range, strength = x²(3 − 2x). Full strength close by,
    /// half at half the range, none at the range.
    /// </summary>
    public static double Strength(double distanceM, double rangeM)
    {
        if (rangeM <= 0) return 0;
        var x = Math.Clamp(1 - distanceM / rangeM, 0, 1);
        return x * x * (3 - 2 * x);
    }

    /// <summary>
    /// How far a body gets from home at most: two bodies around the same parent are farthest apart on opposite sides,
    /// each at its apoapsis; a moon adds its own apoapsis to its planet's. Null for home itself, or when an orbit is missing.
    /// </summary>
    public static double? FarthestFromHome(CelestialBody body, IReadOnlyCollection<CelestialBody> system)
    {
        var home = system.FirstOrDefault(b => b.IsDefault);
        if (home is null || body.Id == home.Id) return null;
        var byId = system.ToDictionary(b => b.Id);

        var homeChain = Chain(home, byId);
        var bodyChain = Chain(body, byId);
        if (homeChain is null || bodyChain is null) return null;

        // Walk both up to the body they share (Kerbol for planets, Kerbin for its moons), adding apoapses on the way.
        var shared = homeChain.Select(b => b.Id).Intersect(bodyChain.Select(b => b.Id)).FirstOrDefault();
        double Up(List<CelestialBody> chain)
        {
            var total = 0.0;
            foreach (var b in chain)
            {
                if (b.Id == shared) break;
                total += Apoapsis(b);
            }
            return total;
        }
        return Up(homeChain) + Up(bodyChain);
    }

    private static double Apoapsis(CelestialBody body) => body.Orbit is { } o ? o.SemiMajorAxisM * (1 + o.Eccentricity) : 0;

    /// <summary>The body, its parent, its parent's parent… up to the star; null if an orbit is missing on the way.</summary>
    private static List<CelestialBody>? Chain(CelestialBody body, Dictionary<long, CelestialBody> byId)
    {
        var chain = new List<CelestialBody>();
        for (var b = body; b is not null; b = b.ParentId is { } p ? byId.GetValueOrDefault(p) : null)
        {
            if (b.ParentId is not null && b.Orbit is null) return null;
            chain.Add(b);
            if (chain.Count > 10) return null;
        }
        return chain;
    }
}
