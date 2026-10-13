using System.Globalization;
using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core;

/// <summary>One stop of a generated Δv map, before it is stored (see <see cref="DvMapNode"/> for the fields).</summary>
public sealed record DvMapNodeSpec(
    string Key, string? ParentKey, string BodyName, string Kind, string Label, double Dv,
    double PlaneChangeDv = 0, bool AerobrakeDown = false, bool AerobrakeUp = false, bool JetEnginesPossible = false,
    string? FlightTime = null, double? AltitudeM = null);

/// <summary>
/// Builds the Δv map from the bodies' physical data: a tree rooted at the home body's surface, with every
/// edge worked out from two-body orbital mechanics (Hohmann transfers, patched-conic escapes and captures).
/// Only the climb through an atmosphere uses an estimate (<see cref="AscentLossDv"/>). See docs/MATH.md §6.1.
/// </summary>
public static class DeltaVMapGenerator
{
    /// <summary>Standard gravity, for expressing surface gravity in g.</summary>
    private const double G0 = 9.80665;

    /// <summary>Map values are rounded to this, like the published community maps.</summary>
    private const double RoundTo = 10;

    /// <summary>The map for the system containing <paramref name="homeName"/>, parents listed before children.</summary>
    public static IReadOnlyList<DvMapNodeSpec> Generate(IReadOnlyList<CelestialBody> bodies, string homeName)
    {
        var home = bodies.Single(b => b.Name == homeName);
        var star = bodies.Single(b => b.Id == home.ParentId);
        if (star.ParentId is not null || home.Orbit is null)
            throw new ArgumentException($"{homeName} must orbit the system's star.", nameof(homeName));

        var map = new Builder(bodies);
        var homeLow = LowOrbitRadius(home);
        var aeroHome = home.HasAtmosphere;

        var surface = map.Add(home, "surface", null, "Surface", "Surface", 0);
        var low = map.Add(home, "lo", surface, "LowOrbit", LowOrbitLabel(home), SurfaceToLowOrbitDv(home)!.Value,
            aeroUp: aeroHome, altitudeM: home.DefaultParkingAltitudeM);

        if (home.SynchronousRadiusM is { } rSync && home.SynchronousAltitudeM is { } syncAlt && rSync > homeLow)
            map.Add(home, "keo", low, "Synchronous",
                $"{(home.Name == "Kerbin" ? "Keostationary" : "Synchronous")} orbit ({Km(syncAlt)})",
                OrbitMath.Hohmann(home.Mu, homeLow, rSync).Total, aeroUp: aeroHome, altitudeM: syncAlt);

        // Moons of home: a Hohmann transfer from low orbit to the moon's orbit.
        foreach (var moon in map.MoonsOf(home))
        {
            var leg = Transfer(home.Mu, homeLow, moon.Orbit!.SemiMajorAxisM, moon.Orbit.InclinationDeg);
            var intercept = map.Add(moon, "intercept", low, "Intercept", "Intercept", leg.Departure,
                plane: leg.PlaneChange, aeroUp: aeroHome, flightTime: leg.TimeS);
            map.Arrive(moon, intercept, leg.ArrivalVInf);
        }

        // Leaving home: an ellipse out to the SOI edge, then the rest of the hyperbolic ejection on each transfer.
        var escapeDv = new OrbitShape(home.Mu, homeLow, home.SoiRadiusM).PeriapsisSpeed - OrbitMath.CircularVelocity(home.Mu, homeLow);
        var escape = map.Add(home, "soi", low, "Elliptical", "Escape (elliptical orbit to SOI edge)", escapeDv, aeroUp: aeroHome);
        double Ejection(double vInf) => OrbitMath.HyperbolicBurnDv(home.Mu, homeLow, vInf) - escapeDv;

        var rHome = home.Orbit.SemiMajorAxisM;

        // The star: drop the periapsis to its low orbit, then circularize there.
        var starLow = LowOrbitRadius(star);
        var dive = new OrbitShape(star.Mu, starLow, rHome);
        var starIntercept = map.Add(star, "intercept", escape, "Intercept", "Intercept",
            Ejection(OrbitMath.CircularVelocity(star.Mu, rHome) - dive.ApoapsisSpeed),
            aeroUp: aeroHome, flightTime: dive.PeriodS / 2);
        var starLowNode = map.Add(star, "lo", starIntercept, "LowOrbit", LowOrbitLabel(star),
            dive.PeriapsisSpeed - OrbitMath.CircularVelocity(star.Mu, starLow),
            aeroDown: star.HasAtmosphere, altitudeM: star.DefaultParkingAltitudeM);
        map.Add(star, "surface", starLowNode, "Surface", "Surface", SurfaceToLowOrbitDv(star, allowStar: true)!.Value,
            aeroDown: star.HasAtmosphere);

        // The other planets: a Hohmann transfer between the two orbits around the star.
        foreach (var planet in map.MoonsOf(star).Where(p => p != home))
        {
            var leg = Transfer(star.Mu, rHome, planet.Orbit!.SemiMajorAxisM, planet.Orbit.InclinationDeg - home.Orbit.InclinationDeg);
            var intercept = map.Add(planet, "intercept", escape, "Intercept", "Intercept", Ejection(leg.Departure),
                plane: leg.PlaneChange, aeroUp: aeroHome, flightTime: leg.TimeS);
            map.Arrive(planet, intercept, leg.ArrivalVInf);
        }

        return map.Nodes;
    }

    /// <summary>
    /// Δv from the surface to the body's default low orbit (<see cref="CelestialBody.DefaultParkingAltitudeM"/>):
    /// the ideal climb (a Hohmann transfer from the surface, helped by the body's rotation) plus
    /// <see cref="AscentLossDv"/> on bodies with an atmosphere. Null for stars and bodies without gravity.
    /// </summary>
    public static double? SurfaceToLowOrbitDv(CelestialBody body, bool allowStar = false)
    {
        if (body.Mu <= 0 || body.EquatorialRadiusM <= 0 || (body.BodyClass == "Star" && !allowStar)) return null;
        return Round(IdealAscentDv(body) + AscentLossDv(body));
    }

    /// <summary>
    /// Surface to low orbit with no air and instant burns: a Hohmann transfer from a point at rest on the equator,
    /// less the equator's rotation speed (launching east).
    /// </summary>
    public static double IdealAscentDv(CelestialBody body)
    {
        var (mu, r0, r1) = (body.Mu, body.EquatorialRadiusM, LowOrbitRadius(body));
        var climb = new OrbitShape(mu, r0, r1);
        var rotation = body.RotationPeriodS == 0 ? 0 : 2 * Math.PI * r0 / Math.Abs(body.RotationPeriodS);
        return climb.PeriapsisSpeed - rotation + OrbitMath.CircularVelocity(mu, r1) - climb.ApoapsisSpeed;
    }

    /// <summary>
    /// Rough allowance for gravity and drag losses climbing out of an atmosphere (0 if airless):
    /// 1,000 m/s per g of surface gravity, scaled up for air thicker than 1 atm by max(1, 0.4 + 0.6·p).
    /// A rule of thumb, not a simulation; the real figure depends on the rocket and how it's flown.
    /// </summary>
    public static double AscentLossDv(CelestialBody body)
    {
        if (!body.HasAtmosphere) return 0;
        var pressureAtm = body.Atmosphere!.SeaLevelPressureAtm ?? 1;
        return 1000 * body.SurfaceGravity / G0 * Math.Max(1, 0.4 + 0.6 * pressureAtm);
    }

    private static double LowOrbitRadius(CelestialBody body) => body.EquatorialRadiusM + body.DefaultParkingAltitudeM;

    /// <summary>
    /// A Hohmann transfer from a circular orbit of radius r1 to r2 around one body. Departure and arrival are the
    /// speed differences at each end: the burn itself around this body, or the v∞ (for escaping/capturing at the bodies there).
    /// The plane change is the worst case: the whole inclination difference, made at the transfer's faster end.
    /// </summary>
    private static TransferLeg Transfer(double mu, double r1, double r2, double deltaInclinationDeg)
    {
        var path = new OrbitShape(mu, Math.Min(r1, r2), Math.Max(r1, r2));
        var (speedAt1, speedAt2) = r1 < r2 ? (path.PeriapsisSpeed, path.ApoapsisSpeed) : (path.ApoapsisSpeed, path.PeriapsisSpeed);
        return new TransferLeg(Math.Abs(speedAt1 - OrbitMath.CircularVelocity(mu, r1)), Math.Abs(OrbitMath.CircularVelocity(mu, r2) - speedAt2),
            OrbitMath.PlaneChangeDv(path.PeriapsisSpeed, deltaInclinationDeg), path.PeriodS / 2);
    }

    private readonly record struct TransferLeg(double Departure, double ArrivalVInf, double PlaneChange, double TimeS);

    private static double Round(double dv) => Math.Round(dv / RoundTo) * RoundTo;

    private static string LowOrbitLabel(CelestialBody body) => $"Low orbit ({Km(body.DefaultParkingAltitudeM)})";

    private static string Km(double altitudeM) => altitudeM >= 1e7
        ? string.Create(CultureInfo.InvariantCulture, $"{altitudeM / 1e6:N0} Mm")
        : string.Create(CultureInfo.InvariantCulture, $"{altitudeM / 1000:N0} km");

    /// <summary>A rough trip time on the Kerbin calendar: hours under two days, then days, then years and days.</summary>
    private static string FlightTime(double seconds)
    {
        var cal = GameCalendar.Kerbin;
        if (seconds < 2 * cal.SecondsPerDay) return $"{Math.Round(seconds / 3600):0} hours";
        var days = (int)Math.Round(seconds / cal.SecondsPerDay);
        if (days < cal.DaysPerYear) return $"{days} days";
        var years = days / cal.DaysPerYear;
        return $"{years} year{(years == 1 ? "" : "s")} {days % cal.DaysPerYear} days";
    }

    private sealed class Builder(IReadOnlyList<CelestialBody> bodies)
    {
        public List<DvMapNodeSpec> Nodes { get; } = [];

        /// <summary>Bodies orbiting <paramref name="parent"/>, nearest first.</summary>
        public IEnumerable<CelestialBody> MoonsOf(CelestialBody parent) =>
            bodies.Where(b => b.ParentId == parent.Id && b.Orbit is not null).OrderBy(b => b.Orbit!.SemiMajorAxisM);

        public string Add(CelestialBody body, string suffix, string? parent, string kind, string label, double dv,
            double plane = 0, bool aeroDown = false, bool aeroUp = false, bool jet = false, double? flightTime = null,
            double? altitudeM = null)
        {
            var key = $"{body.Name.ToLowerInvariant()}.{suffix}";
            Nodes.Add(new DvMapNodeSpec(key, parent, body.Name, kind, label, Round(dv), Round(plane), aeroDown, aeroUp, jet,
                flightTime is { } t ? FlightTime(t) : null, altitudeM));
            return key;
        }

        /// <summary>
        /// Capture at a body arriving with excess speed v∞, then its surface. A body with moons is captured into an
        /// ellipse reaching its SOI edge (cheap), and its moons hang off that ellipse; otherwise straight into low orbit.
        /// </summary>
        public void Arrive(CelestialBody body, string intercept, double vInf)
        {
            var (mu, rLow) = (body.Mu, LowOrbitRadius(body));
            var aero = body.HasAtmosphere;
            var hyperbolicSpeed = Math.Sqrt(vInf * vInf + 2 * mu / rLow);
            var moons = MoonsOf(body).ToList();
            string low;
            if (moons.Count == 0)
            {
                low = Add(body, "lo", intercept, "LowOrbit", LowOrbitLabel(body), OrbitMath.HyperbolicBurnDv(mu, rLow, vInf),
                    aeroDown: aero, altitudeM: body.DefaultParkingAltitudeM);
            }
            else
            {
                var capture = new OrbitShape(mu, rLow, body.SoiRadiusM);
                var elliptical = Add(body, "elliptical", intercept, "Elliptical",
                    $"Elliptical orbit ({Km(body.SoiAltitudeM)} × {Km(body.DefaultParkingAltitudeM)})",
                    hyperbolicSpeed - capture.PeriapsisSpeed, aeroDown: aero);
                low = Add(body, "lo", elliptical, "LowOrbit", LowOrbitLabel(body),
                    capture.PeriapsisSpeed - OrbitMath.CircularVelocity(mu, rLow), aeroDown: aero, altitudeM: body.DefaultParkingAltitudeM);

                // To a moon: at the ellipse's periapsis, lower the apoapsis to the moon's orbit.
                foreach (var moon in moons)
                {
                    var rMoon = moon.Orbit!.SemiMajorAxisM;
                    var path = new OrbitShape(mu, rLow, rMoon);
                    var moonIntercept = Add(moon, "intercept", elliptical, "Intercept", "Intercept",
                        capture.PeriapsisSpeed - path.PeriapsisSpeed,
                        plane: OrbitMath.PlaneChangeDv(path.PeriapsisSpeed, moon.Orbit.InclinationDeg),
                        flightTime: path.PeriodS / 2);
                    Arrive(moon, moonIntercept, Math.Abs(OrbitMath.CircularVelocity(mu, rMoon) - path.ApoapsisSpeed));
                }
            }

            Add(body, "surface", low, "Surface", "Surface", SurfaceToLowOrbitDv(body, allowStar: true)!.Value,
                aeroDown: aero, jet: body.Atmosphere?.HasOxygen == true);
        }
    }
}
