using System;

namespace KspFlightPlanner.PowerHeat
{
    /// <summary>Day and night where a craft is, or will be (MATH.md §13.1–13.2).</summary>
    internal static class Sunlight
    {
        /// <summary>The Sun: the body every other body ends up orbiting.</summary>
        internal static CelestialBody Sun => Planetarium.fetch != null ? Planetarium.fetch.Sun : FlightGlobals.Bodies?[0];

        /// <summary>A body's mean distance from the Sun (m): its own orbit, or for a moon its planet's. 0 for the Sun.</summary>
        internal static double SunDistance(CelestialBody body)
        {
            var b = body;
            while (b != null && b.referenceBody != null && b.referenceBody != b && b.referenceBody != Sun) b = b.referenceBody;
            return b == null || b == Sun || b.orbit == null ? 0 : b.orbit.semiMajorAxis;
        }

        /// <summary>Night on the surface at the equator (s): half the solar day, which KSP keeps as solarDayLength.</summary>
        internal static double SurfaceNight(CelestialBody body) => body.solarDayLength / 2;

        internal static double SurfaceDay(CelestialBody body) => body.solarDayLength - SurfaceNight(body);

        /// <summary>
        /// The longest night in a circular orbit at this altitude (s): the Sun in the orbit's plane, so the craft crosses
        /// the body's full shadow, taken as a cylinder as wide as the body: T · asin(R / a) / π.
        /// </summary>
        internal static double OrbitNight(CelestialBody body, double altitude, out double period)
        {
            var a = body.Radius + Math.Max(0, altitude);
            period = 2 * Math.PI * Math.Sqrt(a * a * a / body.gravParameter);
            return period * Math.Asin(body.Radius / a) / Math.PI;
        }

        /// <summary>Sun now, how long until that changes (s), and how long the coming night lasts (s). Null when it can't tell.</summary>
        internal sealed class Now
        {
            public bool Sunlit;
            public double UntilChange;
            public double NightLength;
            public bool Never; // polar day or night: no change coming
        }

        /// <summary>
        /// A landed craft: the Sun's height over its horizon over the solar day, from its latitude φ and the Sun's
        /// declination δ against the body's axis: sin e = sin φ sin δ + cos φ cos δ cos H, with the hour angle H turning once
        /// per solar day. Terrain and the planet eclipsing its moon aren't counted.
        /// </summary>
        internal static Now Landed(Vessel v)
        {
            var body = v.mainBody;
            var sun = Sun;
            if (body == null || sun == null || body == sun || body.solarDayLength <= 0) return null;
            // The spin axis in world space: KSP moves the ground at angularVelocity × r (getRFrmVel), so east is n × up.
            // (CelestialBody.RotationAxis is a constant up, not in world space.)
            if (body.angularVelocity.sqrMagnitude < 1e-20) return null;
            var n = body.angularVelocity.normalized;
            var pos = v.GetWorldPos3D();
            var up = (pos - body.position).normalized;
            var s = (sun.position - pos).normalized;
            var sinPhi = Vector3d.Dot(up, n);
            var sinDelta = Vector3d.Dot(s, n);
            var upP = (up - n * sinPhi).normalized;
            var sP = (s - n * sinDelta).normalized;
            var east = Vector3d.Cross(n, upP);
            // Hour angle: 0 at noon, negative in the morning (the Sun still east, ahead of the turning ground).
            var h = -Math.Atan2(Vector3d.Dot(east, sP), Vector3d.Dot(upP, sP));
            return Turning(Math.Asin(Clamp(sinPhi)), Math.Asin(Clamp(sinDelta)), h, body.solarDayLength);
        }

        /// <summary>The closed form behind Landed, apart from KSP so it can be checked: φ, δ, H in radians, S in seconds.</summary>
        internal static Now Turning(double phi, double delta, double h, double solarDay)
        {
            var c = -Math.Tan(phi) * Math.Tan(delta);
            var now = new Now();
            if (c >= 1 || c <= -1)
            {
                now.Never = true;
                now.Sunlit = c <= -1;
                now.NightLength = now.Sunlit ? 0 : solarDay;
                return now;
            }
            var h0 = Math.Acos(c); // the Sun sets at +h0 and rises at −h0
            var w = 2 * Math.PI / solarDay;
            h = Wrap(h);
            now.Sunlit = Math.Abs(h) < h0;
            now.NightLength = (2 * Math.PI - 2 * h0) / w;
            now.UntilChange = now.Sunlit ? (h0 - h) / w : Wrap2Pi(-h0 - h) / w;
            return now;
        }

        /// <summary>
        /// A craft in orbit: steps along its orbit for one period and finds where it enters and leaves the body's shadow
        /// (a cylinder as wide as the body, away from the Sun). The Sun's direction is held for the orbit.
        /// </summary>
        internal static Now Orbiting(Vessel v)
        {
            var body = v.mainBody;
            var sun = Sun;
            var o = v.orbit;
            if (body == null || sun == null || body == sun || o == null || o.eccentricity >= 1 || o.period <= 0) return null;
            var ut = Planetarium.GetUniversalTime();
            var s = SunFromBody(body, ut);
            const int steps = 720;
            var dt = o.period / steps;
            bool Dark(double t)
            {
                var p = o.getRelativePositionAtUT(t);
                var along = Vector3d.Dot(p, s);
                return along < 0 && (p - s * along).magnitude < body.Radius;
            }
            var now = new Now { Sunlit = !Dark(ut) };
            double change = -1, nightStart = -1, nightEnd = -1;
            var was = !now.Sunlit;
            for (var i = 1; i <= 2 * steps; i++)
            {
                var t = ut + i * dt;
                var dark = Dark(t);
                if (dark != was)
                {
                    if (change < 0) change = t - ut;
                    if (dark && nightStart < 0) nightStart = t;
                    else if (!dark && nightStart >= 0 && nightEnd < 0) nightEnd = t;
                }
                was = dark;
                if (nightEnd >= 0 && change >= 0) break;
            }
            if (change < 0)
            {
                now.Never = true;
                now.NightLength = now.Sunlit ? 0 : o.period;
                return now;
            }
            now.UntilChange = change;
            // In the dark now: the night ends at the first change, and lasted from its start (not known): give what's left.
            now.NightLength = !now.Sunlit ? change : nightEnd > nightStart ? nightEnd - nightStart : 0;
            return now;
        }

        /// <summary>The Sun's direction from a body, in the orbit frame getRelativePositionAtUT uses: minus the body's own path out to the Sun.</summary>
        private static Vector3d SunFromBody(CelestialBody body, double ut)
        {
            var p = Vector3d.zero;
            var b = body;
            var sun = Sun;
            while (b != null && b != sun && b.orbit != null)
            {
                p += b.orbit.getRelativePositionAtUT(ut);
                b = b.referenceBody;
            }
            return (-p).normalized;
        }

        private static double Clamp(double x) => Math.Max(-1, Math.Min(1, x));

        private static double Wrap(double a)
        {
            a = Wrap2Pi(a);
            return a > Math.PI ? a - 2 * Math.PI : a;
        }

        private static double Wrap2Pi(double a)
        {
            a %= 2 * Math.PI;
            return a < 0 ? a + 2 * Math.PI : a;
        }
    }
}
