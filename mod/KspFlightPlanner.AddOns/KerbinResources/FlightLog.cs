using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace KspFlightPlanner.KerbinResources
{
    /// <summary>A kerbal aboard a flight, with their trait (Pilot, Engineer, Scientist, Tourist) at the time.</summary>
    internal sealed class FlightCrew
    {
        public string Name = "";
        public string Trait = "";
    }

    /// <summary>
    /// One flight: a crewed craft left a place and arrived at another. A place is a craft (a station, by its vessel
    /// persistent id, its name for show), or 0 and a name for somewhere that isn't a craft (a launch site, an orbit round a
    /// body, the body a trip left free-flying from).
    /// </summary>
    internal sealed class Flight
    {
        public double DepartedUt, ArrivedUt;
        public uint FromId;
        public string FromName = "";
        public uint ToId;
        public string ToName = "";
        public string Vessel = "";

        /// <summary>Flown by Traffic Controller's autopilot (a route), not by the player.</summary>
        public bool Autopilot;

        public List<FlightCrew> Crew = new List<FlightCrew>();

        /// <summary>The same trip: from the same craft (or, from id 0, the same named place), to the same craft (or named place).</summary>
        internal bool Same(uint fromId, string fromName, uint toId, string toName) =>
            (toId != 0 ? ToId == toId : ToId == 0 && ToName == toName) && (fromId != 0 ? FromId == fromId : FromId == 0 && FromName == fromName);

        internal bool Aboard(string kerbal) => Crew.Any(c => c.Name == kerbal);

        internal void Save(ConfigNode n)
        {
            n.AddValue("departed", DepartedUt.ToString("R", CultureInfo.InvariantCulture));
            n.AddValue("arrived", ArrivedUt.ToString("R", CultureInfo.InvariantCulture));
            n.AddValue("fromId", FromId);
            n.AddValue("fromName", FromName);
            n.AddValue("toId", ToId);
            n.AddValue("toName", ToName);
            n.AddValue("vessel", Vessel);
            n.AddValue("autopilot", Autopilot);
            foreach (var c in Crew)
            {
                var k = n.AddNode("KERBAL");
                k.AddValue("name", c.Name);
                k.AddValue("trait", c.Trait);
            }
        }

        internal static Flight Load(ConfigNode n)
        {
            var f = new Flight
            {
                DepartedUt = Num(n, "departed"),
                ArrivedUt = Num(n, "arrived"),
                FromId = Id(n, "fromId"),
                FromName = n.GetValue("fromName") ?? "",
                ToId = Id(n, "toId"),
                ToName = n.GetValue("toName") ?? "",
                Vessel = n.GetValue("vessel") ?? "",
                Autopilot = n.GetValue("autopilot") == "True",
            };
            foreach (var k in n.GetNodes("KERBAL"))
                if (k.GetValue("name") is string name && name.Length > 0)
                    f.Crew.Add(new FlightCrew { Name = name, Trait = k.GetValue("trait") ?? "" });
            return f;
        }

        internal static double Num(ConfigNode n, string key) =>
            double.TryParse(n.GetValue(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

        internal static uint Id(ConfigNode n, string key) =>
            uint.TryParse(n.GetValue(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;
    }

    /// <summary>A crewed craft that has left somewhere and not arrived yet (one per craft, by vessel persistent id).</summary>
    internal sealed class OpenTrip
    {
        public uint VesselId;
        public string VesselName = "";
        public uint FromId;
        public string FromName = "";
        public double DepartedUt;

        internal void Save(ConfigNode n)
        {
            n.AddValue("vesselId", VesselId);
            n.AddValue("vesselName", VesselName);
            n.AddValue("fromId", FromId);
            n.AddValue("fromName", FromName);
            n.AddValue("departed", DepartedUt.ToString("R", CultureInfo.InvariantCulture));
        }

        internal static OpenTrip Load(ConfigNode n) => new OpenTrip
        {
            VesselId = Flight.Id(n, "vesselId"),
            VesselName = n.GetValue("vesselName") ?? "",
            FromId = Flight.Id(n, "fromId"),
            FromName = n.GetValue("fromName") ?? "",
            DepartedUt = Flight.Num(n, "departed"),
        };
    }

    /// <summary>
    /// Every crewed flight in this save, each with who was aboard, in the order they arrived. Flights the player flies
    /// count as well as Traffic Controller's routes. Per-kerbal counts (how often Jeb has flown Mun Station → Kerb
    /// Station) are worked out from the list, so later rules (training: who rode with whom) can read it as it happened.
    /// docs/MATH.md §11.12.
    /// </summary>
    internal sealed class FlightLog
    {
        private readonly List<Flight> _flights = new List<Flight>();
        private readonly Dictionary<uint, OpenTrip> _open = new Dictionary<uint, OpenTrip>();

        internal IReadOnlyList<Flight> Flights => _flights;
        internal IEnumerable<OpenTrip> OpenTrips => _open.Values;

        /// <summary>Everyone who has been aboard a logged flight.</summary>
        internal IEnumerable<string> Kerbals => _flights.SelectMany(f => f.Crew).Select(c => c.Name).Distinct();

        internal IEnumerable<Flight> FlightsOf(string kerbal) => _flights.Where(f => f.Aboard(kerbal));

        /// <summary>How often this kerbal has made this trip (0 if never). Directions count apart.</summary>
        internal int TimesFlown(string kerbal, uint fromId, string fromName, uint toId, string toName = "") =>
            _flights.Count(f => f.Aboard(kerbal) && f.Same(fromId, fromName, toId, toName));

        /// <summary>A crewed craft has left a place: its trip is open till it arrives somewhere else. Replaces any open trip it had.</summary>
        internal void Departed(Vessel ship, uint fromId, string fromName, double now) =>
            _open[ship.persistentId] = new OpenTrip { VesselId = ship.persistentId, VesselName = ship.vesselName, FromId = fromId, FromName = fromName, DepartedUt = now };

        /// <summary>
        /// A craft has arrived at a place (docked at it). If it had an open trip from somewhere else, the flight is logged with
        /// the crew aboard now and returned; null when it had none (it wasn't seen leaving), or it's back where it left (moved
        /// to another port, or a docking that was called off), or nobody's aboard.
        /// </summary>
        internal Flight Arrived(Vessel ship, uint toId, string toName, double now)
        {
            if (!_open.TryGetValue(ship.persistentId, out var trip)) return null;
            _open.Remove(ship.persistentId);
            if (toId != 0 && toId == trip.FromId) return null;
            var crew = CrewOf(ship);
            if (crew.Count == 0) return null;
            var flight = new Flight
            {
                DepartedUt = trip.DepartedUt,
                ArrivedUt = now,
                FromId = trip.FromId,
                FromName = trip.FromName,
                ToId = toId,
                ToName = toName,
                Vessel = ship.vesselName,
                Crew = crew,
            };
            Record(flight);
            return flight;
        }

        /// <summary>Logs a finished flight (Traffic Controller's routes log theirs this way).</summary>
        internal void Record(Flight flight) => _flights.Add(flight);

        internal void DropOpen(uint vesselId) => _open.Remove(vesselId);

        /// <summary>A kerbal was renamed: their flights follow.</summary>
        internal void RenameKerbal(string from, string to)
        {
            foreach (var f in _flights)
                foreach (var c in f.Crew)
                    if (c.Name == from) c.Name = to;
        }

        /// <summary>KSP gave a craft a new persistent id: its open trip follows it.</summary>
        internal void Renumber(uint from, uint to)
        {
            if (from == to || !_open.TryGetValue(from, out var trip)) return;
            _open.Remove(from);
            trip.VesselId = to;
            _open[to] = trip;
        }

        /// <summary>The kerbals aboard a craft now, with their traits.</summary>
        internal static List<FlightCrew> CrewOf(Vessel ship) =>
            ship.GetVesselCrew().Where(c => c != null).Select(c => new FlightCrew { Name = c.name, Trait = c.trait ?? "" }).ToList();

        /// <summary>A kerbal's trait from the save's roster ("" if they aren't in it).</summary>
        internal static string TraitOf(string kerbal)
        {
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null) return "";
            try { return roster[kerbal]?.trait ?? ""; }
            catch (Exception) { return ""; }
        }

        internal void Save(ConfigNode node)
        {
            foreach (var f in _flights) f.Save(node.AddNode("FLIGHT"));
            foreach (var t in _open.Values) t.Save(node.AddNode("OPENTRIP"));
        }

        internal void Load(ConfigNode node)
        {
            _flights.Clear();
            _open.Clear();
            if (node == null) return;
            foreach (var n in node.GetNodes("FLIGHT")) _flights.Add(Flight.Load(n));
            foreach (var n in node.GetNodes("OPENTRIP"))
            {
                var t = OpenTrip.Load(n);
                if (t.VesselId != 0) _open[t.VesselId] = t;
            }
        }
    }
}
