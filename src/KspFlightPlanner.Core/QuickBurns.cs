using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core;

/// <summary>
/// One-off burns planned from where a craft is right now, without a mission (Fly mode's From here pane): circularize, or
/// move one apsis by burning at the other. Each is a single prograde/retrograde burn at an apsis, where the velocity is
/// horizontal, so the new speed comes straight from vis-viva. On a path that escapes the body (a flyby of a moon just
/// arrived at), the periapsis burns capture: circularize there, or set the apoapsis. docs/MATH.md §2.8.
/// </summary>
public static class QuickBurns
{
    /// <summary>Below this eccentricity the orbit counts as circular: there's no apsis to wait for, so the burn is right away.</summary>
    public const double CircularEccentricity = 0.002;

    /// <summary>
    /// The soonest a "circularize now" burn is planned when the craft is falling and can't wait for an apsis: shorter
    /// than the usual lead, since every second of waiting loses height.
    /// </summary>
    public const double FallingLeadS = 30;

    /// <summary>
    /// Makes the orbit circular at the next apoapsis (or periapsis): v = √(μ/r) there. The next apsis is taken however
    /// soon it is, since the one after may be a whole orbit away. When the craft would pass a periapsis in the atmosphere
    /// (or below the ground) before that apsis — just past the apoapsis after a launch — it circularizes
    /// <see cref="FallingLeadS"/> from now instead, turning the velocity horizontal at the circular speed there.
    /// </summary>
    /// <param name="leadS">On a round orbit, how far from <paramref name="now"/> the burn is (time to point the craft).</param>
    public static FlightBurn Circularize(CelestialBody body, OrbitalElements orbit, double now, double leadS, bool atApoapsis)
    {
        var mu = body.Mu;
        if (!atApoapsis && !ClearOfAtmosphere(body, orbit))
            throw new ArgumentException(orbit.Eccentricity >= 1
                ? $"The pass of {body.Name} is {Below(body)}: correct the path first (a small radial burn raises the periapsis)."
                : $"The periapsis is {Below(body)}: circularize at the apoapsis instead.");

        var ut = ApsisUt(orbit, mu, now, leadS, atApoapsis, body.Name);
        if (FallsFirst(body, orbit, now, ut))
        {
            ut = now + Math.Min(leadS, FallingLeadS);
            var state = Kepler.StateAt(orbit, mu, ut);
            var r = state.Position.Length;
            var target = HorizontalVelocity(state, Math.Sqrt(mu / r));
            return new FlightBurn($"Circularize now ({Km(r - body.EquatorialRadiusM)})", ut, NodeBurn.From(state, target - state.Velocity))
            {
                Note = $"Past the apoapsis with the periapsis {Below(body)}: there's no waiting for the next one.",
            };
        }

        var radius = Kepler.StateAt(orbit, mu, ut).Position.Length;
        var where = orbit.Eccentricity < CircularEccentricity ? "" : atApoapsis ? " at apoapsis" : " at periapsis";
        return AlongVelocity($"Circularize{where} ({Km(radius - body.EquatorialRadiusM)})", orbit, mu, ut, Math.Sqrt(mu / radius));
    }

    /// <summary>
    /// Moves the apoapsis (or periapsis) to <paramref name="altitudeM"/> by burning at the other apsis, which stays put:
    /// v = √(2μ·r₁ / (r₀·(r₀ + r₁))) at r₀ for the new far side r₁.
    /// </summary>
    public static FlightBurn SetApsis(CelestialBody body, OrbitalElements orbit, double now, double leadS, double altitudeM, bool apoapsis)
    {
        var mu = body.Mu;
        if (apoapsis && !ClearOfAtmosphere(body, orbit))
            throw new ArgumentException($"The periapsis is {Below(body)}, so there's no burning there: change the periapsis or circularize first.");
        var ut = ApsisUt(orbit, mu, now, leadS, atApoapsis: !apoapsis, body.Name);
        if (FallsFirst(body, orbit, now, ut))
            throw new ArgumentException($"You'll reach the periapsis {Below(body)} before the next apoapsis: circularize now instead.");
        var r0 = Kepler.StateAt(orbit, mu, ut).Position.Length;
        var r1 = body.EquatorialRadiusM + altitudeM;
        if (r1 <= 0) throw new ArgumentException("That altitude is below the centre of the body.");
        if (apoapsis && r1 < r0 - 1) throw new ArgumentException($"That's below the periapsis ({Km(r0 - body.EquatorialRadiusM)}): set the periapsis instead.");
        if (!apoapsis && r1 > r0 + 1) throw new ArgumentException($"That's above the apoapsis ({Km(r0 - body.EquatorialRadiusM)}): set the apoapsis instead.");

        var current = apoapsis ? orbit.SemiMajorAxisM * (1 + orbit.Eccentricity) : orbit.SemiMajorAxisM * (1 - orbit.Eccentricity);
        var verb = r1 >= current ? "Raise" : "Lower";
        var title = orbit.Eccentricity >= 1 ? $"Capture at {body.Name}, apoapsis {Km(altitudeM)}" : $"{verb} {(apoapsis ? "apoapsis" : "periapsis")} to {Km(altitudeM)}";
        return AlongVelocity(title, orbit, mu, ut, Math.Sqrt(2 * mu * r1 / (r0 * (r0 + r1))));
    }

    /// <summary>
    /// Turns the orbit to <paramref name="inclinationDeg"/> to the equator, keeping its shape and ascending node: one burn
    /// where the orbit crosses the equator, Δv = 2·v_h·sin(Δi/2), as matching a target's plane (§4.2) with the new plane as
    /// the target's. Of the two crossings it takes the cheaper, the higher (slower) one on an eccentric orbit. An orbit in
    /// the equator crosses it everywhere, so the burn is at the next apoapsis (soon, on a round orbit), which becomes a
    /// node. docs/MATH.md §2.10.
    /// </summary>
    public static FlightBurn ChangeInclination(CelestialBody body, OrbitalElements orbit, double now, double leadS, double inclinationDeg)
    {
        if (inclinationDeg is < 0 or > 180) throw new ArgumentException("The inclination is between 0° (equatorial, going east) and 180° (going west).");
        if (orbit.Eccentricity >= 1)
            throw new ArgumentException($"The craft is passing {body.Name}, not orbiting it: capture first, or plan Go to {body.Name} with an inclination, " +
                                        "which tilts the path on the way in for much less.");
        if (Math.Abs(inclinationDeg - orbit.InclinationDeg) < 0.01) throw new ArgumentException($"The orbit is already inclined {orbit.InclinationDeg:0.##}°.");

        var mu = body.Mu;
        var after = now + leadS;
        var lanDeg = orbit.LongitudeOfAscendingNodeDeg;
        if (orbit.InclinationDeg is < 0.05 or > 179.95)
        {
            // The node goes where the burn is; a minute's slack so the crossing search starts before it.
            var ut = ApsisUt(orbit, mu, now, leadS, atApoapsis: true, body.Name);
            var position = Kepler.StateAt(orbit, mu, ut).Position;
            lanDeg = Math.Atan2(position.Y, position.X) * 180 / Math.PI;
            after = Math.Max(now, ut - 60);
        }
        var (burn, _) = Rendezvous.MatchPlane(body, orbit, orbit with { InclinationDeg = inclinationDeg, LongitudeOfAscendingNodeDeg = lanDeg }, after);
        var turn = Math.Abs(inclinationDeg - orbit.InclinationDeg);
        return new FlightBurn($"Change inclination to {inclinationDeg:0.#}°", burn.Ut, burn.Burn)
        {
            Note = turn > 30
                ? "A big turn: it's cheapest where the craft is slowest, so raising the apoapsis first, turning there and lowering it again can cost less. " +
                  "Heading to another body, set the inclination in Go to instead: it's tilted on the way for far less."
                : null,
        };
    }

    /// <summary>
    /// The next apoapsis (or periapsis) after <paramref name="now"/>, however soon; <paramref name="leadS"/> from now on
    /// a circular orbit. Waiting for the lead would skip an apsis that's close and leave the burn a whole orbit later,
    /// which a suborbital craft never reaches and which breaks a relay's spacing. A path escaping
    /// <paramref name="bodyName"/> has no apoapsis and only one periapsis, which may have passed.
    /// </summary>
    public static double ApsisUt(OrbitalElements orbit, double mu, double now, double leadS, bool atApoapsis, string bodyName = "the body")
    {
        if (orbit.Eccentricity >= 1)
        {
            if (atApoapsis)
                throw new ArgumentException($"The craft is escaping {bodyName}, so there's no apoapsis: capture first (circularize at periapsis, or change the apoapsis).");
            return Kepler.NextPeriapsisUt(orbit, mu, now)
                   ?? throw new ArgumentException($"The craft is past its periapsis and on its way out of {bodyName}'s sphere of influence.");
        }
        return orbit.Eccentricity < CircularEccentricity ? now + leadS : Kepler.NextTimeAtMeanAnomaly(orbit, mu, now, atApoapsis ? Math.PI : 0);
    }

    /// <summary>The periapsis is above the atmosphere (or the ground, for a body without one).</summary>
    public static bool ClearOfAtmosphere(CelestialBody body, OrbitalElements orbit) =>
        orbit.SemiMajorAxisM * (1 - orbit.Eccentricity) > body.EquatorialRadiusM + body.AtmosphereHeightM;

    /// <summary>The craft passes a periapsis in the atmosphere (or below the ground) between <paramref name="now"/> and <paramref name="ut"/>.</summary>
    private static bool FallsFirst(CelestialBody body, OrbitalElements orbit, double now, double ut) =>
        !ClearOfAtmosphere(body, orbit) && orbit.Eccentricity >= CircularEccentricity
        && Kepler.NextTimeAtMeanAnomaly(orbit, body.Mu, now, 0) < ut;

    private static string Below(CelestialBody body) => body.AtmosphereHeightM > 0 ? "in the atmosphere" : "below the ground";

    /// <summary>A burn along the velocity that leaves the craft at <paramref name="newSpeed"/>.</summary>
    private static FlightBurn AlongVelocity(string title, OrbitalElements orbit, double mu, double ut, double newSpeed)
    {
        var speed = Kepler.StateAt(orbit, mu, ut).Velocity.Length;
        return new FlightBurn(title, ut, new NodeBurn(newSpeed - speed, 0, 0));
    }

    /// <summary>A velocity of <paramref name="speed"/> at right angles to the radius, in the orbit's plane and direction.</summary>
    internal static Vector3d HorizontalVelocity(StateVector state, double speed)
    {
        var up = state.Position.Normalized;
        var horizontal = state.Velocity - up * Vector3d.Dot(state.Velocity, up);
        return horizontal.Normalized * speed;
    }

    private static string Km(double metres) => $"{metres / 1000:#,0.#} km";
}
