using System;
using System.Collections.Generic;
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

            scenario.Routes.RemoveAll(r => r.VesselId == ship.persistentId);
            route.Log.Add("Started: " + route.Burns.Count + " burns, " + route.Burns.Sum(b => b.DeltaV).ToString("0") + " m/s planned, with " + engine +
                          "; " + HaulerShip.Mass(ship).ToString("0.00") + " t");
            scenario.Routes.Add(route);
            ScreenMessages.PostScreenMessage(ship.vesselName + " is flying to " + meet.vesselName + " on its own", 6f, ScreenMessageStyle.UPPER_CENTER);
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
