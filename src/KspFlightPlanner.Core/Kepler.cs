using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core;

/// <summary>
/// Propagates Keplerian orbits to position/velocity vectors. Elliptical orbits, and hyperbolic ones (a &lt; 0, e &gt; 1:
/// escaping, or flying past a moon), whose mean anomaly is M = e·sinh H − H and doesn't wrap. docs/MATH.md §1.4.
/// </summary>
public static class Kepler
{
    private const double DegToRad = Math.PI / 180;

    public static double OrbitalPeriod(OrbitalElements orbit, double parentMu) =>
        OrbitMath.Period(parentMu, orbit.SemiMajorAxisM);

    /// <summary>Position and velocity relative to the parent at universal time <paramref name="ut"/>.</summary>
    public static StateVector StateAt(OrbitalElements orbit, double parentMu, double ut)
    {
        var a = orbit.SemiMajorAxisM;
        var e = orbit.Eccentricity;
        var n = Math.Sqrt(parentMu / Math.Abs(a * a * a));
        var meanAnomaly = orbit.MeanAnomalyAtEpochRad + n * (ut - orbit.EpochS);
        double trueAnomaly, r;
        if (e < 1)
        {
            var E = SolveEccentricAnomaly(meanAnomaly, e);
            trueAnomaly = 2 * Math.Atan2(Math.Sqrt(1 + e) * Math.Sin(E / 2), Math.Sqrt(1 - e) * Math.Cos(E / 2));
            r = a * (1 - e * Math.Cos(E));
        }
        else
        {
            var H = SolveHyperbolicAnomaly(meanAnomaly, e);
            trueAnomaly = 2 * Math.Atan(Math.Sqrt((e + 1) / (e - 1)) * Math.Tanh(H / 2));
            r = a * (1 - e * Math.Cosh(H));
        }
        var p = a * (1 - e * e);

        // Perifocal frame: x toward periapsis, z along angular momentum.
        var pos = new Vector3d(r * Math.Cos(trueAnomaly), r * Math.Sin(trueAnomaly), 0);
        var velScale = Math.Sqrt(parentMu / p);
        var vel = new Vector3d(-velScale * Math.Sin(trueAnomaly), velScale * (e + Math.Cos(trueAnomaly)), 0);

        return new StateVector(
            ToReferenceFrame(pos, orbit),
            ToReferenceFrame(vel, orbit));
    }

    /// <summary>
    /// The orbit through a position and velocity at <paramref name="ut"/> (the inverse of <see cref="StateAt"/>); its epoch
    /// is <paramref name="ut"/>. Circular orbits get ω = 0 (anomaly measured from the ascending node), equatorial ones
    /// Ω = 0 (node line along +X). Faster than escape speed gives a hyperbola (a &lt; 0, e &gt; 1). See docs/MATH.md §1.5.
    /// </summary>
    public static OrbitalElements ElementsFrom(StateVector state, double parentMu, double ut)
    {
        var (r, v) = (state.Position, state.Velocity);
        var rLen = r.Length;
        var h = Vector3d.Cross(r, v);
        var hHat = h.Normalized;
        var a = 1 / (2 / rLen - Vector3d.Dot(v, v) / parentMu);
        var eVec = (r * (Vector3d.Dot(v, v) - parentMu / rLen) - v * Vector3d.Dot(r, v)) / parentMu;
        var e = eVec.Length;

        var i = Math.Acos(Math.Clamp(hHat.Z, -1, 1));
        var node = Vector3d.Cross(new Vector3d(0, 0, 1), h);
        var lan = node.Length < 1e-9 * h.Length ? 0 : Math.Atan2(node.Y, node.X);

        // In-plane axes: p along the node line (Rz(Ω)·Rx(i)·x̂ = Rz(Ω)·x̂), q ninety degrees ahead of it.
        var p = new Vector3d(Math.Cos(lan), Math.Sin(lan), 0);
        var q = Vector3d.Cross(hHat, p);
        var argumentOfLatitude = Math.Atan2(Vector3d.Dot(r, q), Vector3d.Dot(r, p));
        var omega = e < 1e-9 ? 0 : Math.Atan2(Vector3d.Dot(eVec, q), Vector3d.Dot(eVec, p));
        var nu = argumentOfLatitude - omega;

        double M;
        if (e < 1)
        {
            var E = 2 * Math.Atan2(Math.Sqrt(1 - e) * Math.Sin(nu / 2), Math.Sqrt(1 + e) * Math.Cos(nu / 2));
            M = E - e * Math.Sin(E);
        }
        else
        {
            var H = 2 * Math.Atanh(Math.Sqrt((e - 1) / (e + 1)) * Math.Tan(Math.IEEERemainder(nu, 2 * Math.PI) / 2));
            M = e * Math.Sinh(H) - H;
        }
        return new OrbitalElements(a, e, i * 180 / Math.PI, Wrap360(lan * 180 / Math.PI), Wrap360(omega * 180 / Math.PI), M, ut);
    }

    /// <summary>Mean anomaly at <paramref name="ut"/>, radians in [0, 2π).</summary>
    public static double MeanAnomalyAt(OrbitalElements orbit, double parentMu, double ut)
    {
        var a = orbit.SemiMajorAxisM;
        var m = orbit.MeanAnomalyAtEpochRad + Math.Sqrt(parentMu / (a * a * a)) * (ut - orbit.EpochS);
        return (m % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
    }

    /// <summary>The first time at or after <paramref name="afterUt"/> the orbit reaches mean anomaly <paramref name="meanAnomaly"/> (π = apoapsis).</summary>
    public static double NextTimeAtMeanAnomaly(OrbitalElements orbit, double parentMu, double afterUt, double meanAnomaly)
    {
        var a = orbit.SemiMajorAxisM;
        var n = Math.Sqrt(parentMu / (a * a * a));
        var delta = meanAnomaly - MeanAnomalyAt(orbit, parentMu, afterUt);
        return afterUt + (delta % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI) / n;
    }

    /// <summary>
    /// The next periapsis at or after <paramref name="afterUt"/>. A hyperbola has only one: null once it's passed (the
    /// craft is on its way out).
    /// </summary>
    public static double? NextPeriapsisUt(OrbitalElements orbit, double parentMu, double afterUt)
    {
        if (orbit.Eccentricity < 1) return NextTimeAtMeanAnomaly(orbit, parentMu, afterUt, 0);
        var a = orbit.SemiMajorAxisM;
        var ut = orbit.EpochS - orbit.MeanAnomalyAtEpochRad / Math.Sqrt(parentMu / Math.Abs(a * a * a));
        return ut >= afterUt ? ut : null;
    }

    /// <summary>The closest the orbit comes to the body's centre, a·(1 − e) (also for a hyperbola, where a &lt; 0).</summary>
    public static double PeriapsisRadius(OrbitalElements orbit) => orbit.SemiMajorAxisM * (1 - orbit.Eccentricity);

    private static double Wrap360(double deg) => (deg % 360 + 360) % 360;

    /// <summary>Solves Kepler's equation M = E − e·sin(E) by Newton iteration.</summary>
    public static double SolveEccentricAnomaly(double meanAnomaly, double e)
    {
        var M = Math.IEEERemainder(meanAnomaly, 2 * Math.PI);
        var E = e < 0.8 ? M : Math.PI * Math.Sign(M == 0 ? 1 : M);
        for (var i = 0; i < 50; i++)
        {
            var delta = (E - e * Math.Sin(E) - M) / (1 - e * Math.Cos(E));
            E -= delta;
            if (Math.Abs(delta) < 1e-13) break;
        }
        return E;
    }

    /// <summary>Solves the hyperbolic Kepler equation M = e·sinh(H) − H by Newton iteration.</summary>
    public static double SolveHyperbolicAnomaly(double meanAnomaly, double e)
    {
        var H = Math.Asinh(meanAnomaly / e);
        for (var i = 0; i < 100; i++)
        {
            var delta = (e * Math.Sinh(H) - H - meanAnomaly) / (e * Math.Cosh(H) - 1);
            H -= delta;
            if (Math.Abs(delta) < 1e-13 * Math.Max(1, Math.Abs(H))) break;
        }
        return H;
    }

    // Rotation perifocal → reference: Rz(Ω) · Rx(i) · Rz(ω)
    private static Vector3d ToReferenceFrame(Vector3d v, OrbitalElements orbit)
    {
        var (sO, cO) = Math.SinCos(orbit.LongitudeOfAscendingNodeDeg * DegToRad);
        var (si, ci) = Math.SinCos(orbit.InclinationDeg * DegToRad);
        var (sw, cw) = Math.SinCos(orbit.ArgumentOfPeriapsisDeg * DegToRad);

        var x = (cO * cw - sO * sw * ci) * v.X + (-cO * sw - sO * cw * ci) * v.Y;
        var y = (sO * cw + cO * sw * ci) * v.X + (-sO * sw + cO * cw * ci) * v.Y;
        var z = (sw * si) * v.X + (cw * si) * v.Y;
        return new Vector3d(x, y, z);
    }
}
