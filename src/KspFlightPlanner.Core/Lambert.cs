namespace KspFlightPlanner.Core;

/// <summary>
/// Lambert's problem: the orbit that connects two positions in a given time.
/// Universal-variable formulation (Curtis, Orbital Mechanics for Engineering Students, Alg. 5.2),
/// solved by bisection on z for robustness. Single revolution, prograde: motion about +Z, or about
/// <c>prograde</c> when given (e.g. the departing craft's angular momentum, for inclined or polar orbits).
/// </summary>
public static class Lambert
{
    public readonly record struct Solution(Vector3d DepartureVelocity, Vector3d ArrivalVelocity);

    public static Solution? Solve(Vector3d r1, Vector3d r2, double timeOfFlight, double mu, Vector3d? prograde = null)
    {
        var r1Len = r1.Length;
        var r2Len = r2.Length;

        var cosDTheta = Math.Clamp(Vector3d.Dot(r1, r2) / (r1Len * r2Len), -1, 1);
        var dTheta = Math.Acos(cosDTheta);
        if (Vector3d.Dot(Vector3d.Cross(r1, r2), prograde ?? new Vector3d(0, 0, 1)) < 0) dTheta = 2 * Math.PI - dTheta; // prograde transfer

        var A = Math.Sin(dTheta) * Math.Sqrt(r1Len * r2Len / (1 - cosDTheta));
        if (Math.Abs(A) < 1e-9 || double.IsNaN(A)) return null; // 0° or 180° transfer: plane undefined

        var sqrtMu = Math.Sqrt(mu);
        double Y(double z) => r1Len + r2Len + A * (z * S(z) - 1) / Math.Sqrt(C(z));

        // Time of flight increases monotonically with z on the single-rev branch, z ∈ (−∞, 4π²).
        double low = -4000, high = 4 * Math.PI * Math.PI - 1e-9;
        double z = 0;
        for (var i = 0; i < 200; i++)
        {
            z = (low + high) / 2;
            var y = Y(z);
            if (y < 0) { low = z; continue; }
            var x = Math.Sqrt(y / C(z));
            var t = (x * x * x * S(z) + A * Math.Sqrt(y)) / sqrtMu;
            if (t < timeOfFlight) low = z; else high = z;
            if (high - low < 1e-12) break;
        }

        var yFinal = Y(z);
        if (yFinal <= 0 || double.IsNaN(yFinal)) return null;

        var f = 1 - yFinal / r1Len;
        var g = A * Math.Sqrt(yFinal / mu);
        var gDot = 1 - yFinal / r2Len;

        var v1 = (r2 - f * r1) / g;
        var v2 = (gDot * r2 - r1) / g;
        return new Solution(v1, v2);
    }

    /// <summary>Stumpff function C(z).</summary>
    private static double C(double z)
    {
        if (z > 1e-6) return (1 - Math.Cos(Math.Sqrt(z))) / z;
        if (z < -1e-6) return (Math.Cosh(Math.Sqrt(-z)) - 1) / -z;
        return 0.5 - z / 24 + z * z / 720;
    }

    /// <summary>Stumpff function S(z).</summary>
    private static double S(double z)
    {
        if (z > 1e-6)
        {
            var s = Math.Sqrt(z);
            return (s - Math.Sin(s)) / (s * s * s);
        }
        if (z < -1e-6)
        {
            var s = Math.Sqrt(-z);
            return (Math.Sinh(s) - s) / (s * s * s);
        }
        return 1.0 / 6 - z / 120 + z * z / 5040;
    }
}
