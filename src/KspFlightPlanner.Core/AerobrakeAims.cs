using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core;

/// <summary>How the periapsis is chosen.</summary>
public enum AerobrakeAim
{
    /// <summary>The periapsis typed in.</summary>
    Own,
    /// <summary>As low as is safe: the most braking per pass.</summary>
    FewestPasses,
    /// <summary>As high as still gets the apoapsis down in time: the least braking per pass.</summary>
    Gentlest,
    /// <summary>Down to a chosen apoapsis, then a burn there to lift the periapsis to a chosen height.</summary>
    TargetOrbit,
}

/// <summary>A periapsis worked out for an aim, and why.</summary>
/// <param name="StopAfterPass">The pass after which to stop and burn at apoapsis (null: no stop worked out).</param>
/// <param name="RaiseBurn">Target orbit: the burn at apoapsis after that pass to lift the periapsis to the target's.</param>
/// <param name="LowerBurn">Target orbit: when the passes stop above the target's apoapsis (going lower isn't safe), the burn at
/// the new periapsis that brings the apoapsis down the rest of the way; null when the passes get there.</param>
/// <param name="EnginesOnly">Target orbit: the same arrival into the same orbit by engine alone, burning at periapsis.</param>
/// <param name="StopApoapsisM">Target orbit: the apoapsis the passes leave.</param>
public sealed record AerobrakeSuggestion(double PeriapsisM, int? StopAfterPass, string Why, double? RaiseBurn = null, double? LowerBurn = null,
    double? EnginesOnly = null, double? StopApoapsisM = null)
{
    public double? TotalBurn => RaiseBurn is { } r ? r + (LowerBurn ?? 0) : null;
}

/// <summary>
/// Picks a periapsis for an aim (docs/MATH.md §9.4). Every choice allows for the drag being 30% more than worked out:
/// each pass, flown from the orbit the passes before it leave, must still come back out with that much more. (Pass by
/// pass, not all at once: you see each pass's real result, and can nudge the periapsis before the next.)
/// </summary>
public static class AerobrakeAims
{
    /// <summary>How far off the drag is allowed to be (each way).</summary>
    public const double DragMargin = 0.3;

    /// <summary>Target orbit: a burn total this close to the least (m/s, or share of it) is as good, for fewer passes.</summary>
    public const double GoodEnoughMs = 20, GoodEnoughShare = 0.1;

    /// <summary>Target orbit: an apoapsis this share of its radius above the target's counts as there.</summary>
    private const double ReachedShare = 0.02;

    /// <summary>Periapsis searches stop within this much (m).</summary>
    private const double Tolerance = 50;

    /// <summary>
    /// The lowest periapsis whose first pass still comes back out with 30% more drag: the most speed off per pass.
    /// Null when even the top of the air brings it down.
    /// </summary>
    public static AerobrakeSuggestion? FewestPasses(CelestialBody body, AerobrakeOrbit start, double massKg, DragTable drag)
    {
        var (lo, hi) = Range(body);
        var heavy = drag.Scaled(1 + DragMargin);
        bool Safe(double pe) => FirstPass(body, start, pe, massKg, heavy) is { Outcome: not PassOutcome.ComesDown };
        if (!Safe(hi)) return null;
        if (Safe(lo)) hi = lo;
        while (hi - lo > Tolerance)
        {
            var mid = (lo + hi) / 2;
            if (Safe(mid)) hi = mid; else lo = mid;
        }
        var pe = Math.Ceiling(hi / 100) * 100;
        var at = At(body, start, pe);
        if (FirstPass(body, start, pe, massKg, drag).Outcome == PassOutcome.Escaping)
            return new(pe, null, "The lowest that's safe still doesn't slow it enough to stay: brake with the engines too.");
        // Stop after the last pass that's still safe; the one after it would bite too hard.
        var plan = Aerobraking.Plan(body, at, massKg, drag);
        var stop = Enumerable.Range(1, plan.Passes.Count).Reverse()
            .FirstOrDefault(n => plan.Passes[n - 1].Outcome == PassOutcome.Orbiting && SafeThrough(body, at, massKg, drag, n));
        return stop == 0
            ? new(pe, null, "As low as it can go and still come back out of the first pass with 30% more drag than worked out.")
            : new(pe, stop, $"As low as it can go and still come back out of the first pass with 30% more drag than worked out. " +
                            $"Stop after pass {stop}: the next one isn't safe.", StopApoapsisM: plan.Passes[stop - 1].ApoapsisAfterM);
    }

    /// <summary>
    /// The highest periapsis that still brings the apoapsis down to <paramref name="apoapsisM"/> within
    /// <paramref name="maxPasses"/> passes, none of them coming down with 30% more drag. Null when no height does.
    /// </summary>
    public static AerobrakeSuggestion? Gentlest(CelestialBody body, AerobrakeOrbit start, double massKg, DragTable drag, double apoapsisM, int maxPasses)
    {
        var (lo, hi) = Range(body);
        // Reaches: the apoapsis gets down to the height in time, and safely.
        int? Reaches(double pe)
        {
            var plan = Aerobraking.Plan(body, At(body, start, pe), massKg, drag, maxPasses, apoapsisM);
            if (plan.Last is not { Outcome: PassOutcome.Orbiting, ApoapsisAfterM: { } ap } last || ap > apoapsisM) return null;
            return SafeThrough(body, At(body, start, pe), massKg, drag, last.Number) ? last.Number : null;
        }
        if (Reaches(hi) is { } top) return new(Math.Floor(hi / 100) * 100, top, Gentle(top, apoapsisM));
        // Search down from the top for the first height that reaches; above it nothing does.
        var step = (hi - lo) / 24;
        double? found = null;
        for (var pe = hi - step; pe >= lo; pe -= step)
            if (Reaches(pe) is not null) { found = pe; break; }
        if (found is not { } low) return null;
        var high = low + step;
        while (high - low > Tolerance)
        {
            var mid = (low + high) / 2;
            if (Reaches(mid) is not null) low = mid; else high = mid;
        }
        var chosen = Math.Floor(low / 100) * 100;
        if (Reaches(chosen) is not { } passes) (chosen, passes) = (low, Reaches(low)!.Value);
        return new(chosen, passes, Gentle(passes, apoapsisM));
    }

    private static string Gentle(int passes, double apoapsisM) =>
        $"As high as still gets the apoapsis down to {apoapsisM / 1000:N0} km, in {passes} pass{(passes == 1 ? "" : "es")}: the least drag each time.";

    /// <summary>
    /// Into a target orbit in as few passes as is safe: for each number of passes, the periapsis that brings the apoapsis
    /// down to the target's, or as near as is safe; then a burn at apoapsis to lift the periapsis to the target's (and, when
    /// the passes stop short, one at the new periapsis to bring the apoapsis down the rest of the way). The number of passes
    /// with the least burning wins, the fewer passes on a tie. Null when nothing up to <paramref name="maxPasses"/> works.
    /// </summary>
    public static AerobrakeSuggestion? TargetOrbit(CelestialBody body, AerobrakeOrbit start, double massKg, DragTable drag,
        double apoapsisM, double periapsisM, int maxPasses)
    {
        var (lo, hi) = Range(body);
        var options = new List<AerobrakeSuggestion>();
        var engines = EnginesOnly(body, start, apoapsisM, periapsisM);
        for (var n = 1; n <= maxPasses; n++)
        {
            // The apoapsis after pass n, if the craft comes back out of every pass up to it; higher periapsis, higher apoapsis.
            double? After(double pe)
            {
                var p = Aerobraking.Plan(body, At(body, start, pe), massKg, drag, n);
                return p.Passes.Count == n && p.Last is { Outcome: PassOutcome.Orbiting, ApoapsisAfterM: { } ap } ? ap : null;
            }
            bool Safe(double pe) => SafeThrough(body, At(body, start, pe), massKg, drag, n);
            if (!Safe(hi) || After(hi) is not { } highest) continue;
            // The lowest periapsis that's safe for n passes.
            var (a, b) = (lo, hi);
            if (Safe(a)) b = a;
            else while (b - a > Tolerance) { var m = (a + b) / 2; if (Safe(m)) b = m; else a = m; }
            var safest = b;
            double pe;
            if (highest <= apoapsisM) pe = hi; // even skimming the top gets there
            else if (After(safest) is { } low && low >= apoapsisM) pe = safest; // stops short of the target: as low as is safe
            else
            {
                // Between: the height where the apoapsis after pass n is the target's.
                var (down, up) = (safest, hi);
                while (up - down > Tolerance / 5)
                {
                    var m = (down + up) / 2;
                    if (After(m) is { } ap && ap < apoapsisM) down = m; else up = m;
                }
                pe = up;
            }
            pe = Math.Min(hi, Math.Ceiling(pe / 100) * 100);
            var plan = Aerobraking.Plan(body, At(body, start, pe), massKg, drag, n);
            if (plan.Passes.Count != n || plan.Last is not { Outcome: PassOutcome.Orbiting, ApoapsisAfterM: { } stopAp, PeriapsisAfterM: { } stopPe }) continue;
            var raise = RaiseBurn(body, stopAp, stopPe, periapsisM);
            // Within 2% of the target's apoapsis counts as there (the periapsis is rounded to 0.1 km).
            double? lower = stopAp > apoapsisM + ReachedShare * (body.EquatorialRadiusM + apoapsisM)
                ? LowerBurn(body, periapsisM, stopAp, apoapsisM) : null;
            var why = lower is null
                ? $"Down to {apoapsisM / 1000:N0} km in {n} pass{(n == 1 ? "" : "es")}, each safe with 30% more drag."
                : $"{n} pass{(n == 1 ? "" : "es")} down to {stopAp / 1000:N0} km: lower isn't safe with 30% more drag, so a burn does the rest.";
            options.Add(new AerobrakeSuggestion(pe, n, why, raise, lower, engines, stopAp));
        }
        // More passes save less and less: take the fewest within 20 m/s (or 10%) of the least burning.
        if (options.Count == 0) return null;
        var least = options.Min(o => o.TotalBurn!.Value);
        return options.First(o => o.TotalBurn <= least + Math.Max(GoodEnoughMs, GoodEnoughShare * least));
    }

    /// <summary>At periapsis r_p, apoapsis r_a → r_a': Δv = √(μ(2/r_p − 2/(r_p + r_a))) − √(μ(2/r_p − 2/(r_p + r_a'))).</summary>
    public static double LowerBurn(CelestialBody body, double periapsisM, double apoapsisM, double newApoapsisM)
    {
        var r = body.EquatorialRadiusM;
        var (rp, ra, ra2) = (r + periapsisM, r + apoapsisM, r + newApoapsisM);
        return Math.Abs(Math.Sqrt(body.Mu * (2 / rp - 2 / (rp + ra))) - Math.Sqrt(body.Mu * (2 / rp - 2 / (rp + ra2))));
    }

    /// <summary>
    /// At apoapsis r_a, from periapsis r_p to r_p': Δv = √(μ(2/r_a − 2/(r_a + r_p'))) − √(μ(2/r_a − 2/(r_a + r_p))) (vis-viva).
    /// </summary>
    public static double RaiseBurn(CelestialBody body, double apoapsisM, double periapsisM, double newPeriapsisM)
    {
        var r = body.EquatorialRadiusM;
        var (ra, rp, rp2) = (r + apoapsisM, r + periapsisM, r + newPeriapsisM);
        return Math.Abs(Math.Sqrt(body.Mu * (2 / ra - 2 / (ra + rp2))) - Math.Sqrt(body.Mu * (2 / ra - 2 / (ra + rp))));
    }

    /// <summary>
    /// The same arrival with its periapsis at the target's (out of the air), slowed there by engine to the target's
    /// apoapsis: Δv = √(2(ε + μ/r_p)) − √(μ(2/r_p − 2/(r_p + r_a))).
    /// </summary>
    public static double EnginesOnly(CelestialBody body, AerobrakeOrbit start, double apoapsisM, double periapsisM)
    {
        var r = body.EquatorialRadiusM;
        var (rp, ra) = (r + periapsisM, r + apoapsisM);
        // Same apoapsis (or speed from far away) with the periapsis moved up: the energy changes with it for a closed orbit.
        var energy = start.ApoapsisM(body) is { } ap ? -body.Mu / (2 * r + periapsisM + ap) : start.EnergyJPerKg;
        return Math.Sqrt(2 * (energy + body.Mu / rp)) - Math.Sqrt(body.Mu * (2 / rp - 2 / (rp + ra)));
    }

    /// <summary>
    /// Every pass up to and including <paramref name="passes"/> comes back out, and would still with 30% more drag, flown
    /// from the orbit the passes before it leave.
    /// </summary>
    public static bool SafeThrough(CelestialBody body, AerobrakeOrbit start, double massKg, DragTable drag, int passes)
    {
        var plan = Aerobraking.Plan(body, start, massKg, drag, passes);
        if (plan.Passes.Count < passes || plan.Passes.Take(passes).Any(p => p.Outcome != PassOutcome.Orbiting)) return false;
        var heavy = drag.Scaled(1 + DragMargin);
        var into = start;
        for (var i = 0; i < passes; i++)
        {
            var pass = plan.Passes[i];
            if (i > 0) into = AerobrakeOrbit.FromApsides(body, pass.PeriapsisM, plan.Passes[i - 1].ApoapsisAfterM!.Value, start.InclinationDeg);
            if (Aerobraking.Plan(body, into, massKg, heavy, 1).Passes[0].Outcome == PassOutcome.ComesDown) return false;
        }
        return true;
    }

    private static AerobrakePass FirstPass(CelestialBody body, AerobrakeOrbit start, double pe, double massKg, DragTable drag) =>
        Aerobraking.Plan(body, At(body, start, pe), massKg, drag, 1).Passes[0];

    /// <summary>The same arrival (apoapsis, or speed from far away) dipping to another periapsis.</summary>
    public static AerobrakeOrbit At(CelestialBody body, AerobrakeOrbit start, double periapsisM) => start with
    {
        PeriapsisM = periapsisM,
        EnergyJPerKg = start.ApoapsisM(body) is { } ap ? -body.Mu / (2 * body.EquatorialRadiusM + periapsisM + ap) : start.EnergyJPerKg,
    };

    /// <summary>Periapsis heights searched: from 20% of the way up the air to 0.5 km below its top.</summary>
    private static (double Lo, double Hi) Range(CelestialBody body) => (body.AtmosphereHeightM * 0.2, body.AtmosphereHeightM - 500);
}
