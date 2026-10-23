using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core.Ksp;

/// <summary>
/// A pitch program for the climb: straight up until <paramref name="TurnStartSpeed"/> (m/s over the ground), then
/// pitch = 90° · (1 − f^<paramref name="Shape"/>), f the share of the climb from there to <paramref name="TurnEndAltitudeM"/>,
/// level from then on.
/// </summary>
public sealed record PitchProgram(double TurnStartSpeed, double TurnEndAltitudeM, double Shape);

/// <summary>A moment of a simulated climb.</summary>
public sealed record SimPoint(double TimeS, double AltitudeM, double PitchDeg, double DownrangeM, double SurfaceSpeed);

/// <summary>A simulated climb: whether it got to the target orbit and what that took.</summary>
public sealed record SimAscent(bool ReachedOrbit, double DeltaV, double TimeS, IReadOnlyList<SimPoint> Path, PitchProgram? Program);

/// <summary>
/// The best climb found for this rocket, against a replay of the flown steering through the same model. The model's own
/// error largely cancels in <see cref="Saving"/>: both runs share its drag, thrust and air. Without a replay (a flight
/// with no usable pitch) the best is set against the real flight, which leaves the model's error in.
/// </summary>
public sealed record OptimalAscent(SimAscent Best, SimAscent? Replay, double FlownDeltaV)
{
    public double Saving => !Best.ReachedOrbit ? 0 : Math.Max(0, (Replay is { ReachedOrbit: true } r ? r.DeltaV : FlownDeltaV) - Best.DeltaV);
    public double BestEstimate => FlownDeltaV - Saving;

    /// <summary>Replay Δv ÷ flown Δv − 1: how well the model reproduces the real flight (null without a replay).</summary>
    public double? ModelError => Replay is { ReachedOrbit: true } r && FlownDeltaV > 0 ? r.DeltaV / FlownDeltaV - 1 : null;
}

/// <summary>
/// Re-flies a recorded climb in a model fitted to that flight, so other ways of steering can be tried. The rocket burns
/// fuel at the rate it did (KSP engines burn at a fixed rate; thrust changes with Isp, which changes with air pressure),
/// stages when it did (by burn time), and meets the drag the flight showed. 2D, over the equator, eastward, on a
/// turning planet. docs/MATH.md §8.11.
/// </summary>
public sealed class AscentSimulator
{
    public const double StepS = 0.1;

    /// <summary>
    /// In thick air (q ≥ 1 kPa) a tried program keeps the nose within this many degrees of prograde: further off, a
    /// real rocket starts to flip, so no program may win by doing it.
    /// </summary>
    public const double MaxOffProgradeDeg = 5;

    /// <summary>
    /// The recording only shows the fuel burned up to orbit; a climb flown differently may need a little more. Past the
    /// recorded burn the last stage keeps burning at its last rate, up to this share more burn time.
    /// </summary>
    public const double ExtraBurnShare = 0.5;

    /// <summary>
    /// Fast bands are flown high, in thin air, so their fit is mostly noise (often near 0). KSP's drag falls past Mach 1
    /// but stays well above nothing, so no band is taken below this share of the subsonic drag area (100–400 m/s).
    /// </summary>
    public const double MinDragShare = 0.4;
    private const double G0 = VesselAnalysis.G0;
    private const double SpeedBin = 100;   // drag area is fitted per 100 m/s of air speed
    private const double HeightBin = 5_000; // air density per atmosphere per 5 km

    private readonly CelestialBody _body;
    private readonly double[] _burnTime, _mass, _massFlow;
    private readonly int[] _stage;
    private readonly Dictionary<int, (double Vacuum, double PerAtm)> _isp = [];
    private readonly double[] _dragArea;     // m², per speed bin
    private readonly double[] _densityPerAtm; // kg/m³ per atm, per height bin
    private readonly double _startAltitude, _startVertical;
    private readonly double _targetAp, _targetPe;
    private readonly (double BurnTime, double Pitch)[] _flownPitch;
    private readonly bool _hasPitch;
    private readonly double _lastFlow; // the last stage's fuel flow, for burning past the recording

    /// <summary>Burn time the recorded fuel lasts, s.</summary>
    public double BurnTimeS => _burnTime[^1];

    /// <summary>Drag area fitted per 100 m/s of air speed (m²), for the report.</summary>
    public IReadOnlyList<double> DragArea => _dragArea;

    private AscentSimulator(CelestialBody body, IReadOnlyList<FlightSample> s, AscentReport ascent)
    {
        _body = body;
        _startAltitude = s[0].AltitudeM;
        _startVertical = s[0].VerticalSpeed;
        _targetAp = ascent.ApoapsisAltitudeM;
        _targetPe = ascent.PeriapsisAltitudeM;

        // Fuel: mass and fuel flow against burn time (time with the engines running).
        var burn = new List<double> { 0 };
        for (var i = 1; i < s.Count; i++)
            burn.Add(burn[^1] + (s[i - 1].ThrustN > 0 || s[i].ThrustN > 0 ? s[i].Ut - s[i - 1].Ut : 0));
        _burnTime = [.. burn];
        _mass = s.Select(x => x.MassKg).ToArray();
        _massFlow = s.Select(x => x.ThrustN > 0 && x.IspS > 0 ? x.ThrustN / (x.IspS * G0) : 0).ToArray();
        _stage = s.Select(x => x.Stage).ToArray();
        _lastFlow = _massFlow.LastOrDefault(m => m > 0);
        _flownPitch = s.Select((x, i) => (_burnTime[i], x.PitchDeg)).ToArray();
        _hasPitch = s.All(x => x.SurfaceAttitude);

        // Isp against pressure, per stage: a straight line through what the flight saw (KSP's curves are close to one).
        foreach (var group in s.Where(x => x.ThrustN > 0 && x.IspS > 0).GroupBy(x => x.Stage))
        {
            var pts = group.Select(x => (P: Pressure(x.AltitudeM), Isp: x.IspS)).ToList();
            var (mp, mi) = (pts.Average(p => p.P), pts.Average(p => p.Isp));
            var spread = pts.Sum(p => (p.P - mp) * (p.P - mp));
            var slope = spread > 1e-4 * pts.Count && pts.Max(p => p.P) - pts.Min(p => p.P) > 0.05
                ? pts.Sum(p => (p.P - mp) * (p.Isp - mi)) / spread
                : 0;
            _isp[group.Key] = (mi - slope * mp, slope);
        }

        // Air density per atm of pressure, from q = ½ρv² where the flight was in air.
        var density = new List<(double H, double PerAtm)>();
        foreach (var x in s)
        {
            var p = Pressure(x.AltitudeM);
            if (x.DynamicPressurePa > 200 && x.SurfaceSpeed > 50 && p > 1e-4)
                density.Add((x.AltitudeM, 2 * x.DynamicPressurePa / (x.SurfaceSpeed * x.SurfaceSpeed) / p));
        }
        var heightBins = (int)Math.Ceiling((body.Atmosphere?.HeightM ?? 0) / HeightBin) + 1;
        _densityPerAtm = Binned(density, heightBins, HeightBin, fallback: 1.225);

        // Drag area from the flight: whatever slowed it beyond gravity and thrust along the path, over q. Summed per speed
        // band (Σ drag ÷ Σ q) rather than averaged as ratios, so thin air, where a small error makes a big ratio, counts little.
        var drag = new List<(double V, double Drag, double Q)>();
        for (var i = 1; i < s.Count - 1; i++)
        {
            var (a, x, b) = (s[i - 1], s[i], s[i + 1]);
            if (x.DynamicPressurePa < 1_000 || x.SurfaceSpeed < 30 || a.Stage != b.Stage || b.Ut <= a.Ut) continue;
            // Worked in the non-turning frame, where only thrust, drag and gravity act (MATH §8.11): along the orbital
            // velocity, dv/dt = (F·cos τ − D·cos β)/m − g·sin γ, with β between the airflow and the orbital velocity and τ
            // between the nose and it. The flight is taken as going east over the equator.
            var r = body.EquatorialRadiusM + x.AltitudeM;
            var g = body.Mu / (r * r);
            var vs = x.VerticalSpeed;
            var east = Math.Sqrt(Math.Max(0, x.SurfaceSpeed * x.SurfaceSpeed - vs * vs));
            var eastInertial = east + 2 * Math.PI / body.RotationPeriodS * r;
            var v = Math.Sqrt(eastInertial * eastInertial + vs * vs);
            var cosAir = (east * eastInertial + vs * vs) / (x.SurfaceSpeed * v);
            // Without a usable pitch the thrust is taken as along the path, so steering is counted in with drag.
            var (tEast, tUp) = _hasPitch && x.SurfaceSpeed >= AscentCoach.ProgradeFromSpeed
                ? (Math.Cos(x.PitchDeg * Math.PI / 180), Math.Sin(x.PitchDeg * Math.PI / 180))
                : (east / x.SurfaceSpeed, vs / x.SurfaceSpeed);
            var cosThrust = (tEast * eastInertial + tUp * vs) / v;
            var dvdt = (b.OrbitalSpeed - a.OrbitalSpeed) / (b.Ut - a.Ut);
            if (cosAir < 0.2) continue;
            var dragForce = (x.ThrustN * cosThrust - x.MassKg * (dvdt + g * vs / v)) / cosAir;
            drag.Add((x.SurfaceSpeed, dragForce, x.DynamicPressurePa));
        }
        _dragArea = Weighted(drag, 30, SpeedBin);
        var subsonic = _dragArea.Skip(1).Take(3).DefaultIfEmpty(0).Max();
        for (var i = 0; i < _dragArea.Length; i++) _dragArea[i] = Math.Max(_dragArea[i], MinDragShare * subsonic);
    }

    /// <summary>A simulator for the flight's climb, or null when it had none (joined in flight, or never reached orbit).</summary>
    public static AscentSimulator? For(FlightLog log, AscentReport? ascent, CelestialBody body)
    {
        if (ascent is not { ReachedOrbit: true } || !log.FromLaunch || body.Atmosphere is null) return null;
        // An orbit to aim for: a closed one (an escape isn't "to orbit").
        if (!double.IsFinite(ascent.ApoapsisAltitudeM) || ascent.ApoapsisAltitudeM < ascent.PeriapsisAltitudeM) return null;
        var s = AscentCoach.AscentSamples(log, ascent);
        return s.Count < 10 || !s.Any(x => x.ThrustN > 0) ? null : new AscentSimulator(body, s, ascent);
    }

    /// <summary>The best program found (a coarse search, then a finer one round the best), and the flown steering replayed.</summary>
    public OptimalAscent Optimize(double flownDeltaV)
    {
        var replay = _hasPitch ? Fly(null) : null;
        SimAscent? best = null;
        void Try(PitchProgram program)
        {
            var run = Fly(program);
            if (run.ReachedOrbit && (best is null || run.DeltaV < best.DeltaV)) best = run;
        }
        foreach (var v0 in new[] { 30.0, 50, 70, 90, 110, 140 })
            for (var h = 20_000.0; h <= 80_000; h += 5_000)
                for (var a = 0.3; a <= 1.21; a += 0.1)
                    Try(new PitchProgram(v0, h, a));
        if (best?.Program is { } p)
            for (var v0 = p.TurnStartSpeed - 10; v0 <= p.TurnStartSpeed + 10; v0 += 5)
                for (var h = p.TurnEndAltitudeM - 4_000; h <= p.TurnEndAltitudeM + 4_000; h += 1_000)
                    for (var a = p.Shape - 0.08; a <= p.Shape + 0.081; a += 0.02)
                        if (v0 >= 30 && a > 0.1 && h > 5_000) Try(new PitchProgram(v0, h, a));
        return new OptimalAscent(best ?? new SimAscent(false, 0, 0, [], null), replay, flownDeltaV);
    }

    /// <summary>
    /// Flies the climb with <paramref name="program"/> (or the flown pitch against burn time when null): burn until the
    /// apoapsis reaches the flown one, coast to it, then burn level until the periapsis reaches the flown one.
    /// </summary>
    public SimAscent Fly(PitchProgram? program)
    {
        var mu = _body.Mu;
        var radius = _body.EquatorialRadiusM;
        var omega = 2 * Math.PI / _body.RotationPeriodS;
        var r0 = radius + _startAltitude;
        double x = r0, y = 0, vx = _startVertical, vy = omega * r0;
        double t = 0, burnT = 0, dv = 0;
        double? turnFrom = null;
        var phase = 0; // 0 climb, 1 coast, 2 circularize
        var path = new List<SimPoint>();
        double around = 0, lastAngle = 0; // how far round the planet, kept counting past ±180°

        while (t < 3_600)
        {
            var r = Math.Sqrt(x * x + y * y);
            var (ux, uy) = (x / r, y / r);
            var (ex, ey) = (-uy, ux); // east
            var h = r - radius;
            if (h < _startAltitude - 50) return Done(false);

            var (ax, ay) = (-mu / (r * r) * ux, -mu / (r * r) * uy);
            var (sx, sy) = (vx - omega * r * ex, vy - omega * r * ey); // air speed
            var surface = Math.Sqrt(sx * sx + sy * sy);
            var pressure = Pressure(h);
            var q = 0.5 * Density(h, pressure) * surface * surface;
            var mass = MassAt(burnT);
            if (q > 0 && surface > 1)
            {
                var drag = q * DragAreaAt(surface) / mass;
                ax -= drag * sx / surface;
                ay -= drag * sy / surface;
            }

            var (ap, pe) = Apsides(x, y, vx, vy, mu, radius);
            if (phase == 0 && ap >= _targetAp) phase = 1;
            if (phase == 1 && ux * vx + uy * vy <= 0) phase = 2;
            if (phase == 1 && h < (_body.Atmosphere?.HeightM ?? 0) && ap < _targetAp - 1_000) phase = 0;
            if (phase == 2 && pe >= _targetPe) return Done(true);

            var pitchDeg = 0.0;
            if (phase != 1)
            {
                if (burnT >= BurnTimeS * (1 + ExtraBurnShare)) return Done(false); // out of fuel
                double dx, dy;
                if (phase == 2)
                {
                    (dx, dy) = (ex, ey); // level, east
                }
                else
                {
                    if (program is not null && turnFrom is null && surface >= program.TurnStartSpeed) turnFrom = h;
                    pitchDeg = program is null ? FlownPitch(burnT)
                        : turnFrom is not { } from ? 90
                        : 90 * (1 - Math.Pow(Math.Clamp((h - from) / Math.Max(1, program.TurnEndAltitudeM - from), 0, 1), program.Shape));
                    if (program is not null && q >= AscentCoach.ThickAirPa && surface >= AscentCoach.ProgradeFromSpeed)
                    {
                        var prograde = Math.Asin(Math.Clamp((ux * sx + uy * sy) / surface, -1, 1)) * 180 / Math.PI;
                        pitchDeg = Math.Clamp(pitchDeg, prograde - MaxOffProgradeDeg, prograde + MaxOffProgradeDeg);
                    }
                    var p = pitchDeg * Math.PI / 180;
                    (dx, dy) = (ux * Math.Sin(p) + ex * Math.Cos(p), uy * Math.Sin(p) + ey * Math.Cos(p));
                }
                var thrust = MassFlowAt(burnT) * G0 * IspAt(burnT, pressure) / mass;
                ax += thrust * dx;
                ay += thrust * dy;
                dv += thrust * StepS;
                burnT += StepS;
            }

            var angle = Math.Atan2(y, x);
            around += Math.IEEERemainder(angle - lastAngle, 2 * Math.PI);
            lastAngle = angle;
            if (path.Count == 0 || t - path[^1].TimeS >= 1)
            {
                var downrange = radius * (around - omega * t);
                path.Add(new SimPoint(t, h, phase == 1 ? double.NaN : pitchDeg, downrange, surface));
            }
            vx += ax * StepS;
            vy += ay * StepS;
            x += vx * StepS;
            y += vy * StepS;
            t += StepS;
        }
        return Done(false);

        SimAscent Done(bool reached) => new(reached, dv, t, path, program);
    }

    private static (double Ap, double Pe) Apsides(double x, double y, double vx, double vy, double mu, double radius)
    {
        var r = Math.Sqrt(x * x + y * y);
        var energy = (vx * vx + vy * vy) / 2 - mu / r;
        if (energy >= 0) return (double.PositiveInfinity, double.NegativeInfinity);
        var a = -mu / (2 * energy);
        var h = x * vy - y * vx;
        var e = Math.Sqrt(Math.Max(0, 1 + 2 * energy * h * h / (mu * mu)));
        return (a * (1 + e) - radius, a * (1 - e) - radius);
    }

    private double Pressure(double altitudeM) => _body.Atmosphere?.PressureAtmAt(altitudeM) ?? 0;

    private double Density(double altitudeM, double pressureAtm) =>
        pressureAtm <= 0 ? 0 : pressureAtm * _densityPerAtm[Math.Clamp((int)(altitudeM / HeightBin), 0, _densityPerAtm.Length - 1)];

    private double DragAreaAt(double speed) => _dragArea[Math.Clamp((int)(speed / SpeedBin), 0, _dragArea.Length - 1)];

    private int Segment(double burnT)
    {
        var i = Array.BinarySearch(_burnTime, burnT);
        if (i < 0) i = ~i - 1;
        return Math.Clamp(i, 0, _burnTime.Length - 2);
    }

    // Mass falls with the fuel through a segment; at staging it drops at the segment's end. Past the recorded burn, the
    // last stage keeps burning at its last rate.
    private double MassAt(double burnT)
    {
        if (burnT >= BurnTimeS) return Math.Max(0.3 * _mass[^1], _mass[^1] - _lastFlow * (burnT - BurnTimeS));
        var i = Segment(burnT);
        var span = _burnTime[i + 1] - _burnTime[i];
        if (span <= 0 || _stage[i] != _stage[i + 1]) return _mass[i];
        var f = Math.Clamp((burnT - _burnTime[i]) / span, 0, 1);
        return _mass[i] + f * (_mass[i + 1] - _mass[i]);
    }


    private double MassFlowAt(double burnT) => burnT >= BurnTimeS ? _lastFlow : _massFlow[Segment(burnT)] is var m and > 0 ? m : _massFlow[Math.Min(Segment(burnT) + 1, _massFlow.Length - 1)];

    private double IspAt(double burnT, double pressureAtm) =>
        _isp.TryGetValue(_stage[Segment(burnT)], out var isp) ? Math.Max(1, isp.Vacuum + isp.PerAtm * pressureAtm) : 0;

    private double FlownPitch(double burnT)
    {
        var i = Segment(burnT);
        return _flownPitch[i].Pitch;
    }

    /// <summary>Σ drag ÷ Σ q per speed band (never below 0); bands with too little air take the nearest one that has enough.</summary>
    private static double[] Weighted(List<(double V, double Drag, double Q)> points, int bins, double width)
    {
        var sums = new (double Drag, double Q)[bins];
        foreach (var p in points)
        {
            var i = Math.Clamp((int)(p.V / width), 0, bins - 1);
            sums[i] = (sums[i].Drag + p.Drag, sums[i].Q + p.Q);
        }
        var enough = sums.Select(s => s.Q >= 20_000).ToArray(); // about 10 samples at 2 kPa
        var result = new double[bins];
        if (!enough.Any(e => e)) return result; // no air to speak of: no drag
        for (var i = 0; i < bins; i++)
        {
            var k = enough[i] ? i : Enumerable.Range(0, bins).Where(j => enough[j]).MinBy(j => Math.Abs(j - i));
            result[i] = Math.Max(0, sums[k].Drag / sums[k].Q);
        }
        return result;
    }

    /// <summary>Median per bin of (x, value); empty bins take the nearest filled one, all empty the fallback.</summary>
    private static double[] Binned(List<(double X, double Value)> points, int bins, double width, double fallback)
    {
        var result = new double[bins];
        var filled = new bool[bins];
        foreach (var group in points.GroupBy(p => Math.Clamp((int)(p.X / width), 0, bins - 1)))
        {
            var values = group.Select(p => p.Value).Order().ToList();
            result[group.Key] = values[values.Count / 2];
            filled[group.Key] = true;
        }
        if (!filled.Any(f => f)) return Enumerable.Repeat(fallback, bins).ToArray();
        for (var i = 0; i < bins; i++)
        {
            if (filled[i]) continue;
            var nearest = Enumerable.Range(0, bins).Where(k => filled[k]).MinBy(k => Math.Abs(k - i));
            result[i] = result[nearest];
        }
        return result;
    }
}
