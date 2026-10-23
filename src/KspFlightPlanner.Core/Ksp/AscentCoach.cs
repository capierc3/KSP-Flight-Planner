using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core.Ksp;

/// <summary>One thing the ascent did that cost Δv, and what to try instead.</summary>
/// <param name="Good">True for what went well, false for a problem, null for a remark.</param>
public sealed record AscentAdvice(string Title, string Detail, bool? Good);

/// <summary>Pitch at one point of the climb: where the nose was and where the craft was actually going.</summary>
public sealed record PitchPoint(double MetS, double AltitudeM, double PitchDeg, double ProgradeDeg);

/// <summary>
/// How the climb to orbit went, read from the flight's samples: how much was lost to pointing off prograde, when the
/// turn started and reached 45°, the launch TWR, and advice. docs/MATH.md §8.10.
/// </summary>
/// <param name="SteeringLoss">Δv lost to thrust not along the path: ∫ (F/m)·(1 − cos δ) dt, δ = pitch − climb angle.</param>
/// <param name="DragLoss">The rest of drag &amp; steering (§8.6) once steering is taken out (never below 0).</param>
/// <param name="MeanOffPrograde">Average pitch − climb angle while in thick air (q ≥ 1 kPa), degrees; positive is above prograde.</param>
/// <param name="HasPitch">The flight's pitch was recorded against the horizon (older logs: the pitch figures are null).</param>
public sealed record AscentDiagnosis(
    bool HasPitch,
    double? SteeringLoss,
    double? DragLoss,
    double? LaunchTwr,
    double? TurnStartSpeed,
    double? Pitch45AltitudeM,
    double? MeanOffPrograde,
    IReadOnlyList<PitchPoint> Pitch,
    IReadOnlyList<AscentAdvice> Advice);

public static class AscentCoach
{
    // Rules of thumb for KSP ascents from Kerbin-like bodies (docs/MATH.md §8.10).
    public const double LowTwr = 1.25, HighTwr = 2.2;
    public const double LateTurnSpeed = 120, EarlyTurnSpeed = 20;
    public const double Late45M = 15_000, Early45M = 5_000;
    public const double OffProgradeDeg = 3;
    public const double HighQPa = 25_000;
    public const double ThickAirPa = 1_000;

    /// <summary>Below this surface speed the path is too short to have a direction worth comparing the nose with.</summary>
    public const double ProgradeFromSpeed = 50;

    /// <summary>The climb angle of the path over the ground (degrees above the horizon).</summary>
    public static double ProgradePitch(FlightSample s) =>
        Math.Asin(Math.Clamp(s.VerticalSpeed / Math.Max(1, s.SurfaceSpeed), -1, 1)) * 180 / Math.PI;

    /// <summary>The ascent's samples: from the launch to the sample in orbit (§8.6).</summary>
    public static IReadOnlyList<FlightSample> AscentSamples(FlightLog log, AscentReport ascent) =>
        log.Samples.TakeWhile(s => s.MetS <= ascent.EndMetS).ToList();

    public static AscentDiagnosis? Diagnose(FlightLog log, AscentReport? ascent, CelestialBody body)
    {
        if (ascent is null || !log.FromLaunch) return null;
        var s = AscentSamples(log, ascent);
        if (s.Count < 3) return null;
        var hasPitch = s.All(x => x.SurfaceAttitude);

        double OffBy(FlightSample x) => x.SurfaceSpeed >= ProgradeFromSpeed ? x.PitchDeg - ProgradePitch(x) : 0;
        double Lost(FlightSample x) => x.ThrustN > 0 && x.MassKg > 0 ? x.ThrustN / x.MassKg * (1 - Math.Cos(OffBy(x) * Math.PI / 180)) : 0;

        var steering = 0.0;
        for (var i = 1; i < s.Count; i++) steering += (Lost(s[i - 1]) + Lost(s[i])) / 2 * (s[i].Ut - s[i - 1].Ut);
        var drag = Math.Max(0, ascent.DragAndSteeringLoss - steering);
        if (!hasPitch) steering = drag = double.NaN;

        var g0 = body.Mu / (body.EquatorialRadiusM * body.EquatorialRadiusM);
        var first = s.FirstOrDefault(x => x.ThrustN > 0);
        double? twr = first is null ? null : first.ThrustN / (first.MassKg * g0);
        double? turnSpeed = !hasPitch ? null : s.FirstOrDefault(x => x.PitchDeg < 85 && x.ThrustN > 0) is { } turn ? turn.SurfaceSpeed : null;
        double? at45 = !hasPitch ? null : s.FirstOrDefault(x => x.PitchDeg <= 45) is { } p45 ? p45.AltitudeM : null;
        var thick = !hasPitch ? [] : s.Where(x => x.DynamicPressurePa >= ThickAirPa && x.SurfaceSpeed >= ProgradeFromSpeed).ToList();
        var meanOff = thick.Count > 0 ? thick.Average(OffBy) : 0;

        var advice = new List<AscentAdvice>();
        if (twr is { } t)
            advice.Add(t < LowTwr
                ? new($"Low launch TWR ({t:N2})", $"The rocket climbs slowly, so gravity takes more ({ascent.GravityLoss:N0} m/s here). Aim for 1.3–1.8 at launch: more thrust or a lighter first stage.", false)
                : t > HighTwr
                    ? new($"High launch TWR ({t:N2})", "Fast in the thickest air: drag and heat cost more. Throttle back low down, or use a smaller first stage.", false)
                    : new($"Launch TWR {t:N2}", "In the efficient range (1.3–1.8 or so).", true));
        if (turnSpeed is { } v)
            advice.Add(v > LateTurnSpeed
                ? new($"Started the turn late ({v:N0} m/s)", "Going straight up for long costs gravity loss. Start pitching over at 30–100 m/s, then follow prograde.", false)
                : v < EarlyTurnSpeed
                    ? new($"Started the turn very early ({v:N0} m/s)", "Turning before the rocket has speed lets it fall over and fly flat through thick air.", false)
                    : new($"Turn started at {v:N0} m/s", "A good time to start pitching over.", true));
        if (at45 is { } h45)
            advice.Add(h45 > Late45M
                ? new($"45° came late, at {h45 / 1000:N1} km", $"The climb stayed steep for too long: gravity loss was {ascent.GravityLoss:N0} m/s. Around 8–12 km is usual (sooner for a high TWR).", false)
                : h45 < Early45M
                    ? new($"45° came early, at {h45 / 1000:N1} km", "Flat low down means a long time in thick air. Around 8–12 km is usual.", false)
                    : new($"45° at {h45 / 1000:N1} km", "About right for an efficient turn.", true));
        if (thick.Count > 0)
            advice.Add(meanOff > OffProgradeDeg
                ? new($"Nose {meanOff:N0}° above prograde in thick air", $"Turning slower than the path wants to: steering cost {steering:N0} m/s. Pitch over a little more, then hold prograde (SAS prograde) through the air.", false)
                : meanOff < -OffProgradeDeg
                    ? new($"Nose {-meanOff:N0}° below prograde in thick air", $"Pitching over faster than the path: steering cost {steering:N0} m/s, and more drag and heat from the angle of attack. Hold prograde instead.", false)
                    : new("Followed prograde through the air", $"Steering cost only {steering:N0} m/s.", true));
        if (!hasPitch)
            advice.Add(new("No pitch for this flight", "It was recorded before the planner read pitch against the horizon, so the turn can't be judged. Flights recorded from now on can.", null));
        if (ascent.MaxQPa > HighQPa)
            advice.Add(new($"Max Q {ascent.MaxQPa / 1000:N1} kPa at {ascent.MaxQAltitudeM / 1000:N1} km",
                "Very fast low down: drag climbs with speed squared. Throttle back through 5–15 km, or turn a little later.", false));

        var pitch = s.Where(x => x.SurfaceSpeed >= ProgradeFromSpeed / 2)
            .Select(x => new PitchPoint(x.MetS, x.AltitudeM, hasPitch ? x.PitchDeg : double.NaN, ProgradePitch(x))).ToList();
        return new AscentDiagnosis(hasPitch, hasPitch ? steering : null, hasPitch ? drag : null, twr, turnSpeed, at45,
            hasPitch && thick.Count > 0 ? meanOff : null, pitch, advice);
    }
}
