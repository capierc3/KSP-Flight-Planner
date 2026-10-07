namespace KspFlightPlanner.Core;

/// <summary>The two burns of a Hohmann transfer: Δv₁ leaves the start orbit, Δv₂ circularizes at the target.</summary>
public readonly record struct HohmannTransfer(double Dv1, double Dv2, double TransferTimeS)
{
    public double Total => Dv1 + Dv2;
}

/// <summary>
/// Two-body orbital mechanics helpers. Distances are metres measured from the body's
/// centre (radii) unless the parameter name says altitude; times are seconds.
/// </summary>
public static class OrbitMath
{
    /// <summary>Gravitational constant used by KSP (m³ kg⁻¹ s⁻²).</summary>
    public const double G = 6.67408E-11;

    private const double FourPiSquared = 4 * Math.PI * Math.PI;

    /// <summary>T = 2π√(a³/μ)</summary>
    public static double Period(double mu, double semiMajorAxis) =>
        2 * Math.PI * Math.Sqrt(Math.Pow(semiMajorAxis, 3) / mu);

    /// <summary>a = ∛(μT²/4π²)</summary>
    public static double SemiMajorAxisFromPeriod(double mu, double period) =>
        Math.Cbrt(mu * period * period / FourPiSquared);

    /// <summary>v = √(μ/r)</summary>
    public static double CircularVelocity(double mu, double radius) =>
        Math.Sqrt(mu / radius);

    /// <summary>Vis-viva: v = √(μ(2/r − 1/a))</summary>
    public static double VisViva(double mu, double radius, double semiMajorAxis) =>
        Math.Sqrt(mu * (2 / radius - 1 / semiMajorAxis));

    /// <summary>
    /// Hohmann transfer between circular, coplanar orbits of radius r1 and r2 (either direction).
    /// Δv₁ = |√(μ/r₁)·(√(2r₂/(r₁+r₂)) − 1)|, Δv₂ = |√(μ/r₂)·(1 − √(2r₁/(r₁+r₂)))|, t = π√(a³/μ).
    /// See docs/MATH.md §4.
    /// </summary>
    public static HohmannTransfer Hohmann(double mu, double r1, double r2)
    {
        var sum = r1 + r2;
        var dv1 = Math.Abs(Math.Sqrt(mu / r1) * (Math.Sqrt(2 * r2 / sum) - 1));
        var dv2 = Math.Abs(Math.Sqrt(mu / r2) * (1 - Math.Sqrt(2 * r1 / sum)));
        var a = sum / 2;
        return new HohmannTransfer(dv1, dv2, Math.PI * Math.Sqrt(a * a * a / mu));
    }

    /// <summary>
    /// Δv to go from a circular orbit of radius r to a hyperbolic escape with excess speed v∞ (or the reverse,
    /// capturing into that orbit): √(v∞² + 2μ/r) − √(μ/r). See docs/MATH.md §7.
    /// </summary>
    public static double HyperbolicBurnDv(double mu, double orbitRadius, double vInf) =>
        Math.Sqrt(vInf * vInf + 2 * mu / orbitRadius) - Math.Sqrt(mu / orbitRadius);

    /// <summary>e = (rₐ − rₚ) / (rₐ + rₚ)</summary>
    public static double Eccentricity(double periapsisRadius, double apoapsisRadius) =>
        (apoapsisRadius - periapsisRadius) / (apoapsisRadius + periapsisRadius);

    /// <summary>
    /// Lowest altitude at which <paramref name="satelliteCount"/> evenly spaced satellites keep line of
    /// sight to their neighbours. The chord between neighbours passes r·cos(π/n) from the centre, which
    /// must clear the body's radius scaled by <paramref name="occlusionModifier"/>.
    /// Null for fewer than 3 satellites (no closed ring of links).
    /// </summary>
    public static double? MinimumLineOfSightAltitude(double equatorialRadius, int satelliteCount, double occlusionModifier = 1.0)
    {
        if (satelliteCount < 3) return null;
        return equatorialRadius * occlusionModifier / Math.Cos(Math.PI / satelliteCount) - equatorialRadius;
    }
}
