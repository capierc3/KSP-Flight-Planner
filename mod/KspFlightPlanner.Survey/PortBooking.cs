using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KspFlightPlanner.Haulers
{
    /// <summary>
    /// Booking a port at the station a hauler is going to (docs/MATH.md §11.14). A port is booked about an hour before the
    /// meeting: a short trip books before it undocks, and doesn't leave without one; a long trip leaves on time and books
    /// on the way in. With no free port when it arrives, it waits on the station's orbit 3 km behind it, on rails, and
    /// comes in once one frees up. A booked port counts as claimed (<see cref="HaulerScenario.ClaimedPorts"/>), so no other
    /// hauler takes it.
    /// </summary>
    internal static class PortBooking
    {
        /// <summary>A port is booked this long before the meeting (game s).</summary>
        internal const double BookLeadS = 3600;

        /// <summary>With no free port, it waits this far behind the station on its orbit (m): out of physics range, so on rails.</summary>
        internal const double TrailM = 3000;

        /// <summary>Not booked yet, or waiting, it looks for a free port this often (real s).</summary>
        private const float RetryS = 5f;

        /// <summary>Waiting longer than this (game s: a day), it says so in the game and on the planner's trip list.</summary>
        internal static double LongWaitS => KSPUtil.dateTimeFormatter.Day;

        /// <summary>It's time to book (an hour or less to the meeting) and it hasn't yet; throttled to every 5 s.</summary>
        internal static bool Due(HaulerRoute route, double now) =>
            route.BookedPortId == 0 && now >= route.MeetUt - BookLeadS && Time.time >= route.NextBookTry;

        /// <summary>
        /// Books a free port on <paramref name="station"/> that one of the ship's free ports fits (or, given
        /// <paramref name="shipPorts"/>, one of those port designs), leaving out ports other haulers have claimed. A port
        /// already booked is kept while it's still free. True when it has one.
        /// </summary>
        internal static bool Try(HaulerRoute route, Vessel ship, Vessel station, IList<string> shipPorts = null)
        {
            route.NextBookTry = Time.time + RetryS;
            if (station == null || ship == null) return false;
            var claimed = HaulerScenario.PortsNotFor(route, station);
            var free = FreePorts(station, ship);
            if (route.BookedPortId != 0 && !claimed.Contains(route.BookedPortId) && free.Any(p => p.Key == route.BookedPortId)) return true;
            if (route.BookedPortId != 0) HaulerScenario.Note(route, "Its booked port at " + station.vesselName + " isn't free to it any more.", false);
            route.BookedPortId = 0;
            var mine = shipPorts ?? HaulerShip.FreePortParts(ship);
            foreach (var p in free)
            {
                if (claimed.Contains(p.Key) || !mine.Any(m => RailsDocking.Fits(m, p.Value))) continue;
                route.BookedPortId = p.Key;
                HaulerScenario.Note(route, "Booked a port at " + station.vesselName + ".", false);
                return true;
            }
            return false;
        }

        /// <summary>
        /// The station's free ports (part id, part name). Loaded, its live ports, the one facing the ship most first (it
        /// comes in from its own side); unloaded, from its saved parts.
        /// </summary>
        private static List<KeyValuePair<uint, string>> FreePorts(Vessel station, Vessel ship)
        {
            if (!station.loaded) return RailsDocking.FreePorts(station);
            var toShip = (Vector3)(ship.GetWorldPos3D() - station.GetWorldPos3D()).normalized;
            return station.FindPartModulesImplementing<ModuleDockingNode>().Where(HaulerShip.IsFree)
                .OrderByDescending(n => Vector3.Dot(n.nodeTransform.forward, toShip))
                .Select(n => new KeyValuePair<uint, string>(n.part.persistentId, n.part.partInfo.name)).ToList();
        }

        /// <summary>
        /// No free port at the meeting: parked on the station's own orbit 3 km behind it (out of physics range, so on
        /// rails, and on the same orbit, so it holds its place without fuel), waiting.
        /// </summary>
        internal static void Wait(HaulerRoute route, Vessel ship, Vessel station, string head)
        {
            var to = station.orbitDriver.orbit;
            var speed = to.getOrbitalVelocityAtUT(Planetarium.GetUniversalTime()).magnitude;
            HaulerShip.FollowOrbit(ship, to, TrailM / speed);
            route.State = RouteState.Waiting;
            route.WaitSince = Planetarium.GetUniversalTime();
            route.ToldLongWait = false;
            HaulerScenario.Note(route, head + "; no free port open to " + HaulerPorts.RolePlural(route.Role) + " at " + station.vesselName + ": waiting " + (TrailM / 1000).ToString("0") + " km behind it till one frees up.", false);
            ScreenMessages.PostScreenMessage(route.VesselName + " is waiting " + (TrailM / 1000).ToString("0") + " km behind " + station.vesselName + " for a free port", 8f,
                ScreenMessageStyle.UPPER_CENTER);
        }

        /// <summary>Every frame for a waiting route: a free port, and it comes in; a day's wait, and it says so (once).</summary>
        internal static void WatchWaiting(HaulerRoute route, double now)
        {
            if (Time.time < route.NextBookTry) return;
            var ship = HaulerShip.Find(route.VesselId);
            var station = HaulerShip.Find(route.TargetId);
            if (ship == null || station == null)
            {
                HaulerScenario.Stop(route, (ship == null ? route.VesselName : route.TargetName) + " is gone.");
                return;
            }
            if (Try(route, ship, station))
            {
                RailsFlyer.ComeIn(route, ship, station, "A port is free at " + station.vesselName + " after waiting " + KSPUtil.PrintTimeCompact(now - route.WaitSince, false));
                return;
            }
            if (!route.ToldLongWait && now - route.WaitSince > LongWaitS)
            {
                route.ToldLongWait = true;
                HaulerScenario.Note(route, "No free port at " + station.vesselName + " for a day: still waiting 3 km behind it.", true);
            }
        }
    }
}
