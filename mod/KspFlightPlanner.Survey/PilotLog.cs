using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace KspFlightPlanner.Haulers
{
    /// <summary>One trip a pilot has made: from where to where (stations by id, names for show), how often, and when last.</summary>
    internal sealed class PilotFlight
    {
        /// <summary>The station it left (vessel persistent id), or 0 when it didn't leave from one: then <see cref="FromName"/> is the body it left from.</summary>
        public uint FromId;
        public string FromName = "";
        public uint ToId;
        public string ToName = "";
        public int TimesFlown;
        public double LastFlownUt;

        internal bool Same(uint fromId, string fromName, uint toId) =>
            ToId == toId && (fromId != 0 ? FromId == fromId : FromId == 0 && FromName == fromName);
    }

    /// <summary>
    /// Which trips each pilot has flown, kept in the save (with the hauler routes). A pilot gets one more flight on a
    /// trip each time a route they're aboard for docks at its target. Directions count apart: Mun Station → Kerbin
    /// Station isn't Kerbin Station → Mun Station. Read later by the crew rules (<see cref="HaulerRules"/>) and training.
    /// </summary>
    internal sealed class PilotLog
    {
        private readonly Dictionary<string, List<PilotFlight>> _byKerbal = new Dictionary<string, List<PilotFlight>>();

        internal IEnumerable<string> Kerbals => _byKerbal.Keys;

        internal IReadOnlyList<PilotFlight> Flights(string kerbal) =>
            _byKerbal.TryGetValue(kerbal, out var list) ? list : (IReadOnlyList<PilotFlight>)new PilotFlight[0];

        /// <summary>How often this kerbal has flown this trip (0 if never).</summary>
        internal int TimesFlown(string kerbal, uint fromId, string fromName, uint toId) =>
            Flights(kerbal).FirstOrDefault(f => f.Same(fromId, fromName, toId))?.TimesFlown ?? 0;

        /// <summary>Gives each pilot of the route one more flight on its trip. Once per route.</summary>
        internal void Credit(HaulerRoute route, double now)
        {
            if (route.Credited) return;
            route.Credited = true;
            foreach (var kerbal in route.Pilots)
            {
                if (!_byKerbal.TryGetValue(kerbal, out var list)) _byKerbal[kerbal] = list = new List<PilotFlight>();
                var flight = list.FirstOrDefault(f => f.Same(route.FromId, route.FromName, route.TargetId));
                if (flight == null)
                    list.Add(flight = new PilotFlight { FromId = route.FromId, FromName = route.FromName, ToId = route.TargetId });
                flight.FromName = route.FromName; // names follow renames
                flight.ToName = route.TargetName;
                flight.TimesFlown++;
                flight.LastFlownUt = now;
            }
            if (route.Pilots.Count > 0)
                HaulerScenario.Note(route, string.Join(", ", route.Pilots) + ": " + route.FromName + " → " + route.TargetName + " flown " +
                                           string.Join(", ", route.Pilots.Select(k => TimesFlown(k, route.FromId, route.FromName, route.TargetId) + "×")) + ".", false);
        }

        internal void Save(ConfigNode node)
        {
            foreach (var pair in _byKerbal)
            {
                var k = node.AddNode("KERBAL");
                k.AddValue("name", pair.Key);
                foreach (var f in pair.Value)
                {
                    var n = k.AddNode("FLIGHT");
                    n.AddValue("fromId", f.FromId);
                    n.AddValue("fromName", f.FromName);
                    n.AddValue("toId", f.ToId);
                    n.AddValue("toName", f.ToName);
                    n.AddValue("timesFlown", f.TimesFlown);
                    n.AddValue("lastFlown", f.LastFlownUt.ToString("R", CultureInfo.InvariantCulture));
                }
            }
        }

        internal void Load(ConfigNode node)
        {
            _byKerbal.Clear();
            if (node == null) return;
            foreach (var k in node.GetNodes("KERBAL"))
            {
                var name = k.GetValue("name");
                if (string.IsNullOrEmpty(name)) continue;
                var list = new List<PilotFlight>();
                foreach (var n in k.GetNodes("FLIGHT"))
                    list.Add(new PilotFlight
                    {
                        FromId = uint.TryParse(n.GetValue("fromId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var from) ? from : 0,
                        FromName = n.GetValue("fromName") ?? "",
                        ToId = uint.TryParse(n.GetValue("toId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var to) ? to : 0,
                        ToName = n.GetValue("toName") ?? "",
                        TimesFlown = int.TryParse(n.GetValue("timesFlown"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var times) ? times : 0,
                        LastFlownUt = double.TryParse(n.GetValue("lastFlown"), NumberStyles.Float, CultureInfo.InvariantCulture, out var last) ? last : 0,
                    });
                _byKerbal[name] = list;
            }
        }
    }
}
