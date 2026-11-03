using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core;

/// <summary>
/// Air density and speed of sound with height (docs/MATH.md §9.1). Pressure comes from the body's table; temperature from
/// where the table also has a density (T = P·M / ρ·R; a bake has it every km), else from the scale height. Density is then
/// P·M / R·T everywhere, so a table whose densities are rounded away up high still gives the thin air there.
/// </summary>
public sealed class AirModel
{
    public const double GasConstant = 8.314462618;
    private readonly Atmosphere _air;
    private readonly double _molarKg;
    private readonly double _gamma;
    private readonly (double H, double T)[] _temps;

    private AirModel(Atmosphere air, double molarKg, double gamma, (double, double)[] temps)
    {
        _air = air;
        _molarKg = molarKg;
        _gamma = gamma;
        _temps = temps;
    }

    /// <summary>The body's air; null when it has none (or no pressure data).</summary>
    public static AirModel? For(CelestialBody body)
    {
        if (body.Atmosphere is not { HeightM: > 0 } air || air.PressureAtmAt(0) is not > 0) return null;
        var molar = (air.MolarMassGPerMol ?? 28.9644) / 1000;
        var temps = air.PressureTable
            .Where(p => p.PressurePa > 0 && p.DensityKgM3 is > 0)
            .Select(p => (p.AltitudeM, T: p.PressurePa * molar / (p.DensityKgM3!.Value * GasConstant)))
            .Where(p => p.T is > 30 and < 3000)
            .ToArray();
        if (temps.Length == 0)
        {
            // An isothermal atmosphere with this scale height: H = R·T / (M·g).
            var t = air.ScaleHeightM is { } h ? h * body.SurfaceGravity * molar / GasConstant : 250;
            temps = [(0, t)];
        }
        return new AirModel(air, molar, air.AdiabaticIndex ?? 1.4, temps);
    }

    public double TopM => _air.HeightM;

    public double PressurePa(double altitudeM) => (_air.PressureAtmAt(altitudeM) ?? 0) * Atmosphere.PascalsPerAtm;

    /// <summary>Air temperature (K): straight lines between the heights where it's known, flat beyond.</summary>
    public double TemperatureK(double altitudeM)
    {
        var t = _temps;
        if (altitudeM <= t[0].H) return t[0].T;
        if (altitudeM >= t[^1].H) return t[^1].T;
        var i = 1;
        while (t[i].H < altitudeM) i++;
        var f = (altitudeM - t[i - 1].H) / (t[i].H - t[i - 1].H);
        return t[i - 1].T + f * (t[i].T - t[i - 1].T);
    }

    /// <summary>Air density (kg/m³) = P·M / (R·T); 0 above the air.</summary>
    public double Density(double altitudeM) => altitudeM >= TopM ? 0 : PressurePa(altitudeM) * _molarKg / (GasConstant * TemperatureK(altitudeM));

    /// <summary>Speed of sound (m/s) = √(γ·R·T / M).</summary>
    public double SpeedOfSound(double altitudeM) => Math.Sqrt(_gamma * GasConstant * TemperatureK(altitudeM) / _molarKg);
}

/// <summary>How a pass through the air ends.</summary>
public enum PassOutcome
{
    /// <summary>Back out into space on a closed orbit: another pass follows at the next periapsis.</summary>
    Orbiting,
    /// <summary>Back out still going fast enough to leave the planet.</summary>
    Escaping,
    /// <summary>Slowed too much to climb back out of the air: it comes down.</summary>
    ComesDown,
}

/// <summary>One trip through the air.</summary>
/// <param name="PeriapsisM">Periapsis altitude going in.</param>
/// <param name="LowestM">The lowest it actually got (a little under the periapsis, as drag pulls it down).</param>
/// <param name="SpeedLost">Speed taken off by drag over the pass (m/s).</param>
/// <param name="PeakG">The hardest the drag pulls, in g.</param>
/// <param name="TimeInAirS">From entering the air to leaving it (or to the ground).</param>
/// <param name="ApoapsisAfterM">Apoapsis altitude after the pass; null when escaping or coming down.</param>
/// <param name="PeriapsisAfterM">Periapsis altitude after the pass; null when coming down.</param>
/// <param name="PeriodAfterS">Time around the orbit after the pass, to the next pass; null unless orbiting.</param>
/// <param name="StartUt">When it enters the air, from the start of the first pass (s).</param>
public sealed record AerobrakePass(
    int Number,
    double PeriapsisM,
    double LowestM,
    double SpeedLost,
    double PeakG,
    double TimeInAirS,
    PassOutcome Outcome,
    double? ApoapsisAfterM,
    double? PeriapsisAfterM,
    double? PeriodAfterS,
    double StartS);

/// <summary>The orbit going into the first pass: periapsis altitude and orbital energy.</summary>
/// <param name="EnergyJPerKg">Specific orbital energy v²/2 − μ/r: negative for a closed orbit, positive arriving from far away.</param>
/// <param name="InclinationDeg">0 going round the way the planet spins (the usual), 180 against it: sets how fast the air meets it.</param>
public sealed record AerobrakeOrbit(double PeriapsisM, double EnergyJPerKg, double InclinationDeg = 0)
{
    /// <summary>A closed orbit from its apoapsis and periapsis altitudes.</summary>
    public static AerobrakeOrbit FromApsides(CelestialBody body, double periapsisM, double apoapsisM, double inclinationDeg = 0) =>
        new(periapsisM, -body.Mu / (2 * body.EquatorialRadiusM + periapsisM + apoapsisM), inclinationDeg);

    /// <summary>Arriving from far away with this speed left over (v∞, m/s).</summary>
    public static AerobrakeOrbit FromExcessSpeed(double periapsisM, double excessSpeed, double inclinationDeg = 0) =>
        new(periapsisM, excessSpeed * excessSpeed / 2, inclinationDeg);

    /// <summary>Apoapsis altitude, or null for a path that leaves.</summary>
    public double? ApoapsisM(CelestialBody body) =>
        EnergyJPerKg >= 0 ? null : -body.Mu / EnergyJPerKg - 2 * body.EquatorialRadiusM - PeriapsisM;
}

/// <summary>A moment in a pass: time since entering the air, height, speed against the stars, and climb rate.</summary>
public readonly record struct PassPoint(double T, double AltitudeM, double Speed, double VerticalSpeed);

/// <summary>The passes, first to last, and anything worth knowing about them.</summary>
public sealed record AerobrakePlan(IReadOnlyList<AerobrakePass> Passes, IReadOnlyList<string> Notes)
{
    public AerobrakePass? Last => Passes.Count == 0 ? null : Passes[^1];
}

/// <summary>
/// Aerobraking, pass by pass (docs/MATH.md §9.3): coast to the top of the air on the orbit, step through the air with
/// gravity and drag (the air turning with the planet), then read the orbit it leaves on. Repeat at each periapsis.
/// In the orbit's plane, over the equator; drag only (no lift), with the craft holding one attitude.
/// </summary>
public static class Aerobraking
{
    public const double G0 = 9.80665;
    public const int DefaultMaxPasses = 30;
    private const double StepS = 0.5;
    private const double MaxSecondsInAir = 4 * 3600;

    /// <param name="massKg">The craft's mass (it doesn't change: nothing burns during the passes).</param>
    /// <param name="stopAtApoapsisM">Stop once a pass brings the apoapsis down to this altitude (null: keep going).</param>
    public static AerobrakePlan Plan(CelestialBody body, AerobrakeOrbit start, double massKg, DragTable drag, int maxPasses = DefaultMaxPasses,
        double? stopAtApoapsisM = null)
    {
        var notes = new List<string>();
        if (AirModel.For(body) is not { } air)
            return new([], [$"{body.Name} has no air to brake in."]);
        if (start.PeriapsisM >= air.TopM)
            return new([], [$"Periapsis is above the air ({air.TopM / 1000:N0} km): nothing slows it."]);
        if (massKg <= 0) return new([], ["The craft needs a mass."]);

        var passes = new List<AerobrakePass>();
        var (pe, energy, clock) = (start.PeriapsisM, start.EnergyJPerKg, 0.0);
        for (var n = 1; n <= maxPasses; n++)
        {
            var pass = Pass(body, air, n, pe, energy, start.InclinationDeg, massKg, drag, clock);
            passes.Add(pass);
            if (pass.Outcome != PassOutcome.Orbiting || pass.PeriapsisAfterM is not { } nextPe || pass.PeriodAfterS is not { } period) break;
            if (pass.ApoapsisAfterM <= stopAtApoapsisM) break;
            if (nextPe >= air.TopM) { notes.Add("The periapsis climbed out of the air."); break; }
            energy = -body.Mu / (2 * body.EquatorialRadiusM + nextPe + pass.ApoapsisAfterM!.Value);
            pe = nextPe;
            clock = pass.StartS + period;
            if (n == maxPasses && stopAtApoapsisM is null) notes.Add($"Stopped after {maxPasses} passes; it keeps going round.");
        }

        if (passes[0].Outcome == PassOutcome.Escaping)
            notes.Add("The first pass doesn't slow it enough to stay: go lower, or brake with the engines too.");
        if (passes.Count > 1 && passes[^1].Outcome == PassOutcome.ComesDown)
            notes.Add($"Pass {passes[^1].Number} brings it down: raise the periapsis out of the air at apoapsis after pass {passes[^1].Number - 1} to stop there.");
        if (passes.Max(p => p.PeakG) > 10)
            notes.Add("Over 10 g at the bottom of a pass: crew and fragile parts may not take it.");
        return new(passes, notes);
    }

    /// <summary>One pass: from the top of the air going in, to the top of the air coming out (or the ground).</summary>
    private static AerobrakePass Pass(CelestialBody body, AirModel air, int number, double peAltM, double energy, double inclinationDeg,
        double massKg, DragTable drag, double startClock, List<PassPoint>? trace = null)
    {
        var mu = body.Mu;
        var radius = body.EquatorialRadiusM;
        var top = radius + air.TopM;
        var rp = radius + peAltM;

        // Where the orbit crosses the top of the air going down (perifocal frame: periapsis along +x).
        var vp = Math.Sqrt(2 * (energy + mu / rp));
        var h = rp * vp;
        var p = h * h / mu;
        var e = p / rp - 1;
        var cosNu = Math.Clamp((p / top - 1) / e, -1, 1);
        var nu = -Math.Acos(cosNu);
        double x = top * Math.Cos(nu), y = top * Math.Sin(nu);
        var vScale = mu / h;
        double vx = -vScale * Math.Sin(nu), vy = vScale * (e + Math.Cos(nu));

        // The air turns with the planet; going round at an angle, only that share of it runs along the path.
        return Fly(body, air, number, peAltM, [x, y, vx, vy], Spin(body, inclinationDeg), massKg, drag, startClock, trace);
    }

    /// <summary>
    /// The first pass, step by step (every 0.5 s from the top of the air): height, speed and climb rate, as the game would
    /// show them. For checking the live tracker against a pass whose drag is known.
    /// </summary>
    public static IReadOnlyList<PassPoint> Trace(CelestialBody body, AerobrakeOrbit start, double massKg, DragTable drag)
    {
        var air = AirModel.For(body) ?? throw new ArgumentException($"{body.Name} has no air.");
        var trace = new List<PassPoint>();
        Pass(body, air, 1, start.PeriapsisM, start.EnergyJPerKg, start.InclinationDeg, massKg, drag, 0, trace);
        return trace;
    }

    /// <summary>How fast the air turns under the path (rad/s): the planet's spin, the share of it along an orbit at this inclination.</summary>
    private static double Spin(CelestialBody body, double inclinationDeg) =>
        body.RotationPeriodS == 0 ? 0 : 2 * Math.PI / body.RotationPeriodS * Math.Cos(inclinationDeg * Math.PI / 180);

    /// <summary>
    /// The rest of a pass already under way, from the craft's height, speed and climb rate now (docs/MATH.md §9.5): where it
    /// leaves the air, and on what orbit. Stepped from here exactly as a planned pass is; speed lost and peak g are from now on.
    /// </summary>
    /// <param name="orbitalSpeed">Speed against the stars (not the ground).</param>
    /// <param name="verticalSpeed">Climb rate: negative going down.</param>
    public static AerobrakePass Continue(CelestialBody body, double altitudeM, double orbitalSpeed, double verticalSpeed, double inclinationDeg,
        double massKg, DragTable drag)
    {
        var air = AirModel.For(body) ?? throw new ArgumentException($"{body.Name} has no air.");
        var r = body.EquatorialRadiusM + altitudeM;
        var vr = Math.Clamp(verticalSpeed, -orbitalSpeed, orbitalSpeed);
        var vt = Math.Sqrt(Math.Max(0, orbitalSpeed * orbitalSpeed - vr * vr));
        // Periapsis of the orbit it's on now (drag aside).
        var h = r * vt;
        var eps = orbitalSpeed * orbitalSpeed / 2 - body.Mu / r;
        var e = Math.Sqrt(Math.Max(0, 1 + 2 * eps * h * h / (body.Mu * body.Mu)));
        var pe = h * h / body.Mu / (1 + e) - body.EquatorialRadiusM;
        return Fly(body, air, 0, pe, [r, 0, vr, vt], Spin(body, inclinationDeg), massKg, drag, 0);
    }

    /// <summary>
    /// The drag slowing the craft now: its pull (m/s²) and the orbital energy it takes per second (J/kg/s, the pull times
    /// the speed along it). §9.5 compares the energy the game shows going with this, to measure the drag.
    /// </summary>
    public static (double Accel, double EnergyRate) DragNow(CelestialBody body, double altitudeM, double orbitalSpeed, double verticalSpeed,
        double inclinationDeg, double massKg, DragTable drag)
    {
        if (AirModel.For(body) is not { } air) return (0, 0);
        var rho = air.Density(altitudeM);
        if (rho <= 0 || massKg <= 0) return (0, 0);
        var r = body.EquatorialRadiusM + altitudeM;
        var vr = Math.Clamp(verticalSpeed, -orbitalSpeed, orbitalSpeed);
        var vt = Math.Sqrt(Math.Max(0, orbitalSpeed * orbitalSpeed - vr * vr));
        var ut = vt - Spin(body, inclinationDeg) * r; // speed through the air, along the horizon
        var u = Math.Sqrt(vr * vr + ut * ut);
        if (u < 1e-6) return (0, 0);
        var accel = 0.5 * rho * u * u * drag.At(u / air.SpeedOfSound(altitudeM)) * drag.ThinAir(rho * u) / massKg;
        return (accel, accel * (vr * vr + vt * ut) / u);
    }

    /// <summary>Steps through the air from <paramref name="start"/> (x, y, vx, vy; the planet at the origin) until it's back out or down.</summary>
    private static AerobrakePass Fly(CelestialBody body, AirModel air, int number, double peAltM, double[] start, double spin, double massKg,
        DragTable drag, double startClock, List<PassPoint>? trace = null)
    {
        var mu = body.Mu;
        var radius = body.EquatorialRadiusM;
        var top = radius + air.TopM;
        double lost = 0, peakG = 0, lowest = double.MaxValue, t = 0;
        var state = start;
        var deriv = new double[4];
        var k = new double[4][];
        for (var i = 0; i < 4; i++) k[i] = new double[4];
        var tmp = new double[4];
        var outcome = PassOutcome.ComesDown;

        while (t < MaxSecondsInAir)
        {
            // RK4 over gravity and drag.
            Derivative(state, deriv);
            Array.Copy(deriv, k[0], 4);
            for (var i = 0; i < 4; i++) tmp[i] = state[i] + 0.5 * StepS * k[0][i];
            Derivative(tmp, k[1]);
            for (var i = 0; i < 4; i++) tmp[i] = state[i] + 0.5 * StepS * k[1][i];
            Derivative(tmp, k[2]);
            for (var i = 0; i < 4; i++) tmp[i] = state[i] + StepS * k[2][i];
            Derivative(tmp, k[3]);
            for (var i = 0; i < 4; i++) state[i] += StepS / 6 * (k[0][i] + 2 * k[1][i] + 2 * k[2][i] + k[3][i]);
            t += StepS;

            var (dragAccel, _, _) = Drag(state);
            lost += dragAccel * StepS;
            peakG = Math.Max(peakG, dragAccel / G0);
            var r = Math.Sqrt(state[0] * state[0] + state[1] * state[1]);
            lowest = Math.Min(lowest, r - radius);
            if (r <= radius) break;
            var radial = (state[0] * state[2] + state[1] * state[3]) / r;
            trace?.Add(new PassPoint(t, r - radius, Math.Sqrt(state[2] * state[2] + state[3] * state[3]), radial));
            if (r >= top && radial > 0)
            {
                outcome = PassOutcome.Orbiting;
                break;
            }
        }

        if (outcome == PassOutcome.ComesDown)
            return new(number, peAltM, Math.Max(0, lowest), lost, peakG, t, PassOutcome.ComesDown, null, null, null, startClock);

        // The orbit it leaves on.
        var rOut = Math.Sqrt(state[0] * state[0] + state[1] * state[1]);
        var v2 = state[2] * state[2] + state[3] * state[3];
        var eps = v2 / 2 - mu / rOut;
        var hOut = state[0] * state[3] - state[1] * state[2];
        var eOut = Math.Sqrt(Math.Max(0, 1 + 2 * eps * hOut * hOut / (mu * mu)));
        var pOut = hOut * hOut / mu;
        var peAfter = pOut / (1 + eOut) - radius;
        if (eps >= 0)
            return new(number, peAltM, lowest, lost, peakG, t, PassOutcome.Escaping, null, peAfter, null, startClock);
        var a = -mu / (2 * eps);
        var apAfter = a * (1 + eOut) - radius;
        if (apAfter < air.TopM) // can't happen once out, but keep the table honest
            return new(number, peAltM, lowest, lost, peakG, t, PassOutcome.ComesDown, null, null, null, startClock);
        return new(number, peAltM, lowest, lost, peakG, t, PassOutcome.Orbiting, apAfter, peAfter, 2 * Math.PI * Math.Sqrt(a * a * a / mu), startClock);

        (double Accel, double Ax, double Ay) Drag(double[] s)
        {
            var r = Math.Sqrt(s[0] * s[0] + s[1] * s[1]);
            var alt = r - radius;
            var rho = air.Density(alt);
            if (rho <= 0) return (0, 0, 0);
            // Speed through the air: the craft's, less the air's own (ω × r).
            var ux = s[2] + spin * s[1];
            var uy = s[3] - spin * s[0];
            var u = Math.Sqrt(ux * ux + uy * uy);
            if (u < 1e-6) return (0, 0, 0);
            var mach = u / air.SpeedOfSound(alt);
            var accel = 0.5 * rho * u * u * drag.At(mach) * drag.ThinAir(rho * u) / massKg;
            return (accel, -accel * ux / u, -accel * uy / u);
        }

        void Derivative(double[] s, double[] d)
        {
            var r2 = s[0] * s[0] + s[1] * s[1];
            var r = Math.Sqrt(r2);
            var g = -mu / (r2 * r);
            var (_, ax, ay) = Drag(s);
            d[0] = s[2];
            d[1] = s[3];
            d[2] = g * s[0] + ax;
            d[3] = g * s[1] + ay;
        }
    }

    /// <summary>The same arrival at several periapsis heights, to compare (each with its passes).</summary>
    public static IReadOnlyList<(double PeriapsisM, AerobrakePlan Plan)> Sweep(CelestialBody body, AerobrakeOrbit start, double massKg,
        DragTable drag, IEnumerable<double> periapsesM) =>
        periapsesM.Select(pe => (pe, Plan(body, start with
        {
            PeriapsisM = pe,
            // Keep the same apoapsis (or the same speed from far away) at each periapsis.
            EnergyJPerKg = start.ApoapsisM(body) is { } ap ? -body.Mu / (2 * body.EquatorialRadiusM + pe + ap) : start.EnergyJPerKg,
        }, massKg, drag))).ToList();

    /// <summary>Periapsis heights worth comparing: about 45% to 93% of the way up the air, rounded to the km.</summary>
    public static IReadOnlyList<double> SweepHeights(double airTopM)
    {
        var step = Math.Max(1_000, Math.Round(airTopM / 14 / 1_000) * 1_000);
        var list = new List<double>();
        for (var h = Math.Round(airTopM * 0.45 / step) * step; h < airTopM - step / 2; h += step) list.Add(h);
        return list;
    }
}
