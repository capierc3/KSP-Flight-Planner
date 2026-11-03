using KspFlightPlanner.Core.Ksp;
using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core;

/// <summary>What to do before the next pass: the periapsis to set, or (finishing) the burns that end the braking.</summary>
/// <param name="PeriapsisM">The periapsis to burn to: in the air for another pass, or above it to finish.</param>
/// <param name="Finish">The braking is done (or no pass from here is safe): lift the periapsis out of the air.</param>
/// <param name="ApoapsisM">Finishing into a target orbit: the apoapsis to bring down to afterwards, at the new periapsis.</param>
public sealed record AerobrakeStep(double PeriapsisM, bool Finish, string Why, double? ApoapsisM = null);

/// <summary>
/// One pass through the air as it was flown, measured from the game: what the model said going in, what happened, and how
/// strong the drag really was against the model.
/// </summary>
public sealed record FlownPass(int Number, double EntryUt, double ExitUt, double? PredictedApoapsisM, double? ApoapsisM, double? PeriapsisM,
    double SpeedLost, double PeakG, double? DragFactor, bool CameDown);

/// <summary>
/// Aerobraking while it's flown (docs/MATH.md §9.5): the next step from the orbit the craft is on, the burn that sets its
/// periapsis, and a tracker that measures the drag from the game's readings.
/// </summary>
public static class AerobrakeLive
{
    /// <summary>A periapsis this close to the one wanted needs no burn (m).</summary>
    public const double CloseEnoughM = 300;

    /// <summary>
    /// The next step for an aim, from the orbit the craft is on now (<paramref name="now"/>: its periapsis, energy and
    /// inclination; a path from far away has no apoapsis). Gentlest and target
    /// orbit stop once the apoapsis is down to theirs (within 2% of its radius); every aim stops when no pass from here is
    /// safe with 30% more drag. <paramref name="drag"/> is the model's times the drag measured so far.
    /// </summary>
    /// <param name="ownPeriapsisM">The periapsis typed in, for <see cref="AerobrakeAim.Own"/>.</param>
    /// <param name="maxPasses">The most passes from here (gentlest, target orbit).</param>
    public static AerobrakeStep Next(CelestialBody body, AerobrakeOrbit now, double massKg, DragTable drag,
        AerobrakeAim aim, double ownPeriapsisM, double targetApoapsisM, double targetPeriapsisM, int maxPasses)
    {
        var apoapsisM = now.ApoapsisM(body) ?? double.PositiveInfinity;
        var air = body.AtmosphereHeightM;
        var clear = air + 1_000;
        var finishPe = aim == AerobrakeAim.TargetOrbit ? targetPeriapsisM : clear;
        var goal = aim is AerobrakeAim.Gentlest or AerobrakeAim.TargetOrbit ? targetApoapsisM : (double?)null;
        var start = now;

        // Within 2% of the goal's radius counts as there (as for the aims, §9.4).
        var reached = goal is { } g && apoapsisM <= g + 0.02 * (body.EquatorialRadiusM + g);
        AerobrakeStep Finish(string why) => new(finishPe, true, why, aim == AerobrakeAim.TargetOrbit && !reached ? targetApoapsisM : null);

        if (reached)
            return Finish($"The apoapsis is down to {Km(apoapsisM)}: lift the periapsis out of the air at apoapsis.");

        switch (aim)
        {
            case AerobrakeAim.Own:
                return AerobrakeAims.SafeThrough(body, AerobrakeAims.At(body, start, ownPeriapsisM), massKg, drag, 1)
                    ? new(ownPeriapsisM, false, "Your periapsis: the next pass is safe with 30% more drag.")
                    : new(ownPeriapsisM, false, "Your periapsis: careful, with 30% more drag the next pass would bring you down.");
            case AerobrakeAim.FewestPasses:
                return AerobrakeAims.FewestPasses(body, start, massKg, drag) is { } f && SafeThrough1(f.PeriapsisM)
                    ? new(f.PeriapsisM, false, "As low as is safe for the next pass, with 30% more drag.")
                    : Finish("No pass from here is safe with 30% more drag: lift the periapsis out of the air at apoapsis.");
            case AerobrakeAim.Gentlest:
                return AerobrakeAims.Gentlest(body, start, massKg, drag, targetApoapsisM, maxPasses) is { } s
                    ? new(s.PeriapsisM, false, s.Why)
                    : Finish($"No periapsis gets the apoapsis down to {Km(targetApoapsisM)} in {maxPasses} more passes safely: lift the periapsis out " +
                             "of the air (or allow more passes).");
            default:
                if (AerobrakeAims.TargetOrbit(body, start, massKg, drag, targetApoapsisM, targetPeriapsisM, maxPasses) is not { } t)
                    return Finish("No safe pass from here gets closer: finish with burns.");
                // Burning now beats another pass: the passes would stop where it is.
                if (t.StopApoapsisM is { } stop && stop >= apoapsisM - 1_000)
                    return Finish("Another pass isn't safe enough to help: finish with burns.");
                return new(t.PeriapsisM, false, t.Why);
        }

        bool SafeThrough1(double pe) => AerobrakeAims.SafeThrough(body, AerobrakeAims.At(body, start, pe), massKg, drag, 1);
    }

    /// <summary>
    /// Moves the periapsis to <paramref name="periapsisM"/>. At the next apoapsis when that comes first (the cheapest place,
    /// and the apoapsis stays put); otherwise, on the way in, a burn along the horizon <paramref name="leadS"/> from now,
    /// sized by search (§9.5).
    /// </summary>
    public static FlightBurn SetPeriapsis(CelestialBody body, OrbitalElements orbit, double now, double leadS, double periapsisM)
    {
        var mu = body.Mu;
        var after = now + leadS;
        if (orbit.Eccentricity < 1 && Kepler.NextPeriapsisUt(orbit, mu, after) is { } nextPe)
        {
            var period = 2 * Math.PI * Math.Sqrt(Math.Pow(orbit.SemiMajorAxisM, 3) / mu);
            var nextAp = nextPe - period / 2;
            if (nextAp >= after)
            {
                try { return QuickBurns.SetApsis(body, orbit, now, leadS, periapsisM, apoapsis: false); }
                catch (ArgumentException) { /* burn on the way in instead */ }
            }
        }

        var s = Kepler.StateAt(orbit, mu, after);
        var r = s.Position.Length;
        var v = s.Velocity.Length;
        var sinGamma = Math.Clamp(Vector3d.Dot(s.Position, s.Velocity) / (r * v), -1, 1);
        var cosGamma = Math.Sqrt(1 - sinGamma * sinGamma);
        var horizontal = v * cosGamma;
        var target = body.EquatorialRadiusM + periapsisM;
        if (target >= r) throw new ArgumentException($"That periapsis is above the craft ({Km(r - body.EquatorialRadiusM)}).");

        // k along the horizon: −v_h stops all sideways motion (periapsis 0); more raises the periapsis.
        NodeBurn Burn(double k) => new(k * cosGamma, 0, -k * sinGamma);
        double Pe(double k) => Kepler.PeriapsisRadius(Rendezvous.OrbitAfter(after, Burn(k), orbit, mu));
        double lo = -horizontal, hi = Math.Max(1, v);
        while (Pe(hi) < target && hi < 1e5) hi *= 2;
        for (var i = 0; i < 80 && hi - lo > 1e-4; i++)
        {
            var mid = (lo + hi) / 2;
            if (Pe(mid) < target) lo = mid; else hi = mid;
        }
        var burn = Burn((lo + hi) / 2);
        var verb = target >= Kepler.PeriapsisRadius(orbit) ? "Raise" : "Lower";
        return new FlightBurn($"{verb} periapsis to {Km(periapsisM)}", after, burn);
    }

    internal static string Km(double metres) => (metres / 1000).ToString(metres < 100_000 ? "N1" : "N0") + " km";
}

/// <summary>
/// Follows a craft through its passes from the game's readings (twice a second), and measures the drag (§9.5): the orbital
/// energy v²/2 − μ/r only changes by drag when the engines are off, so what the game shows going, against what the model
/// says should go over the same moments, is how strong the drag really is.
/// </summary>
public sealed class AerobrakeTracker(CelestialBody body, double inclinationDeg, DragTable drag)
{
    /// <summary>Readings further apart than this (game seconds) aren't compared: too much happened between.</summary>
    private const double MaxGapS = 3;

    /// <summary>The model's energy loss over the pass needs to reach this (J/kg) before the measured drag means anything.</summary>
    private const double MinEnergyJPerKg = 200;

    private readonly double _mu = body.Mu;
    private readonly double _radius = body.EquatorialRadiusM;
    private readonly double _top = body.AtmosphereHeightM;
    private FlightSample? _last;
    private double _modelRate; // the model's energy loss rate at _last
    private double _measured, _model; // energy lost this pass: from the game, from the model (J/kg)
    private double _entrySpeed, _entryUt, _peakG;
    private double? _predictedAp;
    private readonly List<FlownPass> _flown = [];

    public CelestialBody Body => body;
    public DragTable Drag => drag;
    public double InclinationDeg => inclinationDeg;

    /// <summary>In the air now.</summary>
    public bool InAir { get; private set; }

    /// <summary>The passes flown since the tracker started, first to last.</summary>
    public IReadOnlyList<FlownPass> Flown => _flown;

    /// <summary>The drag against the model this pass so far (1 = as worked out); null until there's enough to tell.</summary>
    public double? PassFactor => Math.Abs(_model) >= MinEnergyJPerKg ? _measured / _model : null;

    /// <summary>
    /// The drag against the model to plan with: this pass's when there's enough of it, else the last pass's that measured
    /// one; null before any.
    /// </summary>
    public double? DragFactor => PassFactor ?? _flown.LastOrDefault(p => p.DragFactor is not null)?.DragFactor;

    /// <summary>The drag to plan with: the model's scaled by what's been measured.</summary>
    public DragTable Measured => DragFactor is { } f ? drag.Scaled(f) : drag;

    /// <summary>Energy taken this pass (J/kg): from the game, and what the model says over the same readings.</summary>
    public (double Measured, double Model) EnergyLost => (_measured, _model);

    /// <summary>Speed lost to drag this pass so far: the energy taken, as speed at the height it entered.</summary>
    public double SpeedLost => _entrySpeed > 0 ? _entrySpeed - Math.Sqrt(Math.Max(0, _entrySpeed * _entrySpeed - 2 * _measured)) : 0;

    public double PeakG => _peakG;

    /// <summary>
    /// The model's exit apoapsis for this pass, worked out when it entered the air (null: it came down, or not in a pass).
    /// </summary>
    public double? PredictedApoapsisM => _predictedAp;

    /// <summary>Takes the next reading. Returns the pass just finished, when this reading is the one that leaves the air.</summary>
    public FlownPass? Add(FlightSample s)
    {
        var inAir = s.AltitudeM < _top && s.Body == body.Name;
        var energy = s.OrbitalSpeed * s.OrbitalSpeed / 2 - _mu / (_radius + s.AltitudeM);
        var (accel, rate) = inAir
            ? Aerobraking.DragNow(body, s.AltitudeM, s.OrbitalSpeed, s.VerticalSpeed, inclinationDeg, s.MassKg, drag)
            : (0, 0);
        FlownPass? ended = null;

        if (inAir && !InAir)
        {
            // Into the air: a new pass, and what the model says it'll do from here.
            (_measured, _model, _peakG, _entrySpeed, _entryUt) = (0, 0, 0, s.OrbitalSpeed, s.Ut);
            _predictedAp = Forecast(s, drag) is { Outcome: PassOutcome.Orbiting, ApoapsisAfterM: { } ap } ? ap : null;
        }
        else if (inAir && _last is { } last && s.Ut > last.Ut && s.Ut - last.Ut <= MaxGapS && s.ThrustN <= 0 && last.ThrustN <= 0)
        {
            var lastEnergy = last.OrbitalSpeed * last.OrbitalSpeed / 2 - _mu / (_radius + last.AltitudeM);
            _measured += lastEnergy - energy;
            _model += (_modelRate + rate) / 2 * (s.Ut - last.Ut);
        }
        if (inAir) _peakG = Math.Max(_peakG, accel * (PassFactor ?? 1) / Aerobraking.G0);

        if (!inAir && InAir && _last is not null)
        {
            var cameDown = s.Situation is "LANDED" or "SPLASHED";
            ended = new FlownPass(_flown.Count + 1, _entryUt, s.Ut, _predictedAp,
                double.IsFinite(s.ApoapsisAltitudeM) ? s.ApoapsisAltitudeM : null, double.IsFinite(s.PeriapsisAltitudeM) ? s.PeriapsisAltitudeM : null,
                SpeedLost, _peakG, PassFactor, cameDown);
            _flown.Add(ended);
        }
        else if (InAir && s.Situation is "LANDED" or "SPLASHED")
        {
            ended = new FlownPass(_flown.Count + 1, _entryUt, s.Ut, _predictedAp, null, null, SpeedLost, _peakG, PassFactor, true);
            _flown.Add(ended);
            inAir = false;
        }

        InAir = inAir;
        _modelRate = rate;
        _last = s;
        return ended;
    }

    /// <summary>The rest of the pass from this reading, with this drag.</summary>
    public AerobrakePass Forecast(FlightSample s, DragTable with) =>
        Aerobraking.Continue(body, s.AltitudeM, s.OrbitalSpeed, s.VerticalSpeed, inclinationDeg, s.MassKg, with);
}
