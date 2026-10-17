using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core.Ksp;

/// <summary>One stretch with the engines running.</summary>
public sealed record FlightBurnRecord(double StartMetS, double DurationS, double DeltaV, string Body, int FirstStage, int LastStage);

/// <summary>How the climb to orbit went. Speeds are orbital (against the stars). docs/MATH.md §8.6.</summary>
/// <param name="ReachedOrbit">False when the flight never got into orbit: the figures then run to the end of the log.</param>
/// <param name="EndMetS">When the craft got into orbit (KSP's ORBITING: periapsis above the air).</param>
/// <param name="SpeedGained">Orbital speed at the end − at the launch (the pad already moves with the planet's spin).</param>
/// <param name="GravityLoss">Δv the climb lost to gravity pulling back along the path: ∫ g·sin γ dt.</param>
/// <param name="DragAndSteeringLoss">The rest: spent − gained − gravity loss.</param>
public sealed record AscentReport(bool ReachedOrbit, double EndMetS, double DeltaVSpent, double SpeedGained, double GravityLoss,
    double DragAndSteeringLoss, double ApoapsisAltitudeM, double PeriapsisAltitudeM, double MaxQPa, double MaxQAltitudeM, double MaxQMetS)
{
    public double TotalLoss => GravityLoss + DragAndSteeringLoss;
}

/// <summary>Coming down on a body: how fast it was going the moment before it touched.</summary>
/// <param name="DescentStartMetS">The last time it was in orbit (or entered the body's sphere) before coming down.</param>
/// <param name="DeltaVSpent">Spent from then to touchdown.</param>
public sealed record TouchdownRecord(string Body, double DescentStartMetS, double MetS, bool Splashed, double VerticalSpeed, double SurfaceSpeed,
    double LatitudeDeg, double LongitudeDeg, double DeltaVSpent);

/// <summary>A point worked out from the orbit where the log has no samples (on rails, the craft follows it exactly).</summary>
public sealed record FilledPoint(double Ut, double MetS, string Body, double AltitudeM, double OrbitalSpeed)
{
    /// <summary>Where the craft is relative to <see cref="Body"/>, when the orbit's orientation was recorded.</summary>
    public Vector3d? Position { get; init; }
}

/// <summary>A flight log worked through: where the Δv went and how the ascent and landings went.</summary>
public sealed record FlightReport(
    IReadOnlyList<FlightBurnRecord> Burns,
    IReadOnlyList<(int Stage, double DeltaV)> ByStage,
    IReadOnlyList<(string Body, double DeltaV)> ByBody,
    AscentReport? Ascent,
    IReadOnlyList<TouchdownRecord> Touchdowns,
    double MaxG,
    double MaxGMetS,
    IReadOnlyList<double> DeltaVSoFar);

public static class FlightAnalysis
{
    /// <summary>Two runs of the engines closer than this (s) are one burn (throttle blips, a staging pause).</summary>
    public const double BurnJoinS = 3;

    public static FlightReport Analyze(FlightLog log, Func<string, CelestialBody?> body)
    {
        var s = log.Samples;
        var soFar = new double[s.Count];
        var steps = new double[s.Count]; // Δv spent arriving at sample i
        for (var i = 1; i < s.Count; i++)
        {
            steps[i] = FlightLogMath.DeltaVBetween(s[i - 1], s[i]);
            soFar[i] = soFar[i - 1] + steps[i];
        }

        var byStage = new SortedDictionary<int, double>(Comparer<int>.Create((a, b) => b.CompareTo(a))); // in firing order
        var byBody = new List<(string Body, double DeltaV)>();
        for (var i = 1; i < s.Count; i++)
        {
            if (steps[i] <= 0) continue;
            byStage[s[i - 1].Stage] = byStage.GetValueOrDefault(s[i - 1].Stage) + steps[i];
            var where = s[i - 1].Body;
            if (byBody.FindIndex(b => b.Body == where) is var at and >= 0) byBody[at] = (where, byBody[at].DeltaV + steps[i]);
            else byBody.Add((where, steps[i]));
        }

        var maxG = 0.0;
        var maxGMet = 0.0;
        foreach (var x in s.Where(x => x.Situation is not ("LANDED" or "SPLASHED" or "PRELAUNCH")))
            if (x.GForce > maxG) (maxG, maxGMet) = (x.GForce, x.MetS);

        return new FlightReport(Burns(s, steps), byStage.Select(p => (p.Key, p.Value)).ToList(), byBody,
            log.FromLaunch ? Ascent(s, steps, body) : null, Touchdowns(s, soFar), maxG, maxGMet, soFar);
    }

    private static List<FlightBurnRecord> Burns(IReadOnlyList<FlightSample> s, double[] steps)
    {
        var burns = new List<FlightBurnRecord>();
        int? start = null;
        var end = 0;
        var dv = 0.0;
        var closedAt = -1; // the last sample counted in a burn
        for (var i = 0; i < s.Count; i++)
        {
            if (s[i].ThrustN <= 0) continue;
            if (start is { } b && s[i].Ut - s[end].Ut <= BurnJoinS && s[i].Body == s[b].Body)
            {
                for (var j = end + 1; j <= i; j++) dv += steps[j];
                end = i;
                continue;
            }
            Close();
            start = end = i;
            dv = i > closedAt ? steps[i] : 0; // the throttle coming up, since the sample before
        }
        Close();
        return burns;

        void Close()
        {
            if (start is not { } b) return;
            // The burn runs on to the sample after its last one with thrust, where the engines were seen off.
            var last = Math.Min(end + 1, s.Count - 1);
            if (last > end) dv += steps[last];
            burns.Add(new FlightBurnRecord(s[b].MetS, s[last].Ut - s[b].Ut, dv, s[b].Body, s[b].Stage, s[end].Stage));
            closedAt = last;
            start = null;
        }
    }

    /// <summary>From the launch to the first sample in orbit around the launch body (or the log's end).</summary>
    private static AscentReport? Ascent(IReadOnlyList<FlightSample> s, double[] steps, Func<string, CelestialBody?> body)
    {
        if (s.Count < 2 || body(s[0].Body) is not { } home) return null;
        var end = 0;
        while (end < s.Count - 1 && s[end].Situation != "ORBITING" && s[end + 1].Body == home.Name) end++;
        var reached = s[end].Situation == "ORBITING";

        double spent = 0, gravity = 0, maxQ = 0;
        var maxQAt = s[0];
        for (var i = 1; i <= end; i++)
        {
            spent += steps[i];
            gravity += (GravityAlongPath(s[i - 1], home) + GravityAlongPath(s[i], home)) / 2 * (s[i].Ut - s[i - 1].Ut);
        }
        for (var i = 0; i <= end; i++)
            if (s[i].DynamicPressurePa > maxQ) (maxQ, maxQAt) = (s[i].DynamicPressurePa, s[i]);

        var gained = s[end].OrbitalSpeed - s[0].OrbitalSpeed;
        return new AscentReport(reached, s[end].MetS, spent, gained, gravity, spent - gained - gravity,
            s[end].ApoapsisAltitudeM, s[end].PeriapsisAltitudeM, maxQ, maxQAt.AltitudeM, maxQAt.MetS);
    }

    /// <summary>
    /// g·sin γ: the part of gravity along the path, with γ the climb angle (sin γ = vertical speed ÷ orbital speed).
    /// Positive while climbing.
    /// </summary>
    public static double GravityAlongPath(FlightSample x, CelestialBody body)
    {
        if (x.OrbitalSpeed <= 0) return 0;
        var r = body.EquatorialRadiusM + x.AltitudeM;
        var g = body.Mu / (r * r);
        return g * Math.Clamp(x.VerticalSpeed / x.OrbitalSpeed, -1, 1);
    }

    private static List<TouchdownRecord> Touchdowns(IReadOnlyList<FlightSample> s, double[] soFar)
    {
        var list = new List<TouchdownRecord>();
        var from = 0; // where the descent started: last time in orbit or entering the body
        for (var i = 1; i < s.Count; i++)
        {
            if (s[i].Body != s[i - 1].Body || s[i - 1].Situation is "ORBITING" or "ESCAPING") from = i - 1;
            var down = s[i].Situation is "LANDED" or "SPLASHED";
            if (!down || s[i - 1].Situation is "LANDED" or "SPLASHED" or "PRELAUNCH") continue;
            var before = s[i - 1];
            list.Add(new TouchdownRecord(s[i].Body, s[from].MetS, s[i].MetS, s[i].Situation == "SPLASHED", before.VerticalSpeed, before.SurfaceSpeed,
                s[i].LatitudeDeg, s[i].LongitudeDeg, soFar[i] - soFar[from]));
        }
        return list;
    }

    /// <summary>Gaps between samples longer than this (game seconds) are filled from the orbit.</summary>
    public const double FillAfterS = 30;

    /// <summary>At most this many points per gap: plenty for a chart, however long the warp.</summary>
    public const int FillPointsPerGap = 200;

    /// <summary>
    /// Points along the path where the log has no samples (time warp on rails, or not recorded while coasting), worked
    /// out from the orbit with Kepler's equation. On rails the craft follows its orbit exactly; only a change of sphere of
    /// influence changes it, so a gap that crosses one is filled forward from the sample before it until the craft leaves
    /// the old body's sphere, and back from the sample after it until it entered the new one. Gaps where the mass changed
    /// (a burn while not recorded), in the air or on the ground aren't filled. docs/MATH.md §8.7.
    /// </summary>
    public static IReadOnlyList<FilledPoint> FillGaps(IReadOnlyList<FlightSample> s, Func<string, CelestialBody?> body,
        Func<CelestialBody, CelestialBody?>? parent = null)
    {
        var points = new List<FilledPoint>();
        for (var i = 1; i < s.Count; i++)
        {
            var (a, b) = (s[i - 1], s[i]);
            if (b.Ut - a.Ut <= FillAfterS || !Coasting(a) || !Coasting(b) || Math.Abs(a.MassKg - b.MassKg) > 1e-3 * a.MassKg) continue;
            var step = Math.Max(FillAfterS / 3, (b.Ut - a.Ut) / FillPointsPerGap);
            var other = a.Body == b.Body ? null : body(b.Body);
            if (a.HasOrbit && body(a.Body) is { } from)
                for (var t = a.Ut + step; t < b.Ut; t += step)
                {
                    var p = Along(a, from, t);
                    var at = PositionAt(a, from, t);
                    if (other is not null && !Inside(from, other, p.R, at, t, parent)) break;
                    points.Add(new FilledPoint(t, a.MetS + (t - a.Ut), a.Body, p.R - from.EquatorialRadiusM, p.V) { Position = at });
                }
            if (other is not null && b.HasOrbit && body(b.Body) is { } to && body(a.Body) is { } left)
            {
                var back = new List<FilledPoint>();
                for (var t = b.Ut - step; t > a.Ut; t -= step)
                {
                    var p = Along(b, to, t);
                    var at = PositionAt(b, to, t);
                    if (!Inside(to, left, p.R, at, t, parent)) break;
                    back.Add(new FilledPoint(t, b.MetS - (b.Ut - t), b.Body, p.R - to.EquatorialRadiusM, p.V) { Position = at });
                }
                back.Reverse();
                points.AddRange(back);
            }
        }
        return points;
    }

    /// <summary>
    /// Whether a craft <paramref name="r"/> from the centre of <paramref name="around"/> (at <paramref name="at"/>, when
    /// known) is still in its sphere of influence rather than <paramref name="other"/>'s: inside its own sphere, and, when
    /// the other is one of its moons, outside the moon's (where the moon was then; without a position, short of the
    /// moon's orbit less its sphere).
    /// </summary>
    private static bool Inside(CelestialBody around, CelestialBody other, double r, Vector3d? at, double ut, Func<CelestialBody, CelestialBody?>? parent)
    {
        if (r > around.SoiRadiusM) return false;
        if (other.Orbit is not { } moonOrbit || parent?.Invoke(other)?.Name != around.Name) return true;
        if (at is { } p) return (p - Kepler.StateAt(moonOrbit, around.Mu, ut).Position).Length > other.SoiRadiusM;
        return r < Math.Abs(moonOrbit.SemiMajorAxisM) - other.SoiRadiusM;
    }

    /// <summary>Where the craft is (relative to its body) at <paramref name="ut"/> on the orbit of <paramref name="x"/>, if all of it was kept.</summary>
    public static Vector3d? PositionAt(FlightSample x, CelestialBody body, double ut) =>
        x.Elements is { } orbit ? Kepler.StateAt(orbit, body.Mu, ut).Position : null;

    private static bool Coasting(FlightSample x) =>
        x.ThrustN <= 0 && x.DynamicPressurePa <= 0 && x.Situation is not ("LANDED" or "SPLASHED" or "PRELAUNCH" or "FLYING");

    /// <summary>
    /// Distance from the body's centre and orbital speed at <paramref name="ut"/> on the orbit of <paramref name="x"/>:
    /// M = M₀ + n·Δt, Kepler's equation for E (or H on a hyperbola), r = a·(1 − e·cos E), v from vis-viva.
    /// </summary>
    public static (double R, double V) Along(FlightSample x, CelestialBody body, double ut)
    {
        var a = x.SemiMajorAxisM;
        var e = x.Eccentricity;
        var m = x.MeanAnomalyRad + Math.Sqrt(body.Mu / Math.Abs(a * a * a)) * (ut - x.Ut);
        var r = e < 1
            ? a * (1 - e * Math.Cos(Kepler.SolveEccentricAnomaly(m, e)))
            : a * (1 - e * Math.Cosh(Kepler.SolveHyperbolicAnomaly(m, e)));
        return (r, Math.Sqrt(Math.Max(0, body.Mu * (2 / r - 1 / a))));
    }

    /// <summary>Ground distance (m) from the launch site, along the surface of a body of this radius.</summary>
    public static double Downrange(FlightSample from, FlightSample to, double radiusM)
    {
        const double rad = Math.PI / 180;
        var dLat = (to.LatitudeDeg - from.LatitudeDeg) * rad;
        var dLon = (to.LongitudeDeg - from.LongitudeDeg) * rad;
        var a = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(from.LatitudeDeg * rad) * Math.Cos(to.LatitudeDeg * rad) * Math.Pow(Math.Sin(dLon / 2), 2);
        return 2 * radiusM * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
}
