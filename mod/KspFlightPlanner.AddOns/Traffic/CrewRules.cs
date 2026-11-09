using System.Collections.Generic;
using System.Linq;
using KspFlightPlanner.KerbinResources;
using CrewLog = KspFlightPlanner.KerbinResources.FlightLog;
using DutyShift = KspFlightPlanner.KerbinResources.DutyShift;

namespace KspFlightPlanner.Traffic
{
    /// <summary>Where one pilot stands on one trip: flights flown on it, and training rides taken while not yet cleared.</summary>
    internal sealed class PilotStanding
    {
        public int Flights;
        public int TrainingRides;

        /// <summary>Cleared to fly the trip as a route craft's pilot.</summary>
        public bool Cleared => Flights > 0;
    }

    /// <summary>
    /// The game's crew rules for route craft, in one place: who may fly a route, and which stations direct their traffic.
    /// The flying code only asks; the rules live here. docs/TRAFFIC-CONTROLLER.md "Crew rules design".
    ///
    /// Route qualification: a crewed route craft starts a route only with a pilot aboard who is cleared for that trip, in
    /// that direction. A pilot is cleared by flying it once by hand (the player flying, the pilot aboard), or by riding
    /// along on a route flown by a pilot with enough flights on it to train others. Probe craft (no crew) aren't held by
    /// this; the controller rules for them come later.
    /// </summary>
    internal static class CrewRules
    {
        /// <summary>
        /// May this craft start this route? Asked once, when the route starts, before anything undocks, with the craft's own
        /// crew (a docked craft's, not its station's). <paramref name="route"/> has its trip (from, to) set. A route planned
        /// again on the way (it keeps its start time) was cleared when it started.
        /// </summary>
        internal static bool CanFly(Route route, IEnumerable<string> crew, out string why)
        {
            why = "";
            if (route.StartedUt > 0) return true;
            var s = TrafficSettings.Current;
            if (!s.RouteQualification) return true;
            var names = crew?.ToList() ?? new List<string>();
            if (names.Count == 0) return true;
            var pilots = names.Where(k => CrewLog.TraitOf(k) == "Pilot").ToList();
            if (pilots.Count == 0)
            {
                why = "a crewed craft needs a pilot aboard to fly a route, and nobody aboard is one";
                return false;
            }
            var log = KerbinResourcesScenario.Instance?.Log;
            if (log == null) return true;
            var standings = Standings(log, route.FromId, route.FromName, route.TargetId, route.TargetName);
            if (pilots.Any(p => standings.TryGetValue(p, out var st) && st.Cleared)) return true;
            why = "no pilot aboard (" + string.Join(", ", pilots) + ") has flown " + route.FromName + " → " + route.TargetName +
                  ". Fly it once by hand with a pilot aboard, or send a pilot with " + s.MentorFlights + "+ flights on it along to train one";
            return false;
        }

        /// <summary>
        /// May the physics flyer take this route craft over near the player? Its route was cleared when it started, so yes
        /// for now; the controller rules for probe craft go here.
        /// </summary>
        internal static bool MayTakeOver(Route route, Vessel ship, out string why)
        {
            why = "";
            return true;
        }

        /// <summary>
        /// Every pilot's standing on one trip, worked out from the flight log in the order the flights arrived:
        /// <list type="bullet">
        /// <item>Flown by hand: every pilot aboard flew it (+1 flight each).</item>
        /// <item>Autopilot, with a cleared pilot aboard: each cleared pilot +1 flight. A pilot not yet cleared takes a training
        /// ride if a pilot aboard had the mentor's flights before this one, and is cleared (+1 flight) after enough rides.</item>
        /// <item>Autopilot with no cleared pilot aboard: flown before the rules were on (or with them off), so it counts for
        /// every pilot aboard as if by hand.</item>
        /// </list>
        /// </summary>
        internal static Dictionary<string, PilotStanding> Standings(CrewLog log, uint fromId, string fromName, uint toId, string toName)
        {
            var s = TrafficSettings.Current;
            var map = new Dictionary<string, PilotStanding>();
            PilotStanding Of(string k) => map.TryGetValue(k, out var st) ? st : map[k] = new PilotStanding();
            foreach (var f in log.Flights.Where(f => f.Same(fromId, fromName, toId, toName)).OrderBy(f => f.ArrivedUt))
            {
                var pilots = f.Crew.Where(c => c.Trait == "Pilot").Select(c => c.Name).Distinct().ToList();
                if (pilots.Count == 0) continue;
                var cleared = pilots.Where(p => Of(p).Cleared).ToList();
                var mentor = pilots.Any(p => Of(p).Flights >= s.MentorFlights);
                if (!f.Autopilot || cleared.Count == 0)
                {
                    foreach (var p in pilots) Of(p).Flights++;
                    continue;
                }
                foreach (var p in pilots)
                {
                    var st = Of(p);
                    if (cleared.Contains(p)) st.Flights++;
                    else if (mentor && ++st.TrainingRides >= s.TrainingRides) st.Flights++;
                }
            }
            return map;
        }

        /// <summary>
        /// The data card's lines for a pilot (Kerbin Resources' window): the trips they're cleared for, how often flown,
        /// whether they can train others on each, and the trips they're training on.
        /// </summary>
        internal static IEnumerable<KeyValuePair<string, string>> CardRows(string kerbal)
        {
            var log = KerbinResourcesScenario.Instance?.Log;
            if (log == null || CrewLog.TraitOf(kerbal) != "Pilot") yield break;
            var s = TrafficSettings.Current;
            var cleared = new List<string>();
            var training = new List<string>();
            var trips = log.FlightsOf(kerbal).GroupBy(f => (f.FromId != 0 ? f.FromId.ToString() : f.FromName) + "→" + (f.ToId != 0 ? f.ToId.ToString() : f.ToName));
            foreach (var trip in trips)
            {
                var last = trip.OrderBy(f => f.ArrivedUt).Last();
                if (!Standings(log, last.FromId, last.FromName, last.ToId, last.ToName).TryGetValue(kerbal, out var st)) continue;
                var name = last.FromName + " → " + last.ToName;
                if (st.Cleared) cleared.Add(name + " " + st.Flights + "×" + (st.Flights >= s.MentorFlights ? " (trains others)" : ""));
                else if (st.TrainingRides > 0) training.Add(name + " (" + st.TrainingRides + "/" + s.TrainingRides + " rides)");
            }
            if (!s.RouteQualification) yield break;
            yield return new KeyValuePair<string, string>("Cleared for", cleared.Count > 0 ? string.Join(", ", cleared) : "No routes yet");
            if (training.Count > 0) yield return new KeyValuePair<string, string>("Training", string.Join(", ", training));
        }

        internal const string TrafficControllerJob = "Traffic Controller", ShuttlePilotJob = "Shuttle pilot";

        /// <summary>
        /// The job title for a kerbal on duty (Kerbin Resources asks; null leaves them awaiting orders). Pilots only for now:
        /// <list type="bullet">
        /// <item>At KSC: Traffic Controller (for Kerbin's SOI, standing in for the tracking station).</item>
        /// <item>On a station (KSP's vessel type Station, or a craft routes fly to), in a command part (cupola, pod, cockpit)
        /// of its own parts: Traffic Controller. In a part with no controls (a hitchhiker can, a habitat): no job.</item>
        /// <item>In a ship docked to the station, a craft flying a route, or a ship of their own: Shuttle pilot, if they're in its command part.</item>
        /// </list>
        /// Whether a controller can actually control (a CommNet link, capacity) is decided when the controller rules are built.
        /// </summary>
        internal static string JobOf(DutyShift shift)
        {
            if (shift == null || CrewLog.TraitOf(shift.Kerbal) != "Pilot") return null;
            if (shift.AtKsc) return TrafficControllerJob;
            var post = FindVessel(shift.PostId);
            var saved = post?.protoVessel ?? FindSaved(shift.PostId);
            var command = post != null && post.loaded ? HasCommand(post, shift.SeatPartId) : HasCommand(saved, shift.SeatPartId);
            if (shift.InDockedShip) return command ? ShuttlePilotJob : null;
            var scenario = TrafficScenario.Instance;
            var flying = TrafficScenario.FliesRoute(post);
            var type = post != null ? post.vesselType : saved?.vesselType ?? VesselType.Unknown;
            var station = !flying && (type == VesselType.Station ||
                                      scenario != null && scenario.Routes.Any(r => !r.Finished && (r.TargetId == shift.PostId || r.TargetName == shift.PostName)));
            if (!command) return null;
            return station ? TrafficControllerJob : ShuttlePilotJob;
        }

        private static Vessel FindVessel(uint id)
        {
            if (id == 0 || FlightGlobals.Vessels == null) return null;
            foreach (var v in FlightGlobals.Vessels)
                if (v != null && v.persistentId == id) return v;
            return null;
        }

        /// <summary>A craft's saved record by persistent id, for scenes where the game's craft list isn't filled (the space centre).</summary>
        private static ProtoVessel FindSaved(uint id)
        {
            var vessels = HighLogic.CurrentGame?.flightState?.protoVessels;
            return id == 0 || vessels == null ? null : vessels.FirstOrDefault(pv => pv != null && pv.persistentId == id);
        }

        /// <summary>Is the part with this persistent id a command part (pod, cupola, cockpit, probe core)? A loaded craft's.</summary>
        private static bool HasCommand(Vessel v, uint partId)
        {
            var part = v == null || partId == 0 ? null : v.parts.FirstOrDefault(p => p.persistentId == partId);
            return part != null && part.FindModuleImplementing<ModuleCommand>() != null;
        }

        /// <summary>The same from a saved craft record.</summary>
        private static bool HasCommand(ProtoVessel pv, uint partId)
        {
            var pp = pv == null || partId == 0 ? null : pv.protoPartSnapshots.FirstOrDefault(p => p.persistentId == partId);
            return pp != null && pp.modules.Any(m => m.moduleName == "ModuleCommand");
        }

        /// <summary>
        /// Does this station direct its traffic (clearance into its keep-out zone, the arrival and departure lanes)? It
        /// does with a pilot aboard (one in a ship docked to it counts: it's aboard the station as KSP sees it). Without
        /// one, route craft still come and dock there on their own, kept apart only by their pilots' safety stops.
        /// </summary>
        internal static bool HasTrafficControl(Vessel station, out string why)
        {
            why = "";
            if (station == null) return false;
            foreach (var kerbal in station.GetVesselCrew())
                if (kerbal.trait == "Pilot") return true;
            why = "no pilot aboard " + station.vesselName;
            return false;
        }
    }
}
