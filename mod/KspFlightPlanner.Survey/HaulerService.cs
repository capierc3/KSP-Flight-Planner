using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KRPC.Service;
using KRPC.Service.Attributes;

namespace KspFlightPlanner.Haulers
{
    /// <summary>
    /// kRPC service "PlannerHaulers": hand a ship a planned trip and let the game fly it in the background
    /// (<see cref="HaulerScenario"/> does the flying). The planner plans; this only follows the plan.
    /// </summary>
    [KRPCService(Name = "PlannerHaulers", GameScene = GameScene.All)]
    public static class HaulerService
    {
        /// <summary>
        /// Starts <paramref name="vessel"/> on a trip to meet <paramref name="target"/>. <paramref name="burns"/> holds
        /// four numbers per burn, in time order: game time, then prograde, normal and radial Δv in m/s (KSP's node axes);
        /// <paramref name="titles"/> names each burn for the log and screen. At <paramref name="meetUt"/> the ship is parked
        /// beside the target. Ships are named exactly as in the game. Replaces any route the ship already had.
        /// </summary>
        [KRPCProcedure]
        public static string StartRoute(string vessel, string target, IList<double> burns, IList<string> titles, double meetUt)
        {
            var scenario = RequireScenario();
            var ship = HaulerShip.FindByName(vessel);
            var meet = HaulerShip.FindByName(target);
            if (ship == meet) throw new ArgumentException("A ship can't fly to meet itself.");
            if (!HaulerShip.InSpace(ship)) throw new ArgumentException(vessel + " isn't in space (" + ship.situation + "): undock and fly it clear first.");
            if (burns.Count == 0 || burns.Count % 4 != 0) throw new ArgumentException("Burns come as four numbers each: time, prograde, normal, radial.");
            var engine = HaulerShip.MainEngine(ship);
            if (engine == null) throw new ArgumentException(vessel + " has no engine with fuel on board.");

            var route = new HaulerRoute
            {
                VesselId = ship.persistentId,
                VesselName = ship.vesselName,
                TargetId = meet.persistentId,
                TargetName = meet.vesselName,
                MeetUt = meetUt,
            };
            for (var i = 0; i < burns.Count; i += 4)
                route.Burns.Add(new HaulerBurn
                {
                    Ut = burns[i],
                    Prograde = burns[i + 1],
                    Normal = burns[i + 2],
                    Radial = burns[i + 3],
                    Title = titles != null && i / 4 < titles.Count ? titles[i / 4] : "",
                });
            route.Burns.Sort((a, b) => a.Ut.CompareTo(b.Ut));

            var now = Planetarium.GetUniversalTime();
            if (route.Burns[0].Ut < now - 5) throw new ArgumentException("The first burn's time has passed: plan again.");
            if (meetUt < route.Burns[route.Burns.Count - 1].Ut) throw new ArgumentException("The meeting can't come before the last burn.");

            // Flown as planned in KSP's own prediction, does it meet the target? A plan that already misses isn't started.
            var meeting = HaulerShip.PredictMeeting(ship, meet, route.Burns, now, meetUt);
            if (meeting.Problem != null || meeting.GapM > CourseCheck.OffCourseM || meeting.SpeedMs > CourseCheck.OffCourseMs)
                throw new ArgumentException("Flown as planned, KSP's own prediction " + (meeting.Problem ?? "has it meet " + meet.vesselName + " " + meeting + " off") +
                                            ": not started.");

            // Usually started while the player is flying the ship: off rails already, so it's checked again once it's left.
            route.Disturbed = CourseCheck.OffRails(ship) || CourseCheck.OffRails(meet);

            // A ship planned again keeps what happened before on its log.
            var old = scenario.Routes.LastOrDefault(r => r.VesselId == ship.persistentId);
            if (old != null) route.Log.AddRange(old.Log);
            scenario.Routes.RemoveAll(r => r.VesselId == ship.persistentId);
            route.Log.Add((old != null && old.State == RouteState.OffCourse ? "Planned again: " : "Started: ") + route.Burns.Count + " burns, " +
                          route.Burns.Sum(b => b.DeltaV).ToString("0") + " m/s planned, with " + engine + "; " + HaulerShip.Mass(ship).ToString("0.00") +
                          " t; KSP's prediction meets " + meet.vesselName + " within " + meeting);
            scenario.Routes.Add(route);
            ScreenMessages.PostScreenMessage(ship.vesselName + (old != null && old.State == RouteState.OffCourse ? " was planned again and is" : " is") +
                                             " flying to " + meet.vesselName + " on its own", 6f, ScreenMessageStyle.UPPER_CENTER);
            return route.Summary();
        }

        /// <summary>One line per route: who's going where and how far along.</summary>
        [KRPCProcedure]
        public static IList<string> Routes()
        {
            var scenario = HaulerScenario.Instance;
            return scenario == null ? new List<string>() : scenario.Routes.Select(r => r.Summary()).ToList();
        }

        /// <summary>What happened on a ship's route so far, one line per event.</summary>
        [KRPCProcedure]
        public static IList<string> RouteLog(string vessel)
        {
            var route = FindRoute(vessel);
            return route == null ? new List<string>() : new List<string>(route.Log);
        }

        /// <summary>Stops a ship's route where it is (the ship stays on its current orbit). False if it had none.</summary>
        [KRPCProcedure]
        public static bool CancelRoute(string vessel)
        {
            var scenario = HaulerScenario.Instance;
            var route = FindRoute(vessel);
            if (scenario == null || route == null) return false;
            scenario.Routes.Remove(route);
            return true;
        }

        /// <summary>
        /// The off-course routes waiting to be planned again, one line each, tab-separated: ship, target, game time now,
        /// then for the ship and then the target: body, semi-major axis (m), eccentricity, inclination, LAN and argument of
        /// periapsis (degrees), mean anomaly at epoch (radians), epoch (s). KSP's own orbit elements, as the save has them.
        /// </summary>
        [KRPCProcedure]
        public static IList<string> ReplanRequests()
        {
            var list = new List<string>();
            var scenario = HaulerScenario.Instance;
            if (scenario == null) return list;
            var now = Planetarium.GetUniversalTime();
            foreach (var r in scenario.Routes.Where(r => r.State == RouteState.OffCourse))
            {
                var ship = HaulerShip.Find(r.VesselId);
                var target = HaulerShip.Find(r.TargetId);
                if (ship == null || target == null) continue;
                list.Add(string.Join(Tab, new[] { ship.vesselName, target.vesselName, R(now), Elements(ship.orbitDriver.orbit), Elements(target.orbitDriver.orbit) }));
            }
            return list;
        }

        /// <summary>The planner couldn't plan an off-course route again (yet): shown in the game, once per reason.</summary>
        [KRPCProcedure]
        public static void ReplanProblem(string vessel, string why)
        {
            var route = FindRoute(vessel);
            if (route != null && route.State == RouteState.OffCourse) CourseCheck.ReplanProblem(route, why);
        }

        private const string Tab = "\t";

        private static string R(double x) => x.ToString("R", CultureInfo.InvariantCulture);

        private static string Elements(Orbit o) =>
            string.Join(Tab, new[] { o.referenceBody.bodyName, R(o.semiMajorAxis), R(o.eccentricity), R(o.inclination), R(o.LAN), R(o.argumentOfPeriapsis),
                R(o.meanAnomalyAtEpoch), R(o.epoch) });

        /// <summary>Forgets finished and stopped routes.</summary>
        [KRPCProcedure]
        public static int ClearFinished()
        {
            var scenario = HaulerScenario.Instance;
            return scenario == null ? 0 : scenario.Routes.RemoveAll(r => r.State != RouteState.Flying);
        }

        private static HaulerRoute FindRoute(string vessel)
        {
            var scenario = HaulerScenario.Instance;
            return scenario?.Routes.LastOrDefault(r => r.VesselName == vessel);
        }

        private static HaulerScenario RequireScenario()
        {
            var scenario = HaulerScenario.Instance;
            if (scenario == null) throw new InvalidOperationException("Load a save first (in flight, the tracking station or the space centre).");
            return scenario;
        }
    }
}
