namespace KspFlightPlanner.Core;

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
