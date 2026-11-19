using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace KspFlightPlanner.Roles
{
    /// <summary>A craft came within range of an anomaly: it's identified (docs/SURVEY.md "Landmarks are found by the survey").</summary>
    internal sealed class AnomalyVisit
    {
        public string Body = "";
        public string Name = "";
        public double Ut;
        public string Vessel = "";

        internal void Save(ConfigNode n)
        {
            n.AddValue("body", Body);
            n.AddValue("name", Name);
            n.AddValue("ut", Ut.ToString("R", CultureInfo.InvariantCulture));
            n.AddValue("vessel", Vessel);
        }

        internal static AnomalyVisit Load(ConfigNode n) => new AnomalyVisit
        {
            Body = n.GetValue("body") ?? "",
            Name = n.GetValue("name") ?? "",
            Ut = double.TryParse(n.GetValue("ut"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ut) ? ut : 0,
            Vessel = n.GetValue("vessel") ?? "",
        };

        internal string Line() => string.Join("\t", new[] { Body, Name, Ut.ToString("R", CultureInfo.InvariantCulture), Vessel });
    }

    /// <summary>
    /// The game's anomalies: every surface object (PQSSurfaceObject) on every body, mods' too. The planner fetches the list
    /// once per save; here, any craft that comes within range of one identifies it.
    /// </summary>
    internal static class Anomalies
    {
        /// <summary>Each anomaly: body, name, the game's display name, latitude and longitude (degrees).</summary>
        internal static List<string> Lines()
        {
            var lines = new List<string>();
            foreach (var body in FlightGlobals.Bodies)
            {
                if (body == null || body.pqsSurfaceObjects == null) continue;
                foreach (var o in body.pqsSurfaceObjects)
                {
                    if (o == null) continue;
                    var pos = o.transform.position;
                    var lat = body.GetLatitude(pos);
                    var lon = body.GetLongitude(pos);
                    if (double.IsNaN(lat) || double.IsNaN(lon)) continue;
                    lines.Add(string.Join("\t", new[]
                    {
                        body.bodyName, Clean(o.SurfaceObjectName), Clean(o.DisplaySurfaceObjectName),
                        lat.ToString("R", CultureInfo.InvariantCulture), Wrap(lon).ToString("R", CultureInfo.InvariantCulture),
                    }));
                }
            }
            return lines;
        }

        /// <summary>
        /// Craft (loaded or not, landed or flying) within <see cref="RolesSettings.VisitKm"/> of an anomaly not yet visited:
        /// each first visit is added. Debris and space objects don't count; a flag does (someone planted it there).
        /// </summary>
        internal static void LookForVisits(List<AnomalyVisit> visits, double now)
        {
            if (FlightGlobals.Vessels == null) return;
            var range = RolesSettings.Current.VisitKm * 1000.0;
            var seen = new HashSet<string>(visits.Select(v => v.Body + "|" + v.Name));
            foreach (var body in FlightGlobals.Bodies)
            {
                if (body == null || body.pqsSurfaceObjects == null || body.pqsSurfaceObjects.Length == 0) continue;
                var craft = FlightGlobals.Vessels.Where(v => v != null && v.mainBody == body &&
                                                             v.vesselType != VesselType.Debris && v.vesselType != VesselType.SpaceObject).ToList();
                if (craft.Count == 0) continue;
                foreach (var o in body.pqsSurfaceObjects)
                {
                    if (o == null || seen.Contains(body.bodyName + "|" + o.SurfaceObjectName)) continue;
                    Vector3d at = o.transform.position;
                    foreach (var v in craft)
                    {
                        if ((v.GetWorldPos3D() - at).magnitude > range) continue;
                        visits.Add(new AnomalyVisit { Body = body.bodyName, Name = o.SurfaceObjectName, Ut = now, Vessel = v.vesselName });
                        seen.Add(body.bodyName + "|" + o.SurfaceObjectName);
                        Debug.Log("[KspFlightPlanner] Roles: " + v.vesselName + " visited " + o.SurfaceObjectName + " on " + body.bodyName + ".");
                        ScreenMessages.PostScreenMessage(v.vesselName + " found " + (string.IsNullOrEmpty(o.DisplaySurfaceObjectName) ? o.SurfaceObjectName : o.DisplaySurfaceObjectName) +
                                                         " on " + body.bodyName, 8f, ScreenMessageStyle.UPPER_CENTER);
                        break;
                    }
                }
            }
        }

        private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ');

        private static double Wrap(double lon) => ((lon + 180) % 360 + 360) % 360 - 180;
    }
}
