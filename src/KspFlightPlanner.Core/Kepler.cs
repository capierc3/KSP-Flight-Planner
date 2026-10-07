using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core;

/// <summary>Propagates elliptical Keplerian orbits to position/velocity vectors.</summary>
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
        var n = Math.Sqrt(parentMu / (a * a * a));
        var meanAnomaly = orbit.MeanAnomalyAtEpochRad + n * (ut - orbit.EpochS);
        var E = SolveEccentricAnomaly(meanAnomaly, e);

        var trueAnomaly = 2 * Math.Atan2(Math.Sqrt(1 + e) * Math.Sin(E / 2), Math.Sqrt(1 - e) * Math.Cos(E / 2));
        var r = a * (1 - e * Math.Cos(E));
        var p = a * (1 - e * e);

        // Perifocal frame: x toward periapsis, z along angular momentum.
        var pos = new Vector3d(r * Math.Cos(trueAnomaly), r * Math.Sin(trueAnomaly), 0);
        var velScale = Math.Sqrt(parentMu / p);
        var vel = new Vector3d(-velScale * Math.Sin(trueAnomaly), velScale * (e + Math.Cos(trueAnomaly)), 0);

        return new StateVector(
            ToReferenceFrame(pos, orbit),
            ToReferenceFrame(vel, orbit));
    }

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
