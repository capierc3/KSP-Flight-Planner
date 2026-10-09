using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core;

/// <summary>
/// Turns custom locations into full orbits so the Transfer Planner can treat them like planets
/// (docs/MATH.md §3.6).
/// </summary>
public static class CustomLocationOrbits
{
    public const string BodyClassName = "Custom location";
    private const double DegToRad = Math.PI / 180;

    /// <summary>
    /// Orbital elements around <paramref name="body"/>. Planned locations get mean anomaly 0 at UT 0 — their position
    /// is arbitrary until they're built. Placed locations use the entered position.
    /// </summary>
    /// <param name="home">The home planet (Kerbin); needed for phase-angle entry.</param>
    /// <param name="star">The home planet's parent; needed for phase-angle entry.</param>
    public static OrbitalElements ToElements(CustomLocation location, CelestialBody body, CelestialBody? home = null, CelestialBody? star = null)
    {
        var rp = body.EquatorialRadiusM + location.PeriapsisAltitudeM;
        var ra = body.EquatorialRadiusM + location.ApoapsisAltitudeM;
        var a = (rp + ra) / 2;
        var e = OrbitMath.Eccentricity(rp, ra);

        if (!location.IsPlaced)
            return new OrbitalElements(a, e, location.InclinationDeg, location.LanDeg, location.ArgumentOfPeriapsisDeg, 0);

        if (location.Mode == PositionMode.Elements)
            return new OrbitalElements(a, e, location.InclinationDeg, location.LanDeg, location.ArgumentOfPeriapsisDeg,
                location.MeanAnomalyDeg * DegToRad, location.EpochUt);

        // Phase angle: the location was θ ahead of the home planet on the given date. For a circular orbit in the
        // reference plane (i = 0, Ω = 0, ω = 0), mean anomaly = true anomaly = longitude.
        ValidatePosition(location, body, home);
        var homeState = Kepler.StateAt(home!.Orbit!, star!.Mu, location.EpochUt);
        var homeLongitude = Math.Atan2(homeState.Position.Y, homeState.Position.X);
        return new OrbitalElements(a, 0, 0, 0, 0, homeLongitude + location.PhaseAngleDeg * DegToRad, location.EpochUt);
    }

    /// <summary>
    /// A placed location on <paramref name="orbit"/> around <paramref name="body"/> (a craft from the save or the live
    /// game): the reverse of <see cref="ToElements"/> for elements entry, angles wrapped to 0–360°.
    /// </summary>
    public static CustomLocation FromElements(long id, string name, CelestialBody body, OrbitalElements orbit)
    {
        var rp = orbit.SemiMajorAxisM * (1 - orbit.Eccentricity);
        var ra = orbit.SemiMajorAxisM * (1 + orbit.Eccentricity);
        return new CustomLocation(id, name, body.Name, rp - body.EquatorialRadiusM, ra - body.EquatorialRadiusM, orbit.InclinationDeg,
            IsPlaced: true, Mode: PositionMode.Elements, LanDeg: Wrap(orbit.LongitudeOfAscendingNodeDeg),
            ArgumentOfPeriapsisDeg: Wrap(orbit.ArgumentOfPeriapsisDeg), MeanAnomalyDeg: Wrap(orbit.MeanAnomalyAtEpochRad / DegToRad),
            EpochUt: Math.Max(0, orbit.EpochS));
    }

    private static double Wrap(double degrees) => (degrees % 360 + 360) % 360;

    /// <summary>Checks the position inputs of a placed location.</summary>
    public static void ValidatePosition(CustomLocation location, CelestialBody body, CelestialBody? home)
    {
        if (!location.IsPlaced) return;
        if (location.Mode == PositionMode.PhaseAngle)
        {
            if (home is null || home.ParentId != body.Id)
                throw new ArgumentException($"Phase-angle entry only works for orbits around the star (measured from {home?.Name ?? "the home planet"}); " +
                                            $"for an orbit around {body.Name}, use orbital elements.");
            if (!location.IsCircular || location.InclinationDeg != 0)
                throw new ArgumentException("Phase-angle entry needs a circular orbit with 0° inclination; use orbital elements for other orbits.");
        }
        if (location.LanDeg is < 0 or >= 360 || location.ArgumentOfPeriapsisDeg is < 0 or >= 360)
            throw new ArgumentException("Longitude of ascending node and argument of periapsis must be between 0° and 360°.");
        if (location.EpochUt < 0)
            throw new ArgumentException("The date of the position must be on or after Year 1, Day 1.");
    }

    /// <summary>
    /// A stand-in body for the Transfer Planner: the location's orbit, no gravity (μ = 0) and a 1 m radius, so
    /// "capture" becomes a straight speed match (rendezvous) and "ejection" a straight departure burn.
    /// </summary>
    public static CelestialBody ToBody(CustomLocation location, CelestialBody body, CelestialBody? home = null, CelestialBody? star = null) => new()
    {
        Id = -location.Id,
        SystemId = body.SystemId,
        ParentId = body.Id,
        Name = location.Name,
        BodyClass = BodyClassName,
        MassKg = 0,
        Mu = 0,
        EquatorialRadiusM = 1,
        SoiRadiusM = 1e12,
        Color = body.Color,
        Orbit = ToElements(location, body, home, star),
    };

    /// <summary>Angle of the location ahead of the home planet at <paramref name="ut"/>, in its orbital plane (−180°…180°).</summary>
    public static double PhaseAngleFromHome(OrbitalElements locationOrbit, CelestialBody star, CelestialBody home, double ut)
    {
        var target = Kepler.StateAt(locationOrbit, star.Mu, ut).Position;
        var h = Kepler.StateAt(home.Orbit!, star.Mu, ut);
        var normal = Vector3d.Cross(h.Position, h.Velocity).Normalized;
        var radial = h.Position.Normalized;
        var ahead = Vector3d.Cross(normal, radial);
        var deg = Math.Atan2(Vector3d.Dot(target, ahead), Vector3d.Dot(target, radial)) * 180 / Math.PI;
        return ((deg + 180) % 360 + 360) % 360 - 180;
    }
}
