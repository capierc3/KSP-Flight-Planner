using KspFlightPlanner.Core.Models;

namespace KspFlightPlanner.Core.Ksp;

/// <summary>What the craft was doing along a stretch of the map path.</summary>
public enum MapLeg { Coast, Powered, Filled }

/// <summary>A point of the flight on the map, in metres from the centre of the body the map is around (looking down on its equator).</summary>
public sealed record MapPoint(double MetS, double X, double Y, string Body, double AltitudeM, MapLeg Leg);

/// <summary>A moon on a body's map: its orbit, and where it was when the craft arrived and left.</summary>
public sealed record MapMoon(string Name, double RadiusM, double SoiRadiusM, IReadOnlyList<(double X, double Y)> Orbit,
    IReadOnlyList<(string Label, double X, double Y)> Seen);

/// <summary>A moment marked on the map (launch, touchdown).</summary>
public sealed record MapMark(string Label, double X, double Y);

/// <summary>The flight seen from above one body: its path around that body and around its moons, drawn where the moons were.</summary>
public sealed record FlightMapView(string Body, double RadiusM, double AtmosphereM, IReadOnlyList<IReadOnlyList<MapPoint>> Segments,
    IReadOnlyList<MapMoon> Moons, IReadOnlyList<MapMark> Marks);

/// <summary>
/// The flight on a map, top-down on a body's equator. Each sample's position comes from its orbit (Kepler, §1.4), in the
/// same frame as the bodies' orbits; around a moon, the moon's position then is added. docs/MATH.md §8.8.
/// </summary>
public static class FlightMap
{
    /// <summary>Points along a moon's orbit drawn on the map.</summary>
    public const int MoonOrbitPoints = 180;

    /// <summary>Bodies a map can be drawn around: those flown around with the orbit's orientation recorded, in route order.</summary>
    public static IReadOnlyList<string> Views(FlightLog log, Func<string, CelestialBody?> body, Func<CelestialBody, CelestialBody?> parent)
    {
        var views = new List<string>();
        foreach (var name in log.Samples.Where(s => s.Elements is not null).Select(s => s.Body).Distinct())
        {
            if (body(name) is not { } b) continue;
            // A moon's flight also shows on its planet's map; list the planet first.
            if (parent(b) is { } p && log.Samples.Any(s => s.Body == p.Name) && !views.Contains(p.Name)) views.Add(p.Name);
            if (!views.Contains(name)) views.Add(name);
        }
        return views;
    }

    public static FlightMapView? Build(FlightLog log, IReadOnlyList<FilledPoint> filled, string view, Func<string, CelestialBody?> body,
        Func<CelestialBody, CelestialBody?> parent)
    {
        if (body(view) is not { } center) return null;

        // Where a craft around `around` is, seen from `center`: itself, or one of its moons at that moment.
        Vector3d? Offset(string around, double ut)
        {
            if (around == center.Name) return Vector3d.Zero;
            return body(around) is { Orbit: { } orbit } moon && parent(moon)?.Name == center.Name
                ? Kepler.StateAt(orbit, center.Mu, ut).Position
                : null;
        }

        // Samples and filled points, in time order.
        var points = new List<(double Ut, MapPoint? Point)>();
        foreach (var s in log.Samples)
        {
            var at = body(s.Body) is { } b ? FlightAnalysis.PositionAt(s, b, s.Ut) : null;
            var offset = Offset(s.Body, s.Ut);
            var leg = s.ThrustN > 0 ? MapLeg.Powered : MapLeg.Coast;
            points.Add((s.Ut, at is { } p && offset is { } o ? new MapPoint(s.MetS, p.X + o.X, p.Y + o.Y, s.Body, s.AltitudeM, leg) : null));
        }
        foreach (var f in filled)
        {
            var offset = Offset(f.Body, f.Ut);
            points.Add((f.Ut, f.Position is { } p && offset is { } o ? new MapPoint(f.MetS, p.X + o.X, p.Y + o.Y, f.Body, f.AltitudeM, MapLeg.Filled) : null));
        }
        points.Sort((a, b) => a.Ut.CompareTo(b.Ut));

        // Lines break where there's nothing to draw, a gap wasn't filled, or the craft changes body. A change of leg
        // (burning, coasting, worked out) starts a new segment from the last point, so the line stays joined.
        var segments = new List<IReadOnlyList<MapPoint>>();
        var current = new List<MapPoint>();
        double lastUt = double.NaN;
        void Flush()
        {
            if (current.Count > 1) segments.Add(current);
            current = [];
        }
        foreach (var (ut, point) in points)
        {
            if (point is null)
            {
                Flush();
                continue;
            }
            var prev = current.Count > 0 ? current[^1] : null;
            if (prev is null || prev.Body != point.Body
                || ut - lastUt > FlightAnalysis.FillAfterS && prev.Leg != MapLeg.Filled && point.Leg != MapLeg.Filled)
            {
                Flush();
            }
            else if (prev.Leg != point.Leg)
            {
                Flush();
                current.Add(prev with { Leg = point.Leg });
            }
            current.Add(point);
            lastUt = ut;
        }
        Flush();

        var moons = new List<MapMoon>();
        foreach (var name in log.Samples.Select(s => s.Body).Distinct())
        {
            if (name == center.Name || body(name) is not { Orbit: { } orbit } moon || parent(moon)?.Name != center.Name) continue;
            var period = Kepler.OrbitalPeriod(orbit, center.Mu);
            var path = Enumerable.Range(0, MoonOrbitPoints + 1)
                .Select(k => Kepler.StateAt(orbit, center.Mu, orbit.EpochS + period * k / MoonOrbitPoints).Position)
                .Select(p => (p.X, p.Y)).ToList();
            var seen = new List<(string, double, double)>();
            var visits = Visits(log.Samples, name);
            foreach (var (from, to) in visits)
            {
                var arrive = Kepler.StateAt(orbit, center.Mu, from.Ut).Position;
                var leave = Kepler.StateAt(orbit, center.Mu, to.Ut).Position;
                seen.Add(($"{name} when the craft arrived", arrive.X, arrive.Y));
                if (to.Ut - from.Ut > 60) seen.Add(($"{name} when the craft left", leave.X, leave.Y));
            }
            moons.Add(new MapMoon(name, moon.EquatorialRadiusM, moon.SoiRadiusM, path, seen));
        }

        var marks = new List<MapMark>();
        MapPoint? At(FlightSample s) => points.FirstOrDefault(p => p.Ut == s.Ut && p.Point?.Body == s.Body).Point;
        if (log.FromLaunch && log.Samples.Count > 0 && At(log.Samples[0]) is { } launch) marks.Add(new MapMark("Launch", launch.X, launch.Y));
        for (var i = 1; i < log.Samples.Count; i++)
        {
            var s = log.Samples[i];
            if (s.Situation is "LANDED" or "SPLASHED" && log.Samples[i - 1].Situation is not ("LANDED" or "SPLASHED" or "PRELAUNCH") && At(s) is { } down)
                marks.Add(new MapMark(s.Situation == "SPLASHED" ? $"Splashdown on {s.Body}" : $"Touchdown on {s.Body}", down.X, down.Y));
        }

        return new FlightMapView(center.Name, center.EquatorialRadiusM, center.Atmosphere?.HeightM ?? 0, segments, moons, marks);
    }

    /// <summary>Each stay in a body's sphere: its first and last sample.</summary>
    private static List<(FlightSample From, FlightSample To)> Visits(IReadOnlyList<FlightSample> samples, string body)
    {
        var visits = new List<(FlightSample, FlightSample)>();
        FlightSample? first = null, last = null;
        foreach (var s in samples)
        {
            if (s.Body == body) { first ??= s; last = s; continue; }
            if (first is not null) visits.Add((first, last!));
            first = last = null;
        }
        if (first is not null) visits.Add((first, last!));
        return visits;
    }
}
